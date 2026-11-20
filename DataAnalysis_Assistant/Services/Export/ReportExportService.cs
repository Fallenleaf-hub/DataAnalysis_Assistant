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
    public class ReportExportService
    {
        private const string BodyFont = "Microsoft YaHei";
        private const string CodeFont = "Consolas";

        private static readonly Regex EmojiStripRegex = new(
            @"(?:[\uD800-\uDBFF][\uDC00-\uDFFF]|[\u2600-\u27BF\u2B00-\u2BFF\uFE0F\u200D])",
            RegexOptions.Compiled);

        private static readonly Regex InlineMarkdownRegex = new(
            @"`([^`]+)`|\*\*([^\*]+?)\*\*|__([^_]+?)__|\*([^\*]+?)\*|\[([^\]]+)\]\(([^\)]+)\)",
            RegexOptions.Compiled);

        private static readonly Regex OrderedListRegex = new(
            @"^(\s*)(\d+[\.\)）、])\s+(.+)$",
            RegexOptions.Compiled);

        private static readonly Regex UnorderedListRegex = new(
            @"^(\s*)[-*•]\s+(.+)$",
            RegexOptions.Compiled);

        private static readonly Regex HorizontalRuleRegex = new(
            @"^\s*(?:---+|\*\*\*+|___+)\s*$",
            RegexOptions.Compiled);

        private static readonly Regex NumericOrRangeCellRegex = new(
            @"^(?:[<>≤≥]=?\s*)?[+-]?[\d,\.]+(?:\s*[–\-~]\s*[\d,\.]+)?%?$",
            RegexOptions.Compiled);

        private static XColor MakeColor(int r, int g, int b) => new XColor(new SkiaSharp.SKColor((byte)r, (byte)g, (byte)b));

        public static string SanitizeReportMarkdown(string? rawMarkdown)
        {
            if (string.IsNullOrWhiteSpace(rawMarkdown)) return string.Empty;

            var lines = rawMarkdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var cleaned = new List<string>(lines.Length);
            bool prevBlank = false;

            foreach (string raw in lines)
            {
                if (IsInternalLeakLine(raw))
                {
                    continue;
                }

                string line = EmojiStripRegex.Replace(raw, string.Empty);

                line = Regex.Replace(line, @"^(\s*#{1,6})\s+", "$1 ");
                line = Regex.Replace(line, @"^(\s*>)\s+", "$1 ");
                line = Regex.Replace(line, @"^(\s*[-*•])\s+", "$1 ");
                line = Regex.Replace(line, @"^(\s*\d+[\.\)）、])\s+", "$1 ");
                line = line.TrimEnd();

                if (string.IsNullOrWhiteSpace(line))
                {
                    if (!prevBlank && cleaned.Count > 0)
                    {
                        cleaned.Add(string.Empty);
                    }
                    prevBlank = true;
                }
                else
                {
                    cleaned.Add(line);
                    prevBlank = false;
                }
            }

            while (cleaned.Count > 0 && string.IsNullOrWhiteSpace(cleaned[^1]))
            {
                cleaned.RemoveAt(cleaned.Count - 1);
            }

            return string.Join("\n", cleaned);
        }

        private static bool IsInternalLeakLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;

            return line.Contains("[已生成图表产物记录]", StringComparison.Ordinal) ||
                   line.Contains("[系统历史记录-仅供上下文参考", StringComparison.Ordinal) ||
                   line.Contains("[ARTIFACT_CONFIRMED]", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("[ARTIFACT_CAPTURED]", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("后续分析应以此为基础推进，无需重复绘图", StringComparison.Ordinal) ||
                   line.Contains("后续分析应直接基于既有成果推进，无需重复绘图", StringComparison.Ordinal);
        }

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

                string safeTitle = EmojiStripRegex.Replace(title ?? string.Empty, string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(safeTitle))
                {
                    safeTitle = "数据分析与业务洞察综合研报";
                }

                var colorPrimary = MakeColor(0x2E, 0x28, 0x22);
                var colorAccent = MakeColor(0xB8, 0x5D, 0x19);
                var colorSecondary = MakeColor(0x7D, 0x74, 0x6A);
                var colorHeadingSub = MakeColor(0x4A, 0x40, 0x36);

                var pTitle = doc.InsertParagraph();
                pTitle.Append(safeTitle).Font(BodyFont).FontSize(20).Bold().Color(colorPrimary);
                pTitle.SpacingAfter(6);
                pTitle.KeepWithNextParagraph(true);

                var pMeta = doc.InsertParagraph();
                pMeta.Append($"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}   |   分析工具: AI 数据分析助手 (CodeAct 引擎)")
                     .Font(BodyFont).FontSize(9.5).Color(colorSecondary);
                pMeta.SpacingAfter(10);
                pMeta.KeepWithNextParagraph(true);

                InsertDividerLine(doc);

                string sanitized = SanitizeReportMarkdown(markdownContent);
                var lines = sanitized.Split('\n');
                bool inCodeBlock = false;
                var codeLines = new List<string>();
                var tableLines = new List<string>();

                for (int i = 0; i < lines.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string rawLine = lines[i];
                    string line = rawLine.Trim();

                    if (line.StartsWith("```", StringComparison.Ordinal))
                    {
                        FlushTableIfAny(doc, tableLines);

                        if (inCodeBlock)
                        {
                            inCodeBlock = false;
                            FlushCodeBlockIfAny(doc, codeLines);
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

                    if (line.StartsWith("|", StringComparison.Ordinal) && line.EndsWith("|", StringComparison.Ordinal))
                    {
                        tableLines.Add(line);
                        bool nextIsTable = i + 1 < lines.Length &&
                                           lines[i + 1].Trim().StartsWith("|", StringComparison.Ordinal) &&
                                           lines[i + 1].Trim().EndsWith("|", StringComparison.Ordinal);
                        if (!nextIsTable)
                        {
                            FlushTableIfAny(doc, tableLines);
                        }
                        continue;
                    }

                    FlushTableIfAny(doc, tableLines);

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    if (HorizontalRuleRegex.IsMatch(line))
                    {
                        InsertDividerLine(doc);
                        continue;
                    }

                    if (line.StartsWith("#### ", StringComparison.Ordinal))
                    {
                        InsertHeadingParagraph(doc, line.Substring(5).Trim(), 10.5, colorHeadingSub, 7, 3);
                        continue;
                    }
                    if (line.StartsWith("### ", StringComparison.Ordinal))
                    {
                        InsertHeadingParagraph(doc, line.Substring(4).Trim(), 11.5, colorHeadingSub, 9, 4);
                        continue;
                    }
                    if (line.StartsWith("## ", StringComparison.Ordinal))
                    {
                        InsertHeadingParagraph(doc, line.Substring(3).Trim(), 13, colorPrimary, 11, 5);
                        continue;
                    }
                    if (line.StartsWith("# ", StringComparison.Ordinal))
                    {
                        InsertHeadingParagraph(doc, line.Substring(2).Trim(), 15, colorAccent, 13, 6);
                        continue;
                    }

                    var orderedMatch = OrderedListRegex.Match(rawLine);
                    if (orderedMatch.Success)
                    {
                        int indentLevel = Math.Min(4, orderedMatch.Groups[1].Value.Length / 2);
                        string marker = orderedMatch.Groups[2].Value;
                        string itemText = orderedMatch.Groups[3].Value.Trim();

                        var pOrdered = doc.InsertParagraph();
                        pOrdered.IndentationBefore = 14f + indentLevel * 12f;
                        pOrdered.SpacingLine(16);
                        pOrdered.SpacingAfter(4);
                        AppendRun(pOrdered, marker + " ", 10.5, colorPrimary, BodyFont, bold: true, italic: false);
                        AppendMarkdownInline(pOrdered, itemText, 10.5, colorPrimary);
                        continue;
                    }

                    var unorderedMatch = UnorderedListRegex.Match(rawLine);
                    if (unorderedMatch.Success)
                    {
                        int indentLevel = Math.Min(4, unorderedMatch.Groups[1].Value.Length / 2);
                        string itemText = unorderedMatch.Groups[2].Value.Trim();

                        var pBullet = doc.InsertParagraph();
                        pBullet.IndentationBefore = 14f + indentLevel * 12f;
                        pBullet.SpacingLine(16);
                        pBullet.SpacingAfter(3.5);
                        AppendRun(pBullet, "• ", 10.5, colorPrimary, BodyFont, bold: true, italic: false);
                        AppendMarkdownInline(pBullet, itemText, 10.5, colorPrimary);
                        continue;
                    }

                    if (line.StartsWith(">", StringComparison.Ordinal))
                    {
                        string quoteText = line.Substring(1).Trim();
                        if (!string.IsNullOrWhiteSpace(quoteText))
                        {
                            var pQuote = doc.InsertParagraph();
                            pQuote.IndentationBefore = 18f;
                            pQuote.SpacingLine(15);
                            pQuote.SpacingAfter(5);
                            AppendMarkdownInline(pQuote, quoteText, 10, MakeColor(0x55, 0x55, 0x55), forceItalic: true);
                        }
                        continue;
                    }

                    var pNormal = doc.InsertParagraph();
                    pNormal.SpacingLine(16);
                    pNormal.SpacingAfter(6);
                    AppendMarkdownInline(pNormal, line, 10.5, colorPrimary);
                }

                FlushCodeBlockIfAny(doc, codeLines);
                FlushTableIfAny(doc, tableLines);

                var chartList = charts?.Where(c => !string.IsNullOrEmpty(c.FilePath) && File.Exists(c.FilePath)).ToList();
                if (chartList != null && chartList.Count > 0)
                {
                    var pChartHeader = doc.InsertParagraph();
                    pChartHeader.Append("附录：核心数据可视化图表").Font(BodyFont).FontSize(14).Bold().Color(colorAccent);
                    pChartHeader.SpacingBefore(16);
                    pChartHeader.SpacingAfter(10);
                    pChartHeader.KeepWithNextParagraph(true);

                    float usableWidthPt = Math.Max(300f, doc.PageWidth - doc.MarginLeft - doc.MarginRight);
                    float maxImageWidthPt = Math.Min(430f, usableWidthPt - 16f);
                    const float maxImageHeightPt = 330f;

                    int chartIdx = 1;
                    foreach (var chart in chartList)
                    {
                        try
                        {
                            var img = doc.AddImage(chart.FilePath);
                            var pic = img.CreatePicture();

                            float scale = Math.Min(1f, Math.Min(maxImageWidthPt / Math.Max(1, pic.Width), maxImageHeightPt / Math.Max(1, pic.Height)));
                            if (scale < 1f)
                            {
                                pic.Width = Math.Max(1, (int)Math.Round(pic.Width * scale));
                                pic.Height = Math.Max(1, (int)Math.Round(pic.Height * scale));
                            }

                            var pImg = doc.InsertParagraph();
                            pImg.Alignment = Alignment.center;
                            pImg.IndentationBefore = 0f;
                            pImg.IndentationAfter = 0f;
                            pImg.IndentationFirstLine = 0f;
                            pImg.AppendPicture(pic);
                            pImg.SpacingAfter(4);
                            pImg.KeepWithNextParagraph(true);

                            var pCaption = doc.InsertParagraph();
                            pCaption.Alignment = Alignment.center;
                            pCaption.Append($"图 {chartIdx}: {chart.Title} ({chart.FormattedSize})")
                                    .Font(BodyFont).FontSize(9.5).Italic().Color(colorSecondary);
                            pCaption.SpacingAfter(14);

                            chartIdx++;
                        }
                        catch
                        {
                        }
                    }
                }

                doc.Save();
                return targetFilePath;
            }, cancellationToken);
        }

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

            string safeTitle = EmojiStripRegex.Replace(title ?? string.Empty, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(safeTitle))
            {
                safeTitle = "数据分析与业务洞察综合研报";
            }

            string sanitizedContent = SanitizeReportMarkdown(markdownContent);

            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"title: \"{safeTitle}\"");
            sb.AppendLine($"date: \"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\"");
            sb.AppendLine("generator: \"AI 数据分析助手 (CodeAct 引擎)\"");
            sb.AppendLine("---\n");

            sb.AppendLine($"# {safeTitle}\n");
            sb.AppendLine(sanitizedContent);

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

        #region 段落与行内 Markdown 渲染辅助

        private static void InsertDividerLine(DocX doc)
        {
            var pLine = doc.InsertParagraph();
            pLine.InsertHorizontalLine(
                HorizontalBorderPosition.bottom,
                BorderStyle.Tcbs_single,
                6,
                1,
                MakeColor(0xE2, 0xDD, 0xD0));
            pLine.SpacingAfter(12);
        }

        private static void InsertHeadingParagraph(
            DocX doc,
            string headingText,
            double fontSize,
            XColor color,
            double spacingBefore,
            double spacingAfter)
        {
            var p = doc.InsertParagraph();
            p.SpacingBefore(spacingBefore);
            p.SpacingAfter(spacingAfter);
            p.SpacingLine(15);
            p.KeepWithNextParagraph(true);
            AppendMarkdownInline(p, headingText, fontSize, color, forceBold: true);
        }

        private static void FlushCodeBlockIfAny(DocX doc, List<string> codeLines)
        {
            if (codeLines.Count == 0) return;

            var pCode = doc.InsertParagraph();
            pCode.Append(string.Join("\n", codeLines))
                 .Font(CodeFont)
                 .FontSize(9)
                 .Color(MakeColor(0x33, 0x33, 0x33));
            pCode.IndentationBefore = 12f;
            pCode.SpacingLine(13);
            pCode.SpacingAfter(8);
            codeLines.Clear();
        }

        private static void FlushTableIfAny(DocX doc, List<string> tableLines)
        {
            if (tableLines.Count == 0) return;
            RenderDocxTable(doc, tableLines);
            tableLines.Clear();
        }

        private static void AppendMarkdownInline(
            Paragraph p,
            string text,
            double fontSize,
            XColor textColor,
            bool forceBold = false,
            bool forceItalic = false)
        {
            if (string.IsNullOrEmpty(text)) return;

            var colorCode = MakeColor(0x7A, 0x3E, 0x1D);
            int lastIndex = 0;

            foreach (Match match in InlineMarkdownRegex.Matches(text))
            {
                if (match.Index > lastIndex)
                {
                    AppendRun(p, text.Substring(lastIndex, match.Index - lastIndex), fontSize, textColor, BodyFont, forceBold, forceItalic);
                }

                if (match.Groups[1].Success)
                {
                    AppendRun(p, match.Groups[1].Value, Math.Max(8.5, fontSize - 0.5), colorCode, CodeFont, forceBold, forceItalic);
                }
                else if (match.Groups[2].Success)
                {
                    AppendRun(p, match.Groups[2].Value, fontSize, textColor, BodyFont, bold: true, italic: forceItalic);
                }
                else if (match.Groups[3].Success)
                {
                    AppendRun(p, match.Groups[3].Value, fontSize, textColor, BodyFont, bold: true, italic: forceItalic);
                }
                else if (match.Groups[4].Success)
                {
                    AppendRun(p, match.Groups[4].Value, fontSize, textColor, BodyFont, bold: forceBold, italic: true);
                }
                else if (match.Groups[5].Success)
                {
                    AppendRun(p, match.Groups[5].Value, fontSize, textColor, BodyFont, forceBold, forceItalic);
                }

                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < text.Length)
            {
                AppendRun(p, text.Substring(lastIndex), fontSize, textColor, BodyFont, forceBold, forceItalic);
            }
        }

        private static void AppendRun(
            Paragraph p,
            string text,
            double fontSize,
            XColor color,
            string fontName,
            bool bold,
            bool italic)
        {
            if (string.IsNullOrEmpty(text)) return;

            var run = p.Append(text).Font(fontName).FontSize(fontSize).Color(color);
            if (bold) run.Bold();
            if (italic) run.Italic();
        }

        #endregion

        #region 内部表格渲染辅助方法

        private static void RenderDocxTable(DocX doc, List<string> tableLines)
        {
            if (tableLines.Count < 2)
            {
                foreach (var raw in tableLines)
                {
                    var pFallback = doc.InsertParagraph();
                    pFallback.SpacingLine(16);
                    pFallback.SpacingAfter(4);
                    AppendMarkdownInline(pFallback, raw, 10.5, MakeColor(0x2E, 0x28, 0x22));
                }
                return;
            }

            var rowsData = new List<List<string>>();
            Alignment[]? explicitAlignments = null;

            foreach (var tLine in tableLines)
            {
                if (Regex.IsMatch(tLine, @"^\|?[\s\-:]+(\|[\s\-:]+)+\|?$"))
                {
                    var sepCells = SplitTableCells(tLine);
                    explicitAlignments = sepCells.Select(ParseColumnAlignment).ToArray();
                    continue;
                }

                var cells = SplitTableCells(tLine);
                if (cells.Count > 0)
                {
                    rowsData.Add(cells);
                }
            }

            if (rowsData.Count == 0) return;

            int rowCount = rowsData.Count;
            int colCount = rowsData.Max(r => r.Count);
            if (colCount <= 0) return;

            var colAlignments = ResolveColumnAlignments(rowsData, colCount, explicitAlignments);
            var colWidthPercentages = ComputeIntegerColumnWidthPercentages(rowsData, colCount);

            var colorText = MakeColor(0x2E, 0x28, 0x22);
            var colorHeaderBg = MakeColor(0xEA, 0xE5, 0xDA);
            var colorZebraBg = MakeColor(0xFA, 0xF8, 0xF4);

            try
            {
                var table = doc.AddTable(rowCount, colCount);
                table.Design = TableDesign.TableGrid;
                table.AutoFit = AutoFit.Window;
                table.Alignment = Alignment.center;
                table.SetWidthsPercentage(colWidthPercentages, 450f);

                table.SetTableCellMargin(TableCellMarginType.top, 5);
                table.SetTableCellMargin(TableCellMarginType.bottom, 5);
                table.SetTableCellMargin(TableCellMarginType.left, 7);
                table.SetTableCellMargin(TableCellMarginType.right, 7);

                var outerBorder = new Border(BorderStyle.Tcbs_single, BorderSize.two, 0f, MakeColor(0xB8, 0xAD, 0x9E));
                var innerHBorder = new Border(BorderStyle.Tcbs_single, BorderSize.one, 0f, MakeColor(0xE2, 0xDD, 0xD0));
                var lightVBorder = new Border(BorderStyle.Tcbs_single, BorderSize.one, 0f, MakeColor(0xEA, 0xE5, 0xDA));

                table.SetBorder(TableBorderType.Top, outerBorder);
                table.SetBorder(TableBorderType.Bottom, outerBorder);
                table.SetBorder(TableBorderType.InsideH, innerHBorder);
                table.SetBorder(TableBorderType.Left, lightVBorder);
                table.SetBorder(TableBorderType.Right, lightVBorder);
                table.SetBorder(TableBorderType.InsideV, lightVBorder);

                table.Rows[0].TableHeader = true;

                for (int r = 0; r < rowCount; r++)
                {
                    var row = table.Rows[r];
                    row.BreakAcrossPages = false;
                    var rowCells = rowsData[r];

                    for (int c = 0; c < colCount; c++)
                    {
                        string text = c < rowCells.Count ? rowCells[c] : string.Empty;
                        var cell = row.Cells[c];
                        cell.VerticalAlignment = VerticalAlignment.Center;

                        var pCell = cell.Paragraphs[0];
                        pCell.Alignment = colAlignments[c];
                        pCell.SpacingLine(14);
                        pCell.SpacingBefore(0);
                        pCell.SpacingAfter(0);

                        if (r == 0)
                        {
                            cell.FillColor = colorHeaderBg;
                            AppendMarkdownInline(pCell, text, 9.5, colorText, forceBold: true);
                        }
                        else
                        {
                            if (r % 2 == 0)
                            {
                                cell.FillColor = colorZebraBg;
                            }
                            AppendMarkdownInline(pCell, text, 9.5, colorText);
                        }
                    }
                }

                doc.InsertTable(table);
                doc.InsertParagraph().SpacingAfter(6);
            }
            catch
            {
                foreach (var line in tableLines)
                {
                    var pFallback = doc.InsertParagraph();
                    pFallback.Append(line).Font(CodeFont).FontSize(9);
                }
            }
        }

        private static List<string> SplitTableCells(string tLine)
        {
            var cells = tLine.Split('|')
                             .Select(c => c.Trim())
                             .ToList();

            if (cells.Count > 0 && string.IsNullOrEmpty(cells[0])) cells.RemoveAt(0);
            if (cells.Count > 0 && string.IsNullOrEmpty(cells[^1])) cells.RemoveAt(cells.Count - 1);
            return cells;
        }

        private static Alignment ParseColumnAlignment(string separatorCell)
        {
            string s = separatorCell.Trim();
            bool leftColon = s.StartsWith(":", StringComparison.Ordinal);
            bool rightColon = s.EndsWith(":", StringComparison.Ordinal);

            if (leftColon && rightColon) return Alignment.center;
            if (rightColon) return Alignment.right;
            return Alignment.left;
        }

        private static Alignment[] ResolveColumnAlignments(
            List<List<string>> rowsData,
            int colCount,
            Alignment[]? explicitAlignments)
        {
            var result = new Alignment[colCount];
            for (int c = 0; c < colCount; c++)
            {
                if (explicitAlignments != null && c < explicitAlignments.Length && explicitAlignments[c] != Alignment.left)
                {
                    result[c] = explicitAlignments[c];
                    continue;
                }

                bool hasData = false;
                bool allNumericOrRange = true;
                for (int r = 1; r < rowsData.Count; r++)
                {
                    if (c >= rowsData[r].Count) continue;
                    string val = StripInlineMarkdownTokens(rowsData[r][c]).Trim();
                    if (string.IsNullOrEmpty(val)) continue;

                    hasData = true;
                    if (!NumericOrRangeCellRegex.IsMatch(val))
                    {
                        allNumericOrRange = false;
                        break;
                    }
                }

                result[c] = (hasData && allNumericOrRange) ? Alignment.center : Alignment.left;
            }
            return result;
        }

        private static float[] ComputeIntegerColumnWidthPercentages(List<List<string>> rowsData, int colCount)
        {
            var weights = new int[colCount];
            for (int c = 0; c < colCount; c++)
            {
                int maxVisualWidth = 4;
                for (int r = 0; r < rowsData.Count; r++)
                {
                    if (c >= rowsData[r].Count) continue;
                    string plain = StripInlineMarkdownTokens(rowsData[r][c]);
                    int w = EstimateDisplayWidth(plain);
                    if (w > maxVisualWidth) maxVisualWidth = w;
                }
                weights[c] = Math.Clamp(maxVisualWidth + 4, 6, 40);
            }

            int totalWeight = weights.Sum();
            int[] intPcts = new int[colCount];
            int minPct = Math.Max(8, Math.Min(12, 80 / Math.Max(1, colCount)));

            for (int c = 0; c < colCount; c++)
            {
                int rawPct = (int)Math.Round(weights[c] * 100.0 / totalWeight);
                intPcts[c] = Math.Max(minPct, rawPct);
            }

            int diff = 100 - intPcts.Sum();
            while (diff != 0)
            {
                if (diff > 0)
                {
                    int targetCol = Array.IndexOf(weights, weights.Max());
                    intPcts[targetCol] += 1;
                    diff--;
                }
                else
                {
                    int targetCol = -1;
                    int bestPct = -1;
                    for (int c = 0; c < colCount; c++)
                    {
                        if (intPcts[c] > minPct && intPcts[c] > bestPct)
                        {
                            bestPct = intPcts[c];
                            targetCol = c;
                        }
                    }
                    if (targetCol < 0) targetCol = Array.IndexOf(intPcts, intPcts.Max());
                    intPcts[targetCol] -= 1;
                    diff++;
                }
            }

            return intPcts.Select(x => (float)x).ToArray();
        }

        private static string StripInlineMarkdownTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return InlineMarkdownRegex.Replace(text, m =>
            {
                for (int g = 1; g <= 5; g++)
                {
                    if (m.Groups[g].Success) return m.Groups[g].Value;
                }
                return m.Value;
            });
        }

        private static int EstimateDisplayWidth(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int width = 0;
            foreach (char ch in text)
            {
                width += ch > 127 ? 2 : 1;
            }
            return width;
        }

        #endregion
    }
}
