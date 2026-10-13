namespace DataAnalysis_Assistant.Models
{
    public class AppSettings
    {
        public string ApiKey { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = "https://api.openai.com/v1";
        public string ModelName { get; set; } = "gpt-4o";
        public int MaxContextLength { get; set; } = 4096;
        public double Temperature { get; set; } = 0.7;
        public string SystemPrompt { get; set; } = "你是一名专业的数据分析师与文档伴读助手，善于从上传的文档中提炼关键数据、输出格式化 Markdown 表格与分析结论。";

        /// <summary>
        /// 自定义 Python 解释器路径（为空时自动探测环境变量 PATH）
        /// </summary>
        public string PythonPath { get; set; } = string.Empty;

        /// <summary>
        /// Python 安全执行模式：0: 严格确认(每次确认), 1: 智能拦截(仅高危操作确认), 2: 自动放行
        /// </summary>
        public int PythonSecurityMode { get; set; } = 1;

        /// <summary>
        /// 单次 Python 代码执行超时时间（秒，默认 30 秒）
        /// </summary>
        public int PythonTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// 用户禁用的自定义技能名称列表
        /// </summary>
        public System.Collections.Generic.List<string> DisabledSkills { get; set; } = new();

        /// <summary>
        /// 自定义技能根目录路径（若为空则使用默认应用技能目录与系统通用目录）
        /// </summary>
        public string CustomSkillsPath { get; set; } = string.Empty;
    }
}

