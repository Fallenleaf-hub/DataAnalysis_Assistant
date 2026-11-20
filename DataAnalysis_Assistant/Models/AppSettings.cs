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

        public string PythonPath { get; set; } = string.Empty;

        /// Python 安全执行模式：0: 严格确认(每次确认), 1: 智能拦截(仅高危操作确认), 2: 自动放行
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
