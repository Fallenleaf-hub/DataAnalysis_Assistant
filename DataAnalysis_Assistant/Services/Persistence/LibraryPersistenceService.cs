using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DataAnalysis_Assistant.Models;

namespace DataAnalysis_Assistant.Services.Persistence
{
    public class LibraryPersistenceService
    {
        private static readonly Lazy<LibraryPersistenceService> _lazyInstance =
            new(() => new LibraryPersistenceService());

        public static LibraryPersistenceService Instance => _lazyInstance.Value;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly SemaphoreSlim _lock = new(1, 1);

        public const string StandaloneGroupId = "group_standalone";

        public string DataDirectory { get; }
        public string GroupsDirectory { get; }
        public string CacheDirectory { get; }
        public string SchemaCacheDirectory => Path.Combine(CacheDirectory, "schema_profiles");

        public LibraryPersistenceService(string? customBasePath = null)
        {
            if (string.IsNullOrWhiteSpace(customBasePath))
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                DataDirectory = Path.Combine(appData, "DataAnalysis_Assistant");
                GroupsDirectory = Path.Combine(DataDirectory, "document_groups");
                CacheDirectory = Path.Combine(DataDirectory, "cache");
            }
            else
            {
                DataDirectory = customBasePath;
                GroupsDirectory = Path.Combine(DataDirectory, "document_groups");
                CacheDirectory = Path.Combine(DataDirectory, "cache");
            }

            EnsureDirectories();
        }

        public void EnsureDirectories()
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(GroupsDirectory);
            Directory.CreateDirectory(CacheDirectory);
            Directory.CreateDirectory(SchemaCacheDirectory);
            EnsureGroupDirectories(StandaloneGroupId);
            CleanupLegacyDefaultGroupFolder();
        }

        private void CleanupLegacyDefaultGroupFolder()
        {
            try
            {
                string defaultGroupDir = Path.Combine(GroupsDirectory, "default_group");
                if (Directory.Exists(defaultGroupDir))
                {
                    string oldSessionsDir = Path.Combine(defaultGroupDir, "sessions");
                    string standaloneSessionsDir = GetGroupSessionsDirectory(StandaloneGroupId);
                    EnsureGroupDirectories(StandaloneGroupId);

                    if (Directory.Exists(oldSessionsDir))
                    {
                        foreach (var sessionFile in Directory.GetFiles(oldSessionsDir, "session_*.json"))
                        {
                            string targetPath = Path.Combine(standaloneSessionsDir, Path.GetFileName(sessionFile));
                            if (!File.Exists(targetPath))
                            {
                                File.Move(sessionFile, targetPath);
                            }
                        }
                    }
                    Directory.Delete(defaultGroupDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LibraryPersistenceService] 清理旧 default_group 目录警告: {ex.Message}");
            }
        }

        #region 0. 组专属目录与路径计算器 (Group Directory Scoping)

        public string GetGroupDirectory(string groupId)
        {
            string safeId = string.IsNullOrWhiteSpace(groupId) ? StandaloneGroupId : groupId.Trim();
            return Path.Combine(GroupsDirectory, safeId);
        }

        public string GetGroupMetaFilePath(string groupId)
        {
            string groupDir = GetGroupDirectory(groupId);
            return Path.Combine(groupDir, "group.json");
        }

        public string GetGroupDocumentsDirectory(string groupId) =>
            Path.Combine(GetGroupDirectory(groupId), "documents");

        public string GetGroupSessionsDirectory(string groupId) =>
            Path.Combine(GetGroupDirectory(groupId), "sessions");

        public string GetGroupScratchDirectory(string groupId) =>
            Path.Combine(GetGroupDirectory(groupId), "scratch");

        public string GetGroupSandboxDirectory(string groupId) =>
            Path.Combine(GetGroupScratchDirectory(groupId), "agent_sandbox");

        public void EnsureGroupDirectories(string groupId)
        {
            Directory.CreateDirectory(GetGroupDirectory(groupId));
            Directory.CreateDirectory(GetGroupDocumentsDirectory(groupId));
            Directory.CreateDirectory(GetGroupSessionsDirectory(groupId));
            Directory.CreateDirectory(GetGroupScratchDirectory(groupId));
            Directory.CreateDirectory(GetGroupSandboxDirectory(groupId));
        }

        public string GetScratchDirectoryForGroup(string? groupId)
        {
            string targetId = string.IsNullOrWhiteSpace(groupId) ? StandaloneGroupId : groupId.Trim();
            EnsureGroupDirectories(targetId);
            return GetGroupScratchDirectory(targetId);
        }

        public string GetSandboxDirectoryForGroup(string? groupId)
        {
            string targetId = string.IsNullOrWhiteSpace(groupId) ? StandaloneGroupId : groupId.Trim();
            EnsureGroupDirectories(targetId);
            return GetGroupSandboxDirectory(targetId);
        }

        #endregion

        #region 1. 文档组 CRUD (DocumentGroup 自治容器)

        public async Task<DocumentGroup> CreateGroupAsync(string groupName, IEnumerable<string>? docIds = null)
        {
            string randomCode = Guid.NewGuid().ToString("N")[..8];
            string groupId = $"group_{DateTime.Now:yyyyMMdd}_{randomCode}";

            var group = new DocumentGroup
            {
                GroupId = groupId,
                GroupName = string.IsNullOrWhiteSpace(groupName) ? "新建文档组" : groupName.Trim(),
                CreatedAt = DateTime.Now,
                LastActiveAt = DateTime.Now
            };

            EnsureGroupDirectories(group.GroupId);

            if (docIds != null)
            {
                var distinctDocIds = docIds.Distinct().ToList();
                group.DocumentIds.AddRange(distinctDocIds);

                foreach (var docId in distinctDocIds)
                {
                    var existingDoc = await LoadDocumentMetaAsync(docId);
                    if (existingDoc != null)
                    {
                        await SaveDocumentMetaInternalAsync(existingDoc, group.GroupId);
                        string standaloneDocPath = Path.Combine(GetGroupDocumentsDirectory(StandaloneGroupId), $"doc_meta_{docId}.json");
                        if (File.Exists(standaloneDocPath))
                        {
                            try { File.Delete(standaloneDocPath); } catch { }
                        }
                    }
                }
            }

            await SaveGroupAsync(group);
            return group;
        }

        public async Task SaveGroupAsync(DocumentGroup group)
        {
            if (group == null || string.IsNullOrWhiteSpace(group.GroupId)) return;

            EnsureGroupDirectories(group.GroupId);
            string filePath = Path.Combine(GetGroupDirectory(group.GroupId), "group.json");
            string json = JsonSerializer.Serialize(group, JsonOptions);

            await _lock.WaitAsync();
            try
            {
                await File.WriteAllTextAsync(filePath, json);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<DocumentGroup?> LoadGroupAsync(string groupId)
        {
            if (string.IsNullOrWhiteSpace(groupId)) return null;

            string filePath = GetGroupMetaFilePath(groupId);
            if (!File.Exists(filePath)) return null;

            await _lock.WaitAsync();
            try
            {
                string json = await File.ReadAllTextAsync(filePath);
                return SafeDeserializeOrQuarantine<DocumentGroup>(filePath, json);
            }
            catch
            {
                return null;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<List<DocumentGroup>> ListAllGroupsAsync()
        {
            var list = new List<DocumentGroup>();
            if (!Directory.Exists(GroupsDirectory)) return list;

            string[] subDirs = Directory.GetDirectories(GroupsDirectory);
            foreach (string dir in subDirs)
            {
                string folderName = Path.GetFileName(dir);
                if (string.Equals(folderName, StandaloneGroupId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string metaPath = Path.Combine(dir, "group.json");
                if (File.Exists(metaPath))
                {
                    try
                    {
                        string json = await File.ReadAllTextAsync(metaPath);
                        var group = SafeDeserializeOrQuarantine<DocumentGroup>(metaPath, json);
                        if (group != null)
                        {
                            list.Add(group);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return list.OrderByDescending(g => g.LastActiveAt).ToList();
        }

        public async Task<bool> DeleteGroupAsync(string groupId)
        {
            if (string.IsNullOrWhiteSpace(groupId)) return false;

            string groupDir = GetGroupDirectory(groupId);
            await _lock.WaitAsync();
            try
            {
                if (Directory.Exists(groupDir))
                {
                    Directory.Delete(groupDir, recursive: true);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LibraryPersistenceService] DeleteGroupAsync 失败: {ex.Message}");
                return false;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task AddDocumentToGroupAsync(string groupId, string docId)
        {
            var group = await LoadGroupAsync(groupId);
            if (group != null && !group.DocumentIds.Contains(docId))
            {
                group.DocumentIds.Add(docId);
                group.LastActiveAt = DateTime.Now;

                var doc = await LoadDocumentMetaAsync(docId);
                if (doc != null)
                {
                    await SaveDocumentMetaInternalAsync(doc, groupId);
                }

                await SaveGroupAsync(group);
            }
        }

        public async Task RemoveDocumentFromGroupAsync(string groupId, string docId)
        {
            var group = await LoadGroupAsync(groupId);
            if (group != null && group.DocumentIds.Remove(docId))
            {
                group.LastActiveAt = DateTime.Now;
                await SaveGroupAsync(group);

                string docMetaPath = Path.Combine(GetGroupDocumentsDirectory(groupId), $"doc_meta_{docId}.json");
                if (File.Exists(docMetaPath))
                {
                    try { File.Delete(docMetaPath); } catch { }
                }
            }
        }

        private string? FindGroupIdForDocument(string docId)
        {
            if (!Directory.Exists(GroupsDirectory)) return null;

            foreach (var dir in Directory.GetDirectories(GroupsDirectory))
            {
                string p = Path.Combine(dir, "documents", $"doc_meta_{docId}.json");
                if (File.Exists(p)) return Path.GetFileName(dir);
            }
            return null;
        }

        #endregion

        #region 2. 文档元数据 CRUD (DocumentItem)

        public async Task SaveDocumentMetaAsync(DocumentItem doc, string? groupId = null)
        {
            if (doc == null || string.IsNullOrWhiteSpace(doc.Id)) return;

            string targetGroup = !string.IsNullOrWhiteSpace(groupId) ? groupId : FindGroupIdForDocument(doc.Id) ?? StandaloneGroupId;
            await SaveDocumentMetaInternalAsync(doc, targetGroup);
        }

        private async Task SaveDocumentMetaInternalAsync(DocumentItem doc, string targetGroupId)
        {
            CheckDocumentFileExists(doc);
            EnsureGroupDirectories(targetGroupId);

            string filePath = Path.Combine(GetGroupDocumentsDirectory(targetGroupId), $"doc_meta_{doc.Id}.json");
            string json = JsonSerializer.Serialize(doc, JsonOptions);

            await _lock.WaitAsync();
            try
            {
                await File.WriteAllTextAsync(filePath, json);
            }
            finally
            {
                _lock.Release();
            }

            if (doc.SchemaProfile != null)
            {
                await SaveSchemaProfileCacheAsync(doc.Id, doc.SchemaProfile);
            }
        }

        public async Task<DocumentItem?> LoadDocumentMetaAsync(string docId, string? groupId = null)
        {
            if (string.IsNullOrWhiteSpace(docId)) return null;

            string? targetFile = null;
            if (!string.IsNullOrWhiteSpace(groupId))
            {
                string p = Path.Combine(GetGroupDocumentsDirectory(groupId), $"doc_meta_{docId}.json");
                if (File.Exists(p)) targetFile = p;
            }

            if (targetFile == null && Directory.Exists(GroupsDirectory))
            {
                foreach (var dir in Directory.GetDirectories(GroupsDirectory))
                {
                    string p = Path.Combine(dir, "documents", $"doc_meta_{docId}.json");
                    if (File.Exists(p))
                    {
                        targetFile = p;
                        break;
                    }
                }
            }

            if (targetFile == null || !File.Exists(targetFile)) return null;

            await _lock.WaitAsync();
            try
            {
                string json = await File.ReadAllTextAsync(targetFile);
                var doc = SafeDeserializeOrQuarantine<DocumentItem>(targetFile, json);
                if (doc != null)
                {
                    CheckDocumentFileExists(doc);
                    if (doc.SchemaProfile == null && doc.IsTabularData)
                    {
                        doc.SchemaProfile = await LoadSchemaProfileCacheInternalAsync(doc.Id);
                    }
                }
                return doc;
            }
            catch
            {
                return null;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// 获取文档清单（若指定 groupId 则返回组内文档，否则返回全部文档并去重）
        public async Task<List<DocumentItem>> ListAllDocumentsAsync(string? groupId = null)
        {
            var list = new List<DocumentItem>();
            var searchDirs = new List<string>();

            if (!string.IsNullOrWhiteSpace(groupId))
            {
                searchDirs.Add(GetGroupDocumentsDirectory(groupId));
            }
            else if (Directory.Exists(GroupsDirectory))
            {
                foreach (var dir in Directory.GetDirectories(GroupsDirectory))
                {
                    string docsDir = Path.Combine(dir, "documents");
                    if (Directory.Exists(docsDir)) searchDirs.Add(docsDir);
                }
            }

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in searchDirs)
            {
                if (!Directory.Exists(dir)) continue;
                string[] files = Directory.GetFiles(dir, "doc_meta_*.json");
                foreach (string file in files)
                {
                    try
                    {
                        string json = await File.ReadAllTextAsync(file);
                        var doc = SafeDeserializeOrQuarantine<DocumentItem>(file, json);
                        if (doc != null && seenIds.Add(doc.Id))
                        {
                            CheckDocumentFileExists(doc);
                            list.Add(doc);
                        }
                    }
                    catch { }
                }
            }

            return list.OrderByDescending(d => d.UploadTime).ToList();
        }

        public async Task<bool> DeleteDocumentMetaAsync(string docId, string? groupId = null)
        {
            if (string.IsNullOrWhiteSpace(docId)) return false;

            bool deleted = false;
            await _lock.WaitAsync();
            try
            {
                var targetFiles = new List<string>();
                if (!string.IsNullOrWhiteSpace(groupId))
                {
                    targetFiles.Add(Path.Combine(GetGroupDocumentsDirectory(groupId), $"doc_meta_{docId}.json"));
                }
                else if (Directory.Exists(GroupsDirectory))
                {
                    foreach (var dir in Directory.GetDirectories(GroupsDirectory))
                    {
                        targetFiles.Add(Path.Combine(dir, "documents", $"doc_meta_{docId}.json"));
                    }
                }

                foreach (var f in targetFiles)
                {
                    if (File.Exists(f))
                    {
                        File.Delete(f);
                        deleted = true;
                    }
                }

                string schemaPath = GetSchemaCacheFilePath(docId);
                if (File.Exists(schemaPath))
                {
                    File.Delete(schemaPath);
                }
                return deleted;
            }
            finally
            {
                _lock.Release();
            }
        }

        public bool CheckDocumentFileExists(DocumentItem doc)
        {
            if (doc == null) return false;
            bool exists = !string.IsNullOrWhiteSpace(doc.Path) && File.Exists(doc.Path);
            doc.IsMissing = !exists;
            return exists;
        }

        #endregion

        #region 3. 会话存储与组内归属 (ChatSessionItem)

        public async Task SaveSessionAsync(ChatSessionItem session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id)) return;

            string targetGroupId = !string.IsNullOrWhiteSpace(session.BindGroupId)
                ? session.BindGroupId.Trim()
                : StandaloneGroupId;

            EnsureGroupDirectories(targetGroupId);
            string filePath = Path.Combine(GetGroupSessionsDirectory(targetGroupId), $"session_{session.Id}.json");
            string json = JsonSerializer.Serialize(session, JsonOptions);

            await _lock.WaitAsync();
            try
            {
                await File.WriteAllTextAsync(filePath, json);

                if (Directory.Exists(GroupsDirectory))
                {
                    foreach (var dir in Directory.GetDirectories(GroupsDirectory))
                    {
                        if (string.Equals(Path.GetFileName(dir), targetGroupId, StringComparison.OrdinalIgnoreCase)) continue;
                        string oldPath = Path.Combine(dir, "sessions", $"session_{session.Id}.json");
                        if (File.Exists(oldPath))
                        {
                            try { File.Delete(oldPath); } catch { }
                        }
                    }
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<ChatSessionItem?> LoadSessionAsync(string sessionId, string? groupId = null)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return null;

            string? targetFile = null;
            if (!string.IsNullOrWhiteSpace(groupId))
            {
                string p = Path.Combine(GetGroupSessionsDirectory(groupId), $"session_{sessionId}.json");
                if (File.Exists(p)) targetFile = p;
            }

            if (targetFile == null && Directory.Exists(GroupsDirectory))
            {
                foreach (var dir in Directory.GetDirectories(GroupsDirectory))
                {
                    string p = Path.Combine(dir, "sessions", $"session_{sessionId}.json");
                    if (File.Exists(p))
                    {
                        targetFile = p;
                        break;
                    }
                }
            }

            if (targetFile == null || !File.Exists(targetFile)) return null;

            await _lock.WaitAsync();
            try
            {
                string json = await File.ReadAllTextAsync(targetFile);
                return SafeDeserializeOrQuarantine<ChatSessionItem>(targetFile, json);
            }
            catch
            {
                return null;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// 获取会话清单（若指定 groupId 则返回组内会话，否则返回全部会话）
        public async Task<List<ChatSessionItem>> ListAllSessionsAsync(string? groupId = null)
        {
            var list = new List<ChatSessionItem>();
            var searchDirs = new List<string>();

            if (!string.IsNullOrWhiteSpace(groupId))
            {
                searchDirs.Add(GetGroupSessionsDirectory(groupId));
            }
            else if (Directory.Exists(GroupsDirectory))
            {
                foreach (var dir in Directory.GetDirectories(GroupsDirectory))
                {
                    string sesDir = Path.Combine(dir, "sessions");
                    if (Directory.Exists(sesDir)) searchDirs.Add(sesDir);
                }
            }

            foreach (var dir in searchDirs)
            {
                if (!Directory.Exists(dir)) continue;
                string[] files = Directory.GetFiles(dir, "session_*.json");
                foreach (string file in files)
                {
                    try
                    {
                        string json = await File.ReadAllTextAsync(file);
                        var session = SafeDeserializeOrQuarantine<ChatSessionItem>(file, json);
                        if (session != null)
                        {
                            list.Add(session);
                        }
                    }
                    catch { }
                }
            }

            return list.OrderByDescending(s => s.LastActiveAt).ToList();
        }

        public async Task<List<ChatSessionItem>> GetSessionsByGroupIdAsync(string groupId)
        {
            if (string.IsNullOrWhiteSpace(groupId)) return new List<ChatSessionItem>();
            return await ListAllSessionsAsync(groupId);
        }

        public async Task<bool> DeleteSessionAsync(string sessionId, string? groupId = null)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return false;

            bool deleted = false;
            await _lock.WaitAsync();
            try
            {
                var targetFiles = new List<string>();
                if (!string.IsNullOrWhiteSpace(groupId))
                {
                    targetFiles.Add(Path.Combine(GetGroupSessionsDirectory(groupId), $"session_{sessionId}.json"));
                }
                else if (Directory.Exists(GroupsDirectory))
                {
                    foreach (var dir in Directory.GetDirectories(GroupsDirectory))
                    {
                        targetFiles.Add(Path.Combine(dir, "sessions", $"session_{sessionId}.json"));
                    }
                }

                foreach (var f in targetFiles)
                {
                    if (File.Exists(f))
                    {
                        File.Delete(f);
                        deleted = true;
                    }
                }
                return deleted;
            }
            finally
            {
                _lock.Release();
            }
        }

        #endregion

        #region 4. 表格结构探查结果全局缓存 (DataSchemaProfile)

        public async Task SaveSchemaProfileCacheAsync(string docId, DataSchemaProfile profile)
        {
            if (string.IsNullOrWhiteSpace(docId) || profile == null) return;

            string filePath = GetSchemaCacheFilePath(docId);
            string json = JsonSerializer.Serialize(profile, JsonOptions);

            try
            {
                await File.WriteAllTextAsync(filePath, json);
            }
            catch { }
        }

        public async Task<DataSchemaProfile?> LoadSchemaProfileCacheAsync(string docId)
        {
            return await LoadSchemaProfileCacheInternalAsync(docId);
        }

        private async Task<DataSchemaProfile?> LoadSchemaProfileCacheInternalAsync(string docId)
        {
            if (string.IsNullOrWhiteSpace(docId)) return null;

            string filePath = GetSchemaCacheFilePath(docId);
            if (!File.Exists(filePath)) return null;

            try
            {
                string json = await File.ReadAllTextAsync(filePath);
                return SafeDeserializeOrQuarantine<DataSchemaProfile>(filePath, json);
            }
            catch
            {
                return null;
            }
        }

        private string GetSchemaCacheFilePath(string docId) =>
            Path.Combine(SchemaCacheDirectory, $"schema_{docId}.json");

        public void DeleteSchemaProfileCache(string docId)
        {
            if (string.IsNullOrWhiteSpace(docId)) return;
            string filePath = GetSchemaCacheFilePath(docId);
            if (File.Exists(filePath))
            {
                try { File.Delete(filePath); } catch { }
            }
        }

        #endregion

        #region 5. 文件去重与辅助检索

        public static string ComputeFileHash(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return string.Empty;

            try
            {
                using var md5 = MD5.Create();
                using var stream = File.OpenRead(filePath);
                byte[] hash = md5.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
            catch
            {
                return string.Empty;
            }
        }

        public async Task<DocumentItem?> FindDocumentByPathAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;

            var allDocs = await ListAllDocumentsAsync();
            return allDocs.FirstOrDefault(d => string.Equals(Path.GetFullPath(d.Path), Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase));
        }

        #endregion

        #region 6. 数据备份导出、恢复与临时清理

        public async Task<bool> ExportBackupAsync(string targetZipPath)
        {
            if (string.IsNullOrWhiteSpace(targetZipPath)) return false;

            await _lock.WaitAsync();
            try
            {
                string? targetDir = Path.GetDirectoryName(targetZipPath);
                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                string tempZip = Path.Combine(Path.GetTempPath(), $"danalysis_backup_{Guid.NewGuid():N}.zip");
                if (File.Exists(tempZip)) File.Delete(tempZip);

                using (var zipStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, false))
                {
                    void AddDirectoryToZip(string dirPath, string baseDir)
                    {
                        if (!Directory.Exists(dirPath)) return;
                        var files = Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories);
                        foreach (var file in files)
                        {
                            if (file.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ||
                                file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            string relPath = Path.GetRelativePath(baseDir, file).Replace('\\', '/');
                            archive.CreateEntryFromFile(file, relPath, CompressionLevel.Optimal);
                        }
                    }

                    AddDirectoryToZip(GroupsDirectory, DataDirectory);
                    AddDirectoryToZip(CacheDirectory, DataDirectory);
                }

                if (File.Exists(targetZipPath))
                {
                    File.Delete(targetZipPath);
                }
                File.Move(tempZip, targetZipPath);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LibraryPersistenceService] ExportBackupAsync 异常: {ex.Message}");
                return false;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<int> ImportBackupAsync(string sourceZipPath)
        {
            if (string.IsNullOrWhiteSpace(sourceZipPath) || !File.Exists(sourceZipPath)) return 0;

            await _lock.WaitAsync();
            try
            {
                EnsureDirectories();
                int restoredCount = 0;

                using (var zipStream = new FileStream(sourceZipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;

                        string entryRelPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        string destinationPath = Path.GetFullPath(Path.Combine(DataDirectory, entryRelPath));
                        if (!destinationPath.StartsWith(Path.GetFullPath(DataDirectory), StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string? parentDir = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                        {
                            Directory.CreateDirectory(parentDir);
                        }

                        entry.ExtractToFile(destinationPath, overwrite: true);
                        restoredCount++;
                    }
                }

                return restoredCount;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LibraryPersistenceService] ImportBackupAsync 异常: {ex.Message}");
                return 0;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<int> CleanTemporaryScratchAsync(TimeSpan olderThan)
        {
            await _lock.WaitAsync();
            try
            {
                int deletedCount = 0;
                DateTime threshold = DateTime.Now - olderThan;

                if (Directory.Exists(CacheDirectory))
                {
                    foreach (var file in Directory.GetFiles(CacheDirectory, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            if (fi.LastWriteTime < threshold)
                            {
                                File.Delete(file);
                                deletedCount++;
                            }
                        }
                        catch { }
                    }
                }

                if (Directory.Exists(GroupsDirectory))
                {
                    foreach (var groupDir in Directory.GetDirectories(GroupsDirectory))
                    {
                        string scratchDir = Path.Combine(groupDir, "scratch");
                        if (Directory.Exists(scratchDir))
                        {
                            foreach (var file in Directory.GetFiles(scratchDir, "*", SearchOption.TopDirectoryOnly))
                            {
                                try
                                {
                                    var fi = new FileInfo(file);
                                    if (fi.LastWriteTime < threshold)
                                    {
                                        File.Delete(file);
                                        deletedCount++;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }

                return deletedCount;
            }
            finally
            {
                _lock.Release();
            }
        }

        #endregion

        #region 7. 损坏熔断辅助

        private static T? SafeDeserializeOrQuarantine<T>(string filePath, string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                QuarantineCorruptedFile(filePath);
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LibraryPersistenceService] JSON 损坏熔断: {filePath}, 原因: {ex.Message}");
                QuarantineCorruptedFile(filePath);
                return null;
            }
        }

        private static void QuarantineCorruptedFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    string bakPath = filePath + ".bak";
                    if (File.Exists(bakPath)) File.Delete(bakPath);
                    File.Move(filePath, bakPath);
                }
            }
            catch { }
        }

        #endregion
    }
}
