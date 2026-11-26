using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using DataAnalysis_Assistant.Models;

namespace DataAnalysis_Assistant.Services.Python
{
    /// Python 代码执行安全刹车服务
    /// 提供内置常驻静态特征扫描与高危风险硬拦截，防止高危系统级恶意/破坏性代码执行
    public class SafetyGateService
    {
        private static readonly (string Pattern, string Description)[] HighRiskRules = new[]
        {
            (@"\bos\.(system|popen|exec[a-z]*|spawn[a-z]*)\s*\(", "调用操作系统原生底层指令或拉起进程 (os.system/popen/exec/spawn)"),
            (@"\b(import\s+subprocess|from\s+subprocess\s+import|subprocess\.)", "拉起外部子进程 (subprocess)"),
            (@"\bshutil\.rmtree\s*\(", "递归删除目录树 (shutil.rmtree)"),
            (@"\bos\.(remove|unlink|rmdir|removedirs)\s*\(", "删除本地文件或文件夹 (os.remove/unlink/rmdir)"),
            (@"\.(unlink|rmdir)\s*\(", "通过路径对象删除本地文件或目录 (unlink/rmdir)"),
            (@"\b(import\s+ctypes|from\s+ctypes\s+import|ctypes\.)", "调用底层 C 原生内存库 (ctypes)"),
            (@"\b(import\s+winreg|from\s+winreg\s+import|winreg\.)", "修改 Windows 注册表 (winreg)"),
            (@"\bformat\s+[c-z]:", "格式化系统驱动器"),
            (@"\bpowershell(\.exe)?\b|\bcmd\.exe\b", "调用系统命令行终端 Shell"),
            (@"\b(import\s+socket|from\s+socket\s+import)\b", "创建底层网络 Socket 通信"),
            (@"\b(rmdir|rd)\s+/s\b|\bdel\s+/f\b", "危险的系统级文件清除指令")
        };

        /// 剥离 Python 单行 # 注释，同时保留单引号/双引号/三引号字符串中的内容与换行符
        private static string StripPythonComments(string code)
        {
            if (string.IsNullOrEmpty(code)) return string.Empty;

            var sb = new StringBuilder(code.Length);
            int i = 0;
            int n = code.Length;
            bool inSingle = false;
            bool inDouble = false;
            bool inTripleSingle = false;
            bool inTripleDouble = false;

            while (i < n)
            {
                char c = code[i];

                if (inTripleSingle)
                {
                    if (c == '\\' && i + 1 < n)
                    {
                        sb.Append(c);
                        sb.Append(code[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (c == '\'' && i + 2 < n && code[i + 1] == '\'' && code[i + 2] == '\'')
                    {
                        sb.Append("'''");
                        inTripleSingle = false;
                        i += 3;
                        continue;
                    }
                    sb.Append(c);
                    i++;
                    continue;
                }

                if (inTripleDouble)
                {
                    if (c == '\\' && i + 1 < n)
                    {
                        sb.Append(c);
                        sb.Append(code[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (c == '"' && i + 2 < n && code[i + 1] == '"' && code[i + 2] == '"')
                    {
                        sb.Append("\"\"\"");
                        inTripleDouble = false;
                        i += 3;
                        continue;
                    }
                    sb.Append(c);
                    i++;
                    continue;
                }

                if (inSingle)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < n)
                    {
                        sb.Append(code[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (c == '\'' || c == '\n')
                    {
                        inSingle = false;
                    }
                    i++;
                    continue;
                }

                if (inDouble)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < n)
                    {
                        sb.Append(code[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (c == '"' || c == '\n')
                    {
                        inDouble = false;
                    }
                    i++;
                    continue;
                }

                if (c == '#')
                {
                    while (i < n && code[i] != '\n' && code[i] != '\r')
                    {
                        i++;
                    }
                    continue;
                }

                if (c == '\'' && i + 2 < n && code[i + 1] == '\'' && code[i + 2] == '\'')
                {
                    sb.Append("'''");
                    inTripleSingle = true;
                    i += 3;
                    continue;
                }

                if (c == '"' && i + 2 < n && code[i + 1] == '"' && code[i + 2] == '"')
                {
                    sb.Append("\"\"\"");
                    inTripleDouble = true;
                    i += 3;
                    continue;
                }

                if (c == '\'')
                {
                    sb.Append(c);
                    inSingle = true;
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    sb.Append(c);
                    inDouble = true;
                    i++;
                    continue;
                }

                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        /// 对拟执行的 Python 代码进行内置高危静态安全性评估（常规分析与绘图默认放行，高危操作硬拦截）
        public SafetyCheckResult InspectCode(string code, int securityMode = 1)
        {
            var result = new SafetyCheckResult();

            if (string.IsNullOrWhiteSpace(code))
            {
                result.Level = CodeRiskLevel.Safe;
                result.IsAllowedAutoRun = true;
                result.RiskReason = "代码通过高危安全扫描。";
                return result;
            }

            string effectiveCode = StripPythonComments(code);

            foreach (var rule in HighRiskRules)
            {
                if (Regex.IsMatch(effectiveCode, rule.Pattern, RegexOptions.IgnoreCase))
                {
                    result.Level = CodeRiskLevel.HighRisk;
                    if (!result.DetectedKeywords.Contains(rule.Description))
                    {
                        result.DetectedKeywords.Add(rule.Description);
                    }
                }
            }

            if (result.Level == CodeRiskLevel.HighRisk)
            {
                result.IsAllowedAutoRun = false;
                result.RiskReason = $"检测到高危系统操作：{string.Join("；", result.DetectedKeywords)}。系统已自动拒绝执行该段 Python 代码。";
            }
            else
            {
                result.Level = CodeRiskLevel.Safe;
                result.IsAllowedAutoRun = true;
                result.RiskReason = "代码通过高危安全扫描。";
            }

            return result;
        }
    }
}
