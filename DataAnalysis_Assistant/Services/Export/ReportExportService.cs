using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DataAnalysis_Assistant.Models;
using Xceed.Document.NET;
using Xceed.Words.NET;
using XColor = Xceed.Drawing.Color;

namespace DataAnalysis_Assistant.Services.Export
{
    /// <summary>
    /// 专业数据分析研报导出引擎 (ReportExportService)
    /// 支持将 Markdown 分析结论、指标事实表与多模态高清统计图表一键导出为标准 Word (.docx) 或 Markdown (.md)
    /// </summary>
    public class ReportExportService
    {
        private static XColor MakeColor(int r, int g, int b) => new XColor(new SkiaSharp.SKColor((byte)r, (byte)g, (byte)b));

        /// <summary>
        /// 异步将研报导出为格式精美的 Word 文档 (.docx)
        /// </summary>
        public async Task<string> ExportToDocxAsync(
            string targetFilePath,
            string title,
            string markdownContent,
            IEnumerable<ChartArtifactItem>? charts = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(targetFilePath))
            {
                throw new ArgumentNullException(nameof(targetFilePath));
            }

            return await Task.Run(() =>
            {
                string dir = Path.GetDirectoryName(targetFilePath) ?? string.Empty;
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using var doc = DocX.Create(targetFilePath);

                // 1. 研报主标题
                var pTitle = doc.InsertParagraph(string.IsNullOrWhiteSpace(title) ? "数据分析与业务洞察综合研报" : title);
                pTitle.FontSize(20);
                pTitle.Bold();
                pTitle.Color(MakeColor(0x2E, 0x28, 0x22));
                pTitle.SpacingAfter(6);

                // 2. 元数据信息栏
                var pMeta = doc.InsertParagraph($"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}   |   分析工具: AI 数据分析助手 (CodeAct 引擎)");
                pMeta.FontSize(9.5);
                pMeta.Color(MakeColor(0x7D, 0x74, 0x6A));
                pMeta.SpacingAfter(16);

                // 分割线
                var pLine = doc.InsertParagraph("_________________________________________________________________________________");
                pLine.Color(MakeColor(0xE2, 0xDD, 0xD0));
                pLine.SpacingAfter(14);

                // 3. 解析并排版正文 Markdown 内容
                var lines = (markdownContent ?? string.Empty).Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                bool inCodeBlock = false;
                var codeLines = new List<string>();
                var tableLines = new List<string>();

                for (int i = 0; i < lines.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string rawLine = lines[i];
                    string line = rawLine.Trim();

                    // 代码块处理
                    if (line.StartsWith("```"))
                    {
                        if (inCodeBlock)
                        {
                            // 结束代码块
                            inCodeBlock = false;
                            if (codeLines.Count > 0)
                            {
                                var pCode = doc.InsertParagraph(string.Join("\n", codeLines));
                                pCode.Font("Consolas");
                                pCode.FontSize(9);
                                pCode.Color(MakeColor(0x33, 0x33, 0x33));
                                pCode.SpacingAfter(8);
                                codeLines.Clear();
                            }
                        }
                        else
                        {
                            inCodeBlock = true;
                            codeLines.Clear();
                        }
                        continue;
                    }

                    if (inCodeBlock)
                    {
                        codeLines.Add(rawLine);
                        continue;
                    }

                    // Markdown 表格处理
                    if (line.StartsWith("|") && line.EndsWith("|"))
                    {
                        tableLines.Add(line);
                        // 若下一行不是表格行，则渲染当前收集的表格
                        if (i + 1 >= lines.Length || !lines[i + 1].Trim().StartsWith("|"))
                        {
                            RenderDocxTable(doc, tableLines);
                            tableLines.Clear();
                        }
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    // 标题等级匹配
                    if (line.StartsWith("# "))
                    {
                        var h1 = doc.InsertParagraph(line.Substring(2).Trim());
                        h1.FontSize(15);
                        h1.Bold();
                        h1.Color(MakeColor(0xB8, 0x5D, 0x19));
                        h1.SpacingBefore(12);
                        h1.SpacingAfter(6);
                    }
                    else if (line.StartsWith("## "))
                    {
                        var h2 = doc.InsertParagraph(line.Substring(3).Trim());
                        h2.FontSize(13);
                        h2.Bold();
                        h2.Color(MakeColor(0x2E, 0x28, 0x22));
                        h2.SpacingBefore(10);
                        h2.SpacingAfter(4);
                    }
                    else if (line.StartsWith("### "))
                    {
                        var h3 = doc.InsertParagraph(line.Substring(4).Trim());
                        h3.FontSize(11.5);
                        h3.Bold();
                        h3.Color(MakeColor(0x4A, 0x40, 0x36));
                        h3.SpacingBefore(8);
                        h3.SpacingAfter(3);
                    }
                    else if (line.StartsWith("- ") || line.StartsWith("* "))
                    {
                        // 列表项
                        var pBullet = doc.InsertParagraph("• " + line.Substring(2).Trim());
                        pBullet.FontSize(10.5);
                        pBullet.IndentationBefore = 14f;
                        pBullet.SpacingAfter(3);
                    }
                    else if (line.StartsWith("> "))
                    {
                        // 引用快讯
                        var pQuote = doc.InsertParagraph(line.Substring(2).Trim());
                        pQuote.Italic();
                        pQuote.FontSize(10);
                        pQuote.Color(MakeColor(0x55, 0x55, 0x55));
                        pQuote.IndentationBefore = 18f;
                        pQuote.SpacingAfter(5);
                    }
                    else
                    {
                        // 普通文本段落
                        var pNormal = doc.InsertParagraph(line);
                        pNormal.FontSize(10.5);
                        pNormal.LineSpacing = 1.15f;
                        pNormal.SpacingAfter(5);
                    }
                }

                // 4. 插入图表附录 (如有)
                var chartList = charts?.Where(c => !string.IsNullOrEmpty(c.FilePath) && File.Exists(c.FilePath)).ToList();
                if (chartList != null && chartList.Count > 0)
                {
                    var pChartHeader = doc.InsertParagraph("\n附录：核心数据可视化图表");
                    pChartHeader.FontSize(14);
                    pChartHeader.Bold();
                    pChartHeader.Color(MakeColor(0xB8, 0x5D, 0x19));
                    pChartHeader.SpacingBefore(16);
                    pChartHeader.SpacingAfter(10);

                    int chartIdx = 1;
                    foreach (var chart in chartList)
                    {
                        try
                        {
                            var img = doc.AddImage(chart.FilePath);
                            var pic = img.CreatePicture();
                            // 限制图片最大展示宽度
                            if (pic.Width > 520)
                            {
                                float ratio = 520f / pic.Width;
                                pic.Width = 520;
                                pic.Height = (int)(pic.Height * ratio);
                            }

                            var pImg = doc.InsertParagraph();
                            pImg.Alignment = Alignment.center;
                            pImg.AppendPicture(pic);
                            pImg.SpacingAfter(4);

                            var pCaption = doc.InsertParagraph($"图 {chartIdx}: {chart.Title} ({chart.FormattedSize})");
                            pCaption.Alignment = Alignment.center;
                            pCaption.FontSize(9.5);
                            pCaption.Italic();
                            pCaption.Color(MakeColor(0x7D, 0x74, 0x6A));
                            pCaption.SpacingAfter(14);

                            chartIdx++;
                        }
                        catch
                        {
                            // 容错处理
                        }
                    }
                }

                doc.Save();
                return targetFilePath;
            }, cancellationToken);
        }

        /// <summary>
        /// 异步将研报导出为标准 Markdown 文件 (.md)
        /// </summary>
        public async Task<string> ExportToMarkdownAsync(
            string targetFilePath,
            string title,
            string markdownContent,
            IEnumerable<ChartArtifactItem>? charts = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(targetFilePath))
            {
                throw new ArgumentNullException(nameof(targetFilePath));
            }

            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"title: \"{(string.IsNullOrWhiteSpace(title) ? "数据分析与业务洞察综合研报" : title)}\"");
            sb.AppendLine($"date: \"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\"");
            sb.AppendLine("generator: \"AI 数据分析助手 (CodeAct 引擎)\"");
            sb.AppendLine("---\n");

            sb.AppendLine($"# {(string.IsNullOrWhiteSpace(title) ? "数据分析与业务洞察综合研报" : title)}\n");
            sb.AppendLine(markdownContent);

            var chartList = charts?.Where(c => !string.IsNullOrEmpty(c.FilePath) && File.Exists(c.FilePath)).ToList();
            if (chartList != null && chartList.Count > 0)
            {
                sb.AppendLine("\n## 附录：数据可视化图表\n");
                int idx = 1;
                foreach (var c in chartList)
                {
                    sb.AppendLine($"### 图 {idx}: {c.Title}");
                    sb.AppendLine($"![{c.Title}]({c.FilePath})\n");
                    idx++;
                }
            }

            string dir = Path.GetDirectoryName(targetFilePath) ?? string.Empty;
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(targetFilePath, sb.ToString(), Encoding.UTF8, cancellationToken);
            return targetFilePath;
        }

        #region 内部表格渲染辅助方法

        private static void RenderDocxTable(DocX doc, List<string> tableLines)
        {
            if (tableLines.Count < 2) return;

            var rowsData = new List<List<string>>();
            foreach (var tLine in tableLines)
            {
                // 跳过分隔行 |:---|:---|
                if (Regex.IsMatch(tLine, @"^\|[\s\-:]+(\|[\s\-:]+)+\|$"))
                {
                    continue;
                }

                var cells = tLine.Split('|')
                                 .Select(c => c.Trim())
                                 .ToList();

                // 移除两端由前导与尾部管道符产生的空项
                if (cells.Count > 0 && string.IsNullOrEmpty(cells[0])) cells.RemoveAt(0);
                if (cells.Count > 0 && string.IsNullOrEmpty(cells[^1])) cells.RemoveAt(cells.Count - 1);

                if (cells.Count > 0)
                {
                    rowsData.Add(cells);
                }
            }

            if (rowsData.Count == 0) return;

            int rowCount = rowsData.Count;
            int colCount = rowsData.Max(r => r.Count);

            try
            {
                var table = doc.AddTable(rowCount, colCount);
                table.Design = TableDesign.LightShadingAccent1;
                table.Alignment = Alignment.center;

                for (int r = 0; r < rowCount; r++)
                {
                    var rowCells = rowsData[r];
                    for (int c = 0; c < colCount; c++)
                    {
                        string text = c < rowCells.Count ? rowCells[c] : string.Empty;
                        var cell = table.Rows[r].Cells[c];
                        cell.Paragraphs[0].Append(text).FontSize(9.5);
                        if (r == 0)
                        {
                            cell.Paragraphs[0].Bold();
                            cell.FillColor = MakeColor(0xEA, 0xE5, 0xDA);
                        }
                    }
                }

                doc.InsertTable(table);
                doc.InsertParagraph().SpacingAfter(8);
            }
            catch
            {
                // 若由于特殊字符表格创建异常，回退为文本块
                foreach (var line in tableLines)
                {
                    doc.InsertParagraph(line).Font("Consolas").FontSize(9);
                }
            }
        }

        #endregion
    }
}
