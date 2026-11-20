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
    public class SkillManagerService
    {
        public static string GetAppSkillsDirectory()
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
                            "本目录为应用专属的自定义技能（Skill）受控存储目录。\r\n" +
                            "规范要求：\r\n" +
                            "1. 每个技能为一个独立子文件夹（例如 nature-figure/）；\r\n" +
                            "2. 文件夹根目录下必须包含带有 YAML Frontmatter 的 SKILL.md 描述文件；\r\n" +
                            "3. 您可以在软件界面中点击【导入技能】选择文件夹一键安装，或直接将技能文件夹拷贝至本目录下。\r\n",
                            Encoding.UTF8);
                    }
                }
                catch { }
            }
            return dir;
        }

        public static string GetDefaultAppSkillsDirectory() => GetAppSkillsDirectory();

        public static string GetPrimarySkillsDirectory(AppSettings? settings = null) => GetAppSkillsDirectory();

        public static List<string> GetUserSkillsDirectories(AppSettings? settings = null)
        {
            return new List<string> { GetAppSkillsDirectory() };
        }

        public async Task<List<SkillItem>> LoadUserInstalledSkillsAsync(
            AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            var results = new List<SkillItem>();
            var scannedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string rootDir = GetAppSkillsDirectory();
            if (!Directory.Exists(rootDir))
            {
                return results;
            }

            var disabledSet = new HashSet<string>(settings.DisabledSkills ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            try
            {
                var subDirs = Directory.GetDirectories(rootDir);
                foreach (var subDir in subDirs)
                {
                    string dirName = Path.GetFileName(subDir);
                    if (dirName.StartsWith(".") || dirName.StartsWith("_"))
                    {
                        continue;
                    }

                    // 标准 Skill 规范：必须包含 SKILL.md
                    string skillMdPath = Path.Combine(subDir, "SKILL.md");
                    if (!File.Exists(skillMdPath))
                    {
                        skillMdPath = Path.Combine(subDir, "skill.md");
                        if (!File.Exists(skillMdPath))
                        {
                            continue;
                        }
                    }

                    var skillItem = await ParseSkillMdAsync(skillMdPath, subDir, rootDir, cancellationToken);
                    if (skillItem != null && !scannedNames.Contains(skillItem.Name))
                    {
                        scannedNames.Add(skillItem.Name);
                        skillItem.IsEnabled = !disabledSet.Contains(skillItem.Name);
                        results.Add(skillItem);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SkillManagerService] 扫描专属目录出错: {ex.Message}");
            }

            return results.OrderBy(s => s.DisplayName).ToList();
        }

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

                string instructions = content;

                if (content.StartsWith("---"))
                {
                    int secondDashIndex = content.IndexOf("---", 3, StringComparison.Ordinal);
                    if (secondDashIndex > 3)
                    {
                        string yamlBlock = content.Substring(3, secondDashIndex - 3).Trim();
                        int bodyStart = secondDashIndex + 3;
                        while (bodyStart < content.Length && (content[bodyStart] == '\r' || content[bodyStart] == '\n'))
                        {
                            bodyStart++;
                        }
                        instructions = content.Substring(bodyStart).Trim();

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
                    description = $"位于用户专属技能目录的自定义扩展: {folderName}";
                }

                return new SkillItem
                {
                    Name = name,
                    DisplayName = name,
                    Description = description,
                    DirectoryPath = directoryPath,
                    SkillMdPath = skillMdPath,
                    SourceLocation = rootDir,
                    FullInstructions = instructions
                };
            }
            catch
            {
                return null;
            }
        }

        public static async Task<string?> GetSkillFullInstructionsAsync(SkillItem skill, CancellationToken cancellationToken = default)
        {
            if (skill == null) return null;
            if (!string.IsNullOrWhiteSpace(skill.FullInstructions))
            {
                return skill.FullInstructions;
            }

            if (!string.IsNullOrEmpty(skill.SkillMdPath) && File.Exists(skill.SkillMdPath))
            {
                try
                {
                    string content = await File.ReadAllTextAsync(skill.SkillMdPath, Encoding.UTF8, cancellationToken);
                    if (content.StartsWith("---"))
                    {
                        int secondDashIndex = content.IndexOf("---", 3, StringComparison.Ordinal);
                        if (secondDashIndex > 3)
                        {
                            int bodyStart = secondDashIndex + 3;
                            while (bodyStart < content.Length && (content[bodyStart] == '\r' || content[bodyStart] == '\n'))
                            {
                                bodyStart++;
                            }
                            skill.FullInstructions = content.Substring(bodyStart).Trim();
                            return skill.FullInstructions;
                        }
                    }
                    skill.FullInstructions = content.Trim();
                    return skill.FullInstructions;
                }
                catch
                {
                }
            }

            return null;
        }

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

        public static async Task<(bool Success, string Message, SkillItem? Item)> ImportSkillFolderAsync(
            string sourceFolderPath,
            AppSettings? settings = null)
        {
            if (string.IsNullOrWhiteSpace(sourceFolderPath) || !Directory.Exists(sourceFolderPath))
            {
                return (false, "指定的来源技能文件夹不存在。", null);
            }

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
