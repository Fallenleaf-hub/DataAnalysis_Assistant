using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DataAnalysis_Assistant.Models;

namespace DataAnalysis_Assistant.Services.Skills
{
    /// <summary>
    /// 标准 Skill 规范管理服务
    /// 负责扫描用户目录下的自定义技能、解析 SKILL.md 元数据并控制激活状态
    /// </summary>
    public class SkillManagerService
    {
        /// <summary>
        /// 获取本应用专用的默认标准技能根目录 (%APPDATA%\DataAnalysis_Assistant\skills)
        /// 保证任何普通用户使用时均自动创建并绝对可用
        /// </summary>
        public static string GetDefaultAppSkillsDirectory()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "DataAnalysis_Assistant", "skills");
            if (!Directory.Exists(dir))
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    string readmePath = Path.Combine(dir, "README_技能使用说明.txt");
                    if (!File.Exists(readmePath))
                    {
                        File.WriteAllText(readmePath,
                            "【AI数据分析助手 - 自定义技能扩展目录】\r\n\r\n" +
                            "本目录用于存放符合通用 Skill 规范的技能插件。\r\n" +
                            "规范要求：\r\n" +
                            "1. 每个技能为一个独立子文件夹（例如 my-data-skill/）；\r\n" +
                            "2. 文件夹根目录下必须包含带有 YAML Frontmatter 的 SKILL.md 描述文件；\r\n" +
                            "3. 您可以在软件设置的 Skill 界面中直接点击【导入技能】选择文件夹一键安装。\r\n",
                            Encoding.UTF8);
                    }
                }
                catch { }
            }
            return dir;
        }

        /// <summary>
        /// 获取当前生效的主技能目录（优先使用用户自定义目录，若未设置则使用应用专属目录）
        /// </summary>
        public static string GetPrimarySkillsDirectory(AppSettings? settings = null)
        {
            if (settings != null && !string.IsNullOrWhiteSpace(settings.CustomSkillsPath) && Directory.Exists(settings.CustomSkillsPath))
            {
                return settings.CustomSkillsPath;
            }
            return GetDefaultAppSkillsDirectory();
        }

        /// <summary>
        /// 获取所有标准用户技能搜索根目录（优先用户自定义与应用目录，自动兼容常见外部 AI 生态）
        /// </summary>
        public static List<string> GetUserSkillsDirectories(AppSettings? settings = null)
        {
            var list = new List<string>();
            var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. 用户自定义技能根目录（最高优先级）
            if (settings != null && !string.IsNullOrWhiteSpace(settings.CustomSkillsPath) && Directory.Exists(settings.CustomSkillsPath))
            {
                list.Add(settings.CustomSkillsPath);
                addedPaths.Add(Path.GetFullPath(settings.CustomSkillsPath));
            }

            // 2. 本应用专属默认技能目录（任何电脑环境均开箱即用）
            string defaultDir = GetDefaultAppSkillsDirectory();
            string defaultFull = Path.GetFullPath(defaultDir);
            if (!addedPaths.Contains(defaultFull))
            {
                list.Add(defaultDir);
                addedPaths.Add(defaultFull);
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // 3. 跨工具通用技能根目录 (~/.skills)
            string globalSkills = Path.Combine(userProfile, ".skills");
            if (Directory.Exists(globalSkills) && !addedPaths.Contains(Path.GetFullPath(globalSkills)))
            {
                list.Add(globalSkills);
                addedPaths.Add(Path.GetFullPath(globalSkills));
            }

            // 4. 常见开源生态兼容自动发现（Claude, Agents, Gemini 等，若存在则自动融合）
            string[] candidateEcosphereDirs = new[]
            {
                Path.Combine(userProfile, ".claude", "skills"),
                Path.Combine(userProfile, ".agents", "skills"),
                Path.Combine(userProfile, ".agentic", "skills"),
                Path.Combine(userProfile, ".gemini", "config", "skills"),
                Path.Combine(userProfile, ".gemini", "skills")
            };

            foreach (var cDir in candidateEcosphereDirs)
            {
                if (Directory.Exists(cDir))
                {
                    string full = Path.GetFullPath(cDir);
                    if (!addedPaths.Contains(full))
                    {
                        list.Add(cDir);
                        addedPaths.Add(full);
                    }
                }
            }

            return list;
        }

        /// <summary>
        /// 异步扫描并加载所有用户目录下的标准技能
        /// </summary>
        public async Task<List<SkillItem>> LoadUserInstalledSkillsAsync(
            AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            var results = new List<SkillItem>();
            var scannedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var searchDirs = GetUserSkillsDirectories(settings);
            var disabledSet = new HashSet<string>(settings.DisabledSkills ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            foreach (var rootDir in searchDirs)
            {
                if (!Directory.Exists(rootDir)) continue;

                // 严格安全排除内置能力路径
                if (rootDir.IndexOf("builtin", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                try
                {
                    var subDirs = Directory.GetDirectories(rootDir);
                    foreach (var subDir in subDirs)
                    {
                        string dirName = Path.GetFileName(subDir);
                        // 忽略隐藏或系统文件夹 (如 .system, .git)
                        if (dirName.StartsWith(".") || dirName.StartsWith("_"))
                        {
                            continue;
                        }

                        // 标准 Skill 规范：必须包含 SKILL.md
                        string skillMdPath = Path.Combine(subDir, "SKILL.md");
                        if (!File.Exists(skillMdPath))
                        {
                            // 也尝试小写 skill.md 容错
                            skillMdPath = Path.Combine(subDir, "skill.md");
                            if (!File.Exists(skillMdPath))
                            {
                                continue;
                            }
                        }

                        // 读取并解析 SKILL.md 元数据
                        var skillItem = await ParseSkillMdAsync(skillMdPath, subDir, rootDir, cancellationToken);
                        if (skillItem != null && !scannedNames.Contains(skillItem.Name))
                        {
                            scannedNames.Add(skillItem.Name);
                            // 设定激活状态
                            skillItem.IsEnabled = !disabledSet.Contains(skillItem.Name);
                            results.Add(skillItem);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SkillManagerService] 扫描目录 {rootDir} 出错: {ex.Message}");
                }
            }

            return results.OrderBy(s => s.DisplayName).ToList();
        }

        /// <summary>
        /// 解析标准 SKILL.md 文件中的 YAML frontmatter
        /// </summary>
        private static async Task<SkillItem?> ParseSkillMdAsync(
            string skillMdPath,
            string directoryPath,
            string rootDir,
            CancellationToken cancellationToken)
        {
            try
            {
                string content = await File.ReadAllTextAsync(skillMdPath, Encoding.UTF8, cancellationToken);
                string folderName = Path.GetFileName(directoryPath);

                string name = folderName;
                string description = string.Empty;

                // 解析 YAML Frontmatter (--- 包裹区域)
                if (content.StartsWith("---"))
                {
                    int secondDashIndex = content.IndexOf("---", 3, StringComparison.Ordinal);
                    if (secondDashIndex > 3)
                    {
                        string yamlBlock = content.Substring(3, secondDashIndex - 3).Trim();
                        var yamlLines = yamlBlock.Split('\n');

                        var descBuilder = new StringBuilder();
                        bool inMultiLineDesc = false;

                        foreach (var rawLine in yamlLines)
                        {
                            string line = rawLine.TrimEnd('\r');
                            string trimmed = line.Trim();

                            if (trimmed.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                            {
                                inMultiLineDesc = false;
                                name = trimmed.Substring(5).Trim().Trim('"', '\'');
                            }
                            else if (trimmed.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                            {
                                string afterColon = trimmed.Substring(12).Trim();
                                if (afterColon == ">-" || afterColon == "|" || string.IsNullOrEmpty(afterColon))
                                {
                                    inMultiLineDesc = true;
                                }
                                else
                                {
                                    inMultiLineDesc = false;
                                    description = afterColon.Trim('"', '\'');
                                }
                            }
                            else if (inMultiLineDesc)
                            {
                                if (line.StartsWith("  ") || line.StartsWith("\t") || string.IsNullOrWhiteSpace(line))
                                {
                                    if (!string.IsNullOrWhiteSpace(trimmed))
                                    {
                                        if (descBuilder.Length > 0) descBuilder.Append(' ');
                                        descBuilder.Append(trimmed);
                                    }
                                }
                                else
                                {
                                    inMultiLineDesc = false;
                                }
                            }
                        }

                        if (descBuilder.Length > 0 && string.IsNullOrEmpty(description))
                        {
                            description = descBuilder.ToString();
                        }
                    }
                }

                // 若未在 frontmatter 中提取到说明，从首段 markdown 提炼
                if (string.IsNullOrWhiteSpace(description))
                {
                    var lines = content.Split('\n');
                    foreach (var l in lines)
                    {
                        string tl = l.Trim();
                        if (string.IsNullOrEmpty(tl) || tl.StartsWith("#") || tl.StartsWith("---"))
                        {
                            continue;
                        }
                        description = tl;
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(description))
                {
                    description = $"位于用户技能目录的自定义扩展: {folderName}";
                }

                return new SkillItem
                {
                    Name = name,
                    DisplayName = name,
                    Description = description,
                    DirectoryPath = directoryPath,
                    SkillMdPath = skillMdPath,
                    SourceLocation = rootDir
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 在 Windows 资源管理器中打开指定技能目录
        /// </summary>
        public static void OpenFolderInExplorer(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;
            try
            {
                if (Directory.Exists(folderPath))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folderPath}\"") { UseShellExecute = true });
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// 从指定外部文件夹导入技能到应用主技能目录
        /// </summary>
        public static async Task<(bool Success, string Message, SkillItem? Item)> ImportSkillFolderAsync(
            string sourceFolderPath,
            AppSettings? settings = null)
        {
            if (string.IsNullOrWhiteSpace(sourceFolderPath) || !Directory.Exists(sourceFolderPath))
            {
                return (false, "指定的来源技能文件夹不存在。", null);
            }

            // 校验是否符合标准 Skill 规范（包含 SKILL.md 或 skill.md）
            string skillMd = Path.Combine(sourceFolderPath, "SKILL.md");
            if (!File.Exists(skillMd))
            {
                skillMd = Path.Combine(sourceFolderPath, "skill.md");
                if (!File.Exists(skillMd))
                {
                    return (false, "选中的文件夹不符合标准 Skill 规范：缺少 SKILL.md 核心描述文件。", null);
                }
            }

            string folderName = Path.GetFileName(sourceFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(folderName))
            {
                folderName = "imported_skill_" + DateTime.Now.ToString("yyyyMMddHHmmss");
            }

            string targetBase = GetPrimarySkillsDirectory(settings);
            string targetDir = Path.Combine(targetBase, folderName);

            try
            {
                await CopyDirectoryAsync(sourceFolderPath, targetDir);
                string targetSkillMd = Path.Combine(targetDir, Path.GetFileName(skillMd));
                var parsed = await ParseSkillMdAsync(targetSkillMd, targetDir, targetBase, CancellationToken.None);
                return (true, $"成功导入技能【{parsed?.DisplayName ?? folderName}】至本地技能库！", parsed);
            }
            catch (Exception ex)
            {
                return (false, $"导入技能失败: {ex.Message}", null);
            }
        }

        private static async Task CopyDirectoryAsync(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string dest = Path.Combine(targetDir, Path.GetFileName(file));
                File.Copy(file, dest, true);
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                string dirName = Path.GetFileName(dir);
                if (dirName.StartsWith(".") || dirName.StartsWith("_")) continue;
                string destSub = Path.Combine(targetDir, dirName);
                await CopyDirectoryAsync(dir, destSub);
            }
        }
    }
}

