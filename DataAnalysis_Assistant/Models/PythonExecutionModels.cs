using System;
using System.Collections.Generic;

namespace DataAnalysis_Assistant.Models
{
    /// <summary>
    /// Python 本地环境检测状态
    /// </summary>
    public class PythonEnvironmentStatus
    {
        public bool IsInstalled { get; set; }
        public string PythonPath { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public bool HasPandas { get; set; }
        public bool HasMatplotlib { get; set; }
        public bool HasNumpy { get; set; }
        public bool HasSeaborn { get; set; }
        public bool HasOpenPyXl { get; set; }
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 是否具备基础数据分析与绘图环境 (pandas + matplotlib + numpy)
        /// </summary>
        public bool IsDataAnalysisReady => IsInstalled && HasPandas && HasMatplotlib && HasNumpy;
    }

    /// <summary>
    /// 代码风险等级
    /// </summary>
    public enum CodeRiskLevel
    {
        /// <summary>
        /// 安全：纯数据分析与绘图 (pandas, numpy, matplotlib 等只读计算)
        /// </summary>
        Safe,

        /// <summary>
        /// 中度风险：普通文件写入或依赖网络
        /// </summary>
        Moderate,

        /// <summary>
        /// 高危：操作系统级破坏性指令、格式化、修改注册表、系统命令注入等
        /// </summary>
        HighRisk
    }

    /// <summary>
    /// 安全刹车检查结果
    /// </summary>
    public class SafetyCheckResult
    {
        public CodeRiskLevel Level { get; set; } = CodeRiskLevel.Safe;
        public bool IsAllowedAutoRun { get; set; } = true;
        public string RiskReason { get; set; } = string.Empty;
        public List<string> DetectedKeywords { get; set; } = new();
    }

    /// <summary>
    /// Python 代码执行选项
    /// </summary>
    public class PythonExecutionOptions
    {
        public int TimeoutSeconds { get; set; } = 60;
        public int MaxOutputChars { get; set; } = 4000;
        public int MaxOutputLines { get; set; } = 60;
        public string WorkingDirectory { get; set; } = string.Empty;
        public string OutputArtifactsDirectory { get; set; } = string.Empty;
        public bool IsHeavyWork { get; set; } = false;
    }
}

