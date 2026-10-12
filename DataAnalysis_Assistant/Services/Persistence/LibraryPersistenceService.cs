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
    /// <summary>
    /// 本地持久化文档库与会话管理引擎：
    /// 负责文档元数据 CRUD、文档组管理、会话历史存取、Schema 探查缓存以及物理源文件可达性核验。
    /// 遵循“原始用户文件不复制，仅收容元数据、文本缓存与会话状态”的架构铁律。
    /// </summary>
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

        public string BasePath { get; }
        public string DocumentsDirectory => Path.Combine(BasePath, "documents");
        public string GroupsDirectory => Path.Combine(BasePath, "document_groups");
        public string SessionsDirectory => Path.Combine(BasePath, "sessions");
        public string CacheDirectory => Path.Combine(BasePath, "cache");
        public string SchemaCacheDirectory => Path.Combine(CacheDirectory, "schema_profiles");

        public LibraryPersistenceService(string? customBasePath = null)
        {
            if (string.IsNullOrWhiteSpace(customBasePath))
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                BasePath = Path.Combine(appData, "DataAnalysis_Assistant", "library");
            }
            else
            {
                BasePath = customBasePath;
            }

            EnsureDirectories();
        }

        /// <summary>
        /// 初始化确保持久化层级目录存在
        /// </summary>
        public void EnsureDirectories()
        {
            Directory.CreateDirectory(BasePath);
            Directory.CreateDirectory(DocumentsDirectory);
            Directory.CreateDirectory(GroupsDirectory);
            Directory.CreateDirectory(SessionsDirectory);
            Directory.CreateDirectory(CacheDirectory);
            Directory.CreateDirectory(SchemaCacheDirectory);
        }

        #region 1. 文档元数据 CRUD (DocumentItem)

        /// <summary>
        /// 保存或更新单个文档元数据
        /// </summary>
        public async Task SaveDocumentMetaAsync(DocumentItem doc)
        {
            if (doc == null || string.IsNullOrWhiteSpace(doc.Id)) return;

            CheckDocumentFileExists(doc);

            string filePath = GetDocumentMetaFilePath(doc.Id);
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

            // 若包含 SchemaProfile，自动同步写入 Schema 探查缓存
            if (doc.SchemaProfile != null)
            {
                await SaveSchemaProfileCacheAsync(doc.Id, doc.SchemaProfile);
            }
        }

        /// <summary>
        /// 加载指定 ID 的文档元数据
        /// </summary>
        public async Task<DocumentItem?> LoadDocumentMetaAsync(string docId)
        {
            if (string.IsNullOrWhiteSpace(docId)) return null;

            string filePath = GetDocumentMetaFilePath(docId);
            if (!File.Exists(filePath)) return null;

            await _lock.WaitAsync();
            try
            {
                string json = await File.ReadAllTextAsync(filePath);
                var doc = SafeDeserializeOrQuarantine<DocumentItem>(filePath, json);
                if (doc != null)
                {
                    CheckDocumentFileExists(doc);
                    // 若本身未挂载 SchemaProfile，尝试从本地缓存补充
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

        /// <summary>
        /// 获取所有已持久化的文档元数据清单
        /// </summary>
        public async Task<List<DocumentItem>> ListAllDocumentsAsync()
        {
            var list = new List<DocumentItem>();
            if (!Directory.Exists(DocumentsDirectory)) return list;

            string[] files = Directory.GetFiles(DocumentsDirectory, "doc_meta_*.json");
            foreach (string file in files)
            {
                try
                {
                    string json = await File.ReadAllTextAsync(file);
                    var doc = SafeDeserializeOrQuarantine<DocumentItem>(file, json);
                    if (doc != null)
                    {
                        CheckDocumentFileExists(doc);
                        list.Add(doc);
                    }
                }
                catch
                {
                    // 忽略损坏的单个元数据
                }
            }

            return list.OrderByDescending(d => d.UploadTime).ToList();
        }

        /// <summary>
        /// 删除指定文档元数据（同时清理其 Schema 缓存，但不影响组定义）
        /// </summary>
        public async Task<bool> DeleteDocumentMetaAsync(string docId)
        {
            if (string.IsNullOrWhiteSpace(docId)) return false;

            string metaPath = GetDocumentMetaFilePath(docId);
            string schemaPath = GetSchemaCacheFilePath(docId);

            await _lock.WaitAsync();
            try
            {
                bool deleted = false;
                if (File.Exists(metaPath))
                {
                    File.Delete(metaPath);
                    deleted = true;
                }
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

        /// <summary>
        /// 核验文档对应的物理源文件是否存在，并设置 IsMissing 状态
        /// </summary>
        public bool CheckDocumentFileExists(DocumentItem doc)
        {
            if (doc == null) return false;
            bool exists = !string.IsNullOrWhiteSpace(doc.Path) && File.Exists(doc.Path);
            doc.IsMissing = !exists;
            return exists;
        }

        private string GetDocumentMetaFilePath(string docId) =>
            Path.Combine(DocumentsDirectory, $"doc_meta_{docId}.json");

        #endregion

        #region 2. 文档组 CRUD (DocumentGroup)

        /// <summary>
        /// 快速创建一个新的文档组并持久化
        /// </summary>
        public async Task<DocumentGroup> CreateGroupAsync(string groupName, IEnumerable<string>? docIds = null)
        {
            var group = new DocumentGroup
            {
                GroupName = string.IsNullOrWhiteSpace(groupName) ? "新建文档组" : groupName.Trim(),
                CreatedAt = DateTime.Now,
                LastActiveAt = DateTime.Now
            };

            if (docIds != null)
            {
                group.DocumentIds.AddRange(docIds.Distinct());
            }

            await SaveGroupAsync(group);
            return group;
        }

        /// <summary>
        /// 保存或更新文档组定义
        /// </summary>
        public async Task SaveGroupAsync(DocumentGroup group)
        {
            if (group == null || string.IsNullOrWhiteSpace(group.GroupId)) return;

            string filePath = GetGroupFilePath(group.GroupId);
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

        /// <summary>
        /// 加载指定 ID 的文档组
        /// </summary>
        public async Task<DocumentGroup?> LoadGroupAsync(string groupId)
        {
            if (string.IsNullOrWhiteSpace(groupId)) return null;

            string filePath = GetGroupFilePath(groupId);
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

        /// <summary>
        /// 获取所有已创建的文档组清单
        /// </summary>
        public async Task<List<DocumentGroup>> ListAllGroupsAsync()
        {
            var list = new List<DocumentGroup>();
            if (!Directory.Exists(GroupsDirectory)) return list;

            string[] files = Directory.GetFiles(GroupsDirectory, "group_*.json");
            foreach (string file in files)
            {
                try
                {
                    string json = await File.ReadAllTextAsync(file);
                    var group = SafeDeserializeOrQuarantine<DocumentGroup>(file, json);
                    if (group != null)
                    {
                        list.Add(group);
                    }
                }
                catch
                {
                    // 忽略损坏的单个组定义
                }
            }

            return list.OrderByDescending(g => g.LastActiveAt).ToList();
        }

        /// <summary>
        /// 删除指定文档组（注意：不会删除组内的文档实体）
        /// </summary>
        public async Task<bool> DeleteGroupAsync(string groupId)
        {
            if (string.IsNullOrWhiteSpace(groupId)) return false;

            string filePath = GetGroupFilePath(groupId);
            await _lock.WaitAsync();
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    return true;
                }
                return false;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// 将指定文档加入文档组
        /// </summary>
        public async Task AddDocumentToGroupAsync(string groupId, string docId)
        {
            var group = await LoadGroupAsync(groupId);
            if (group != null && !group.DocumentIds.Contains(docId))
            {
                group.DocumentIds.Add(docId);
                group.LastActiveAt = DateTime.Now;
                await SaveGroupAsync(group);
            }
        }

        /// <summary>
        /// 将指定文档从文档组中移除
        /// </summary>
        public async Task RemoveDocumentFromGroupAsync(string groupId, string docId)
        {
            var group = await LoadGroupAsync(groupId);
            if (group != null && group.DocumentIds.Remove(docId))
            {
                group.LastActiveAt = DateTime.Now;
                await SaveGroupAsync(group);
            }
        }

        private string GetGroupFilePath(string groupId) =>
            Path.Combine(GroupsDirectory, $"group_{groupId}.json");

        #endregion

        #region 3. 会话存储与关联映射 (ChatSessionItem)

        /// <summary>
        /// 保存或增量更新会话
        /// </summary>
        public async Task SaveSessionAsync(ChatSessionItem session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.Id)) return;

            string filePath = GetSessionFilePath(session.Id);
            string json = JsonSerializer.Serialize(session, JsonOptions);

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

        /// <summary>
        /// 加载指定 ID 的会话，完整包含历史消息列表
        /// </summary>
        public async Task<ChatSessionItem?> LoadSessionAsync(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return null;

            string filePath = GetSessionFilePath(sessionId);
            if (!File.Exists(filePath)) return null;

            await _lock.WaitAsync();
            try
            {
                string json = await File.ReadAllTextAsync(filePath);
                return SafeDeserializeOrQuarantine<ChatSessionItem>(filePath, json);
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

        /// <summary>
        /// 获取所有历史会话清单（按最后活跃时间降序）
        /// </summary>
        public async Task<List<ChatSessionItem>> ListAllSessionsAsync()
        {
            var list = new List<ChatSessionItem>();
            if (!Directory.Exists(SessionsDirectory)) return list;

            string[] files = Directory.GetFiles(SessionsDirectory, "session_*.json");
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
                catch
                {
                    // 忽略单个读取失败的会话
                }
            }

            return list.OrderByDescending(s => s.LastActiveAt).ToList();
        }

        /// <summary>
        /// 检索指定文档组关联的所有历史会话
        /// </summary>
        public async Task<List<ChatSessionItem>> GetSessionsByGroupIdAsync(string groupId)
        {
            if (string.IsNullOrWhiteSpace(groupId)) return new List<ChatSessionItem>();

            var allSessions = await ListAllSessionsAsync();
            return allSessions.Where(s => string.Equals(s.BindGroupId, groupId, StringComparison.OrdinalIgnoreCase))
                              .OrderByDescending(s => s.LastActiveAt)
                              .ToList();
        }

        /// <summary>
        /// 删除指定会话
        /// </summary>
        public async Task<bool> DeleteSessionAsync(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return false;

            string filePath = GetSessionFilePath(sessionId);
            await _lock.WaitAsync();
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    return true;
                }
                return false;
            }
            finally
            {
                _lock.Release();
            }
        }

        private string GetSessionFilePath(string sessionId) =>
            Path.Combine(SessionsDirectory, $"session_{sessionId}.json");

        #endregion

        #region 4. 表格结构探查结果本地缓存 (DataSchemaProfile)

        /// <summary>
        /// 缓存大表格的 Schema 探查结果，避免反复进行高耗时全表 I/O 扫描
        /// </summary>
        public async Task SaveSchemaProfileCacheAsync(string docId, DataSchemaProfile profile)
        {
            if (string.IsNullOrWhiteSpace(docId) || profile == null) return;

            string filePath = GetSchemaCacheFilePath(docId);
            string json = JsonSerializer.Serialize(profile, JsonOptions);

            try
            {
                await File.WriteAllTextAsync(filePath, json);
            }
            catch
            {
                // 缓存失败不阻断主流程
            }
        }

        /// <summary>
        /// 读取大表格已缓存的 Schema 探查结果
        /// </summary>
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

        #endregion

        #region 5. 文件去重与哈希辅助

        /// <summary>
        /// 计算本地文件的 MD5 哈希值，用于同内容重复导入去重
        /// </summary>
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

        /// <summary>
        /// 检查指定物理文件路径是否已被持久化，返回已有文档实例（避免重复解析与存储）
        /// </summary>
        public async Task<DocumentItem?> FindDocumentByPathAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;

            var allDocs = await ListAllDocumentsAsync();
            return allDocs.FirstOrDefault(d => string.Equals(Path.GetFullPath(d.Path), Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase));
        }

        #endregion

        #region 6. 数据备份导出、恢复与临时缓存清理

        /// <summary>
        /// 将文档库元数据、文档组、会话历史与缓存完整打包导出为 ZIP 文件
        /// </summary>
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
                    void AddDirectoryToZip(string dirPath)
                    {
                        if (!Directory.Exists(dirPath)) return;
                        var files = Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories);
                        foreach (var file in files)
                        {
                            // 过滤 .bak 和临时锁定文件
                            if (file.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ||
                                file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            string relPath = Path.GetRelativePath(BasePath, file).Replace('\\', '/');
                            archive.CreateEntryFromFile(file, relPath, CompressionLevel.Optimal);
                        }
                    }

                    AddDirectoryToZip(DocumentsDirectory);
                    AddDirectoryToZip(GroupsDirectory);
                    AddDirectoryToZip(SessionsDirectory);
                    AddDirectoryToZip(CacheDirectory);
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

        /// <summary>
        /// 从指定 ZIP 备份文件恢复文档库，解压合并并校验结构
        /// </summary>
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
                        if (string.IsNullOrEmpty(entry.Name)) continue; // 目录项跳过

                        // 防止 Zip Slip 路径穿越
                        string entryRelPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        string destinationPath = Path.GetFullPath(Path.Combine(BasePath, entryRelPath));
                        if (!destinationPath.StartsWith(Path.GetFullPath(BasePath), StringComparison.OrdinalIgnoreCase))
                        {
                            continue; // 非法跳出根目录的 entry，直接跳过
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

        /// <summary>
        /// 临时产物安全清理：仅清理 CacheDirectory 中过期的临时文件与探查结果
        /// 严格保证绝不触碰和删除 DocumentsDirectory、GroupsDirectory 与 SessionsDirectory
        /// </summary>
        public async Task<int> CleanTemporaryScratchAsync(TimeSpan olderThan)
        {
            await _lock.WaitAsync();
            try
            {
                int deletedCount = 0;
                if (!Directory.Exists(CacheDirectory)) return 0;

                DateTime threshold = DateTime.Now - olderThan;
                var files = Directory.GetFiles(CacheDirectory, "*", SearchOption.AllDirectories);

                foreach (var file in files)
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
                    catch
                    {
                        // 忽略被占用的临时文件
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

        #region 7. 文件损坏容错与隔离辅助

        /// <summary>
        /// 安全反序列化 JSON，若遇到空文件或语法损毁文件，优雅熔断并重命名为 .bak 隔离，杜绝启动闪退
        /// </summary>
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
                System.Diagnostics.Debug.WriteLine($"[LibraryPersistenceService] JSON 损坏熔断，已隔离为 .bak: {filePath}, 原因: {ex.Message}");
                QuarantineCorruptedFile(filePath);
                return null;
            }
        }

        /// <summary>
        /// 将损毁的持久化文件迁移重命名为 .bak，避免后续流程再次读盘抛错
        /// </summary>
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
            catch
            {
                // 忽略系统文件锁导致的隔离失败
            }
        }

        #endregion
    }
}

