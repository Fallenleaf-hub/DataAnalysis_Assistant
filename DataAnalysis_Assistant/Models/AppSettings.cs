namespace DataAnalysis_Assistant.Models
{
    public class AppSettings
    {
        public string ApiKey { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = "https://api.openai.com/v1";
        public string ModelName { get; set; } = "gpt-4o";
        public int MaxContextLength { get; set; } = 8192;
        public double Temperature { get; set; } = 0.7;
        public string SystemPrompt { get; set; } = "你是一名专业的数据分析师与文档伴读助手，善于从上传的文档中提炼关键数据、输出格式化 Markdown 表格与分析结论。";
    }
}

