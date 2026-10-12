using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DataAnalysis_Assistant.Models;

namespace DataAnalysis_Assistant.Services.Python
{
    /// <summary>
    /// Python 代码执行安全刹车服务
    /// 提供静态特征扫描与风险阻断，防止潜在恶意代码执行
    /// </summary>
    public class SafetyGateService
    {
        // 高危黑名单正则匹配项（系统命令、文件删除、注册表、底层系统句柄）
        private static readonly (string Pattern, string Description)[] HighRiskRules = new[]
        {
            (@"\bos\.system\b", "调用操作系统原生底层指令 (os.system)"),
            (@"\bsubprocess\b", "拉起外部子进程 (subprocess)"),
            (@"\bshutil\.rmtree\b", "递归删除目录树 (shutil.rmtree)"),
            (@"\bos\.(remove|unlink|rmdir)\b", "删除本地文件或文件夹 (os.remove/unlink)"),
            (@"\bctypes\b", "调用底层 C 原生内存库 (ctypes)"),
            (@"\bwinreg\b", "修改 Windows 注册表 (winreg)"),
            (@"\bformat\s+[c-z]:", "格式化系统驱动器"),
            (@"\bpowershell\b|\bcmd\.exe\b", "调用系统命令行终端 shell"),
            (@"\bimport\s+socket\b", "创建底层网络 Socket 监听"),
            (@"\brmdir\s+/s\b|\bdel\s+/f\b", "危险的系统级文件清除指令")
        };

        // 中度敏感匹配项（可能包含外部网络拉取或未受控动态执行）
        private static readonly (string Pattern, string Description)[] ModerateRules = new[]
        {
            (@"\b(requests|urllib|http\.client)\b", "发起外部 HTTP 网络请求"),
            (@"\bopen\(.+['""][wa\+]['""]\)", "向外部路径写入/覆盖文件")
        };

        /// <summary>
        /// 对拟执行的 Python 代码进行静态安全性评估
        /// </summary>
        /// <param name="code">Python 代码内容</param>
        /// <param name="securityMode">安全模式：0:严格模式, 1:智能拦截, 2:自动放行</param>
        public SafetyCheckResult InspectCode(string code, int securityMode = 1)
        {
            var result = new SafetyCheckResult();

            if (string.IsNullOrWhiteSpace(code))
            {
                result.Level = CodeRiskLevel.Safe;
                result.IsAllowedAutoRun = true;
                return result;
            }

            // 1. 扫描高危规则
            foreach (var rule in HighRiskRules)
            {
                if (Regex.IsMatch(code, rule.Pattern, RegexOptions.IgnoreCase))
                {
                    result.Level = CodeRiskLevel.HighRisk;
                    result.DetectedKeywords.Add(rule.Description);
                }
            }

            // 2. 若无高危，扫描中度敏感规则
            if (result.Level == CodeRiskLevel.Safe)
            {
                foreach (var rule in ModerateRules)
                {
                    if (Regex.IsMatch(code, rule.Pattern, RegexOptions.IgnoreCase))
                    {
                        result.Level = CodeRiskLevel.Moderate;
                        result.DetectedKeywords.Add(rule.Description);
                    }
                }
            }

            // 3. 根据安全模式决定是否允许自动执行 (IsAllowedAutoRun)
            switch (securityMode)
            {
                case 0:
                    // 严格模式：任何代码均必须用户确认审批
                    result.IsAllowedAutoRun = false;
                    result.RiskReason = "【严格安全模式】任何本地 Python 代码执行均需人工确认审批。";
                    break;

                case 2:
                    // 自动放行模式：完全信任本地运行
                    result.IsAllowedAutoRun = true;
                    if (result.Level == CodeRiskLevel.HighRisk)
                    {
                        result.RiskReason = "【高危提示】代码包含敏感操作，但当前处于自动放行模式。";
                    }
                    break;

                case 1:
                default:
                    // 智能拦截模式：高危强行拦截审批，纯数据分析放行
                    if (result.Level == CodeRiskLevel.HighRisk)
                    {
                        result.IsAllowedAutoRun = false;
                        result.RiskReason = $"【高危拦截】检测到潜在风险系统级操作：{string.Join("; ", result.DetectedKeywords)}，需人工许可。";
                    }
                    else if (result.Level == CodeRiskLevel.Moderate)
                    {
                        result.IsAllowedAutoRun = false;
                        result.RiskReason = $"【中危提醒】检测到写文件或网络操作：{string.Join("; ", result.DetectedKeywords)}，需确认。";
                    }
                    else
                    {
                        result.IsAllowedAutoRun = true;
                        result.RiskReason = "代码通过安全扫描，属于纯数据分析/统计绘图范畴。";
                    }
                    break;
            }

            return result;
        }
    }
}

