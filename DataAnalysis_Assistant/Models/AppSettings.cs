namespace DataAnalysis_Assistant.Models
{
    public class AppSettings
    {
        public static readonly System.Collections.Generic.IReadOnlyList<string> DefaultModels = new[]
        {
            "gpt-4o",
            "gpt-4o-mini",
            "claude-3-5-sonnet",
            "deepseek-chat",
            "deepseek-reasoner",
            "qwen3.7-flash"
        };

        public string ApiKey { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = "https://api.openai.com/v1";
        public string ModelName { get; set; } = "gpt-4o";
        public System.Collections.Generic.List<string> AvailableModels { get; set; } = new();
        public int MaxContextLength { get; set; } = 4096;
        public double Temperature { get; set; } = 0.7;
        public string SystemPrompt { get; set; } = "你是一名专业的数据分析师与文档伴读助手，善于从上传的文档中提炼关键数据、输出格式化 Markdown 表格与分析结论。";

        public string PythonPath { get; set; } = string.Empty;

        /// 兼容旧版配置的保留字段（高危代码拦截已改为内置常驻生效，不再通过 UI 切换）
        public int PythonSecurityMode { get; set; } = 1;

        public int PythonTimeoutSeconds { get; set; } = 60;

        public System.Collections.Generic.List<string> DisabledSkills { get; set; } = new();

        public string CustomSkillsPath { get; set; } = string.Empty;

        public int AgentMaxIterations { get; set; } = 12;

        public bool EnableAutoFinalSynthesis { get; set; } = true;

        public string SelectedChartLanguageId { get; set; } = ThemePresetCatalog.DefaultLanguageId;

        public string SelectedChartColorThemeId { get; set; } = ThemePresetCatalog.DefaultColorThemeId;

        public System.Collections.Generic.List<ChartLanguageOption> CustomChartLanguages { get; set; } = new();

        public System.Collections.Generic.List<ChartColorThemeOption> CustomChartColorThemes { get; set; } = new();
    }
}
