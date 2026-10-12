using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DataAnalysis_Assistant.Models
{
    /// <summary>
    /// 数据表格单个字段/列的元数据结构信息
    /// </summary>
    public class ColumnSchemaInfo
    {
        public string Name { get; set; } = string.Empty;
        public string DataType { get; set; } = "String";
        public long NullCount { get; set; }
        public double MissingPercentage { get; set; }
        public long DistinctCount { get; set; }
        public string? Min { get; set; }
        public string? Max { get; set; }
        public double? Mean { get; set; }
        public List<string> SampleValues { get; set; } = new();

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"{Name} ({DataType})");
            if (MissingPercentage > 0)
            {
                sb.Append($" [缺失: {MissingPercentage:F1}%]");
            }
            if (!string.IsNullOrEmpty(Min) && !string.IsNullOrEmpty(Max))
            {
                sb.Append($" [范围: {Min} ~ {Max}]");
            }
            if (SampleValues.Count > 0)
            {
                sb.Append($" 示例: [{string.Join(", ", SampleValues)}]");
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 表格数据完整元数据描述模型（践行“数据不动代码动”计算下推规范）
    /// </summary>
    public class DataSchemaProfile
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string FileType { get; set; } = "csv";
        public string EncodingName { get; set; } = "utf-8";
        public long TotalRows { get; set; }
        public int TotalColumns { get; set; }
        public List<ColumnSchemaInfo> Columns { get; set; } = new();
        public List<string> HeadPreviewHeaders { get; set; } = new();
        public List<List<string>> HeadPreviewRows { get; set; } = new();

        /// <summary>
        /// 针对当前数据格式推荐的 Pandas 极速加载代码
        /// </summary>
        public string PythonLoadCode
        {
            get
            {
                string escapedPath = FilePath.Replace("\\", "/");
                if (FileType.Equals("csv", StringComparison.OrdinalIgnoreCase))
                {
                    return $"import pandas as pd\nimport matplotlib.pyplot as plt\n\n# 全量载入本地数据（数据不动代码动，绝不截断）\ndf = pd.read_csv('{escapedPath}', encoding='{EncodingName}')";
                }
                else if (FileType.Equals("xlsx", StringComparison.OrdinalIgnoreCase) || FileType.Equals("xls", StringComparison.OrdinalIgnoreCase))
                {
                    return $"import pandas as pd\nimport matplotlib.pyplot as plt\n\n# 全量载入 Excel 工作表\ndf = pd.read_excel('{escapedPath}')";
                }
                return $"import pandas as pd\ndf = pd.read_csv('{escapedPath}')";
            }
        }

        /// <summary>
        /// 生成高质量、高密度的 Markdown 格式 Schema 说明（供系统提示词或工具输出）
        /// </summary>
        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"### 📊 数据表骨架视图：{FileName}");
            sb.AppendLine($"- **文件路径**：`{FilePath}`");
            sb.AppendLine($"- **格式/编码**：{FileType.ToUpperInvariant()} ({EncodingName})");
            sb.AppendLine($"- **总规模**：**{TotalRows:N0} 行 × {TotalColumns} 列**（全量数据已完整就绪于本地磁盘）");
            sb.AppendLine();

            sb.AppendLine("#### 1. 字段清单与分布");
            sb.AppendLine("| 序号 | 字段名称 | 推断类型 | 缺失率 | 唯一值数 | 极值范围/均值 | 典型样例取值 |");
            sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- | :--- |");

            for (int i = 0; i < Columns.Count; i++)
            {
                var col = Columns[i];
                string missingStr = col.MissingPercentage > 0 ? $"{col.MissingPercentage:F1}%" : "0%";
                string rangeStr = !string.IsNullOrEmpty(col.Min) && !string.IsNullOrEmpty(col.Max)
                    ? $"{col.Min} ~ {col.Max}"
                    : (col.Mean.HasValue ? $"均值:{col.Mean.Value:F2}" : "-");
                string samples = col.SampleValues.Count > 0 ? string.Join(", ", col.SampleValues) : "-";

                sb.AppendLine($"| {i + 1} | `{col.Name}` | {col.DataType} | {missingStr} | {col.DistinctCount:N0} | {rangeStr} | {samples} |");
            }
            sb.AppendLine();

            if (HeadPreviewRows.Count > 0 && HeadPreviewHeaders.Count > 0)
            {
                sb.AppendLine($"#### 2. 前 {HeadPreviewRows.Count} 行预览数据 (Head Preview)");
                sb.Append("| ");
                foreach (var h in HeadPreviewHeaders)
                {
                    sb.Append($"{h} | ");
                }
                sb.AppendLine();

                sb.Append("| ");
                for (int i = 0; i < HeadPreviewHeaders.Count; i++)
                {
                    sb.Append(":--- | ");
                }
                sb.AppendLine();

                foreach (var row in HeadPreviewRows)
                {
                    sb.Append("| ");
                    foreach (var cell in row)
                    {
                        string safeCell = cell.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
                        sb.Append($"{safeCell} | ");
                    }
                    sb.AppendLine();
                }
                sb.AppendLine();
            }

            sb.AppendLine("#### 3. 建议计算下推代码模板");
            sb.AppendLine("```python");
            sb.AppendLine(PythonLoadCode);
            sb.AppendLine("# 在此编写全量聚合计算与分析逻辑...");
            sb.AppendLine("```");

            return sb.ToString();
        }

        /// <summary>
        /// 生成紧凑 Prompt 注入块（在 Token 预算敏感时使用）
        /// </summary>
        public string ToCompactPrompt()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[GROUND_TRUTH_DATA_SCHEMA: {FileName}]");
            sb.AppendLine($"Path: {FilePath} | Size: {TotalRows:N0} rows, {TotalColumns} cols | Encoding: {EncodingName}");
            sb.AppendLine("Columns:");
            foreach (var col in Columns)
            {
                sb.AppendLine($" - {col.Name} ({col.DataType}): distinct={col.DistinctCount}, nulls={col.NullCount}");
            }
            return sb.ToString();
        }
    }
}

