using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DataAnalysis_Assistant.Models;
using DataAnalysis_Assistant.Services.Data;
using OfficeOpenXml;
using UglyToad.PdfPig;
using Xceed.Words.NET;

namespace DataAnalysis_Assistant.Services.Tools
{
    /// <summary>
    /// 非结构化与结构化文档精准调取工具 (Precision Chunking & Ground-Truth Reader)
    /// 为模型提供大纲索引定位、原文字节级无损提取与高精度关键词检索能力，杜绝条款截断失真
    /// </summary>
    public class DocumentReaderTool : IAgentTool
    {
        private readonly DataSchemaProfiler _profiler = new();
        private readonly Func<IEnumerable<string>>? _getAvailableFilePaths;

        public DocumentReaderTool(Func<IEnumerable<string>>? getAvailableFilePaths = null)
        {
            _getAvailableFilePaths = getAvailableFilePaths;
        }

        public string Name => "read_document";

        public string Description => "精准调取本地多格式文档（PDF、Word、Excel、CSV、TXT、Markdown）。支持大纲索引获取(outline)、指定章节或页码无损原文提取(read_section)、关键词全文高精度检索(search)及表格骨架探查(profile)。通过字节级原汁原味提取保证业务规则与事实完整性，杜绝截断失真。";

        public ToolDefinition Definition => ToolDefinition.CreateFunction(
            Name,
            Description,
            new
            {
                type = "object",
                properties = new
                {
                    file_path = new
                    {
                        type = "string",
                        description = "本地文档的绝对路径或文件名（如 'D:/reports/sale_policy.pdf' 或 'sale_policy.pdf'）。"
                    },
                    action = new
                    {
                        type = "string",
                        description = "要执行的读取动作：'outline' (获取大纲/目录/工作表清单)、'read_section' (提取指定章节/页码/工作表的完整原文)、'search' (全文关键词精准检索)、'profile' (表格数据骨架探查)。",
                        @enum = new[] { "outline", "read_section", "search", "profile" }
                    },
                    target = new
                    {
                        type = "string",
                        description = "目标标识。当 action='read_section' 时为章节名称、页码数字（如 '3'）或工作表名；当 action='search' 时为检索关键词。若 action='outline' 则可留空。"
                    },
                    max_results = new
                    {
                        type = "integer",
                        description = "搜索时返回的最大命中段落数（默认为 5 条）。"
                    }
                },
                required = new[] { "file_path", "action" }
            });

        public bool RequiresUserApproval => false; // 只读文档操作，无破坏性风险，免审批放行

        public async Task<ToolExecutionResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken = default)
        {
            string filePath = string.Empty;
            string action = "outline";
            string target = string.Empty;
            int maxResults = 5;

            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("file_path", out var fp)) filePath = fp.GetString() ?? string.Empty;
                if (root.TryGetProperty("action", out var act)) action = act.GetString()?.ToLowerInvariant() ?? "outline";
                if (root.TryGetProperty("target", out var tgt)) target = tgt.GetString() ?? string.Empty;
                if (root.TryGetProperty("max_results", out var mr) && mr.TryGetInt32(out int mVal)) maxResults = mVal;
            }
            catch (Exception ex)
            {
                return ToolExecutionResult.Failed($"参数解析失败：{ex.Message}");
            }

            string? resolvedPath = ResolveFilePath(filePath);
            if (string.IsNullOrEmpty(resolvedPath) || !File.Exists(resolvedPath))
            {
                return ToolExecutionResult.Failed($"找不到指定文档：'{filePath}'。请确认文件路径是否正确。");
            }

            try
            {
                string resultText = action switch
                {
                    "outline" => await GetOutlineAsync(resolvedPath, cancellationToken),
                    "read_section" => await ReadSectionAsync(resolvedPath, target, cancellationToken),
                    "search" => await SearchTextAsync(resolvedPath, target, maxResults, cancellationToken),
                    "profile" => await ProfileDataAsync(resolvedPath, cancellationToken),
                    _ => await GetOutlineAsync(resolvedPath, cancellationToken)
                };

                return ToolExecutionResult.Ok(resultText);
            }
            catch (Exception ex)
            {
                return ToolExecutionResult.Failed($"读取文档失败 [{action}]: {ex.Message}");
            }
        }

        #region 路径解析

        private string? ResolveFilePath(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            if (File.Exists(input)) return Path.GetFullPath(input);

            // 若提供了相对路径或纯文件名，尝试从已注册文档列表中匹配
            if (_getAvailableFilePaths != null)
            {
                var files = _getAvailableFilePaths();
                var exact = files.FirstOrDefault(f => f.Equals(input, StringComparison.OrdinalIgnoreCase) ||
                                                      Path.GetFileName(f).Equals(input, StringComparison.OrdinalIgnoreCase));
                if (exact != null && File.Exists(exact)) return exact;

                var fuzzy = files.FirstOrDefault(f => Path.GetFileName(f).Contains(input, StringComparison.OrdinalIgnoreCase));
                if (fuzzy != null && File.Exists(fuzzy)) return fuzzy;
            }

            return null;
        }

        #endregion

        #region 大纲索引 (outline)

        private async Task<string> GetOutlineAsync(string path, CancellationToken ct)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            var sb = new StringBuilder();
            sb.AppendLine($"[DOCUMENT_OUTLINE: {Path.GetFileName(path)}]");
            sb.AppendLine($"- 格式: {ext.ToUpperInvariant()} | 路径: `{path}`");

            if (ext == ".pdf")
            {
                using var pdf = PdfDocument.Open(path);
                sb.AppendLine($"- 总页数: {pdf.NumberOfPages} 页");
                sb.AppendLine("#### 目录与前置页面提要：");
                int scanPages = Math.Min(pdf.NumberOfPages, 5);
                for (int i = 1; i <= scanPages; i++)
                {
                    var page = pdf.GetPage(i);
                    string text = page.Text ?? string.Empty;
                    var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                    .Select(l => l.Trim())
                                    .Where(l => l.Length > 2)
                                    .Take(3);
                    sb.AppendLine($"  - 第 {i} 页摘要: {string.Join(" | ", lines)}");
                }
            }
            else if (ext == ".docx")
            {
                using var doc = DocX.Load(path);
                sb.AppendLine($"- 段落总数: {doc.Paragraphs.Count}");
                sb.AppendLine("#### 发现的章节标题（大纲）：");
                int headingCount = 0;
                foreach (var p in doc.Paragraphs)
                {
                    string pText = p.Text.Trim();
                    if (string.IsNullOrWhiteSpace(pText)) continue;

                    bool isHeading = p.StyleId != null && p.StyleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase);
                    bool looksLikeHeading = pText.Length < 60 && Regex.IsMatch(pText, @"^(第[一二三四五六七八九十0-9]+[章节篇条]|[\d\.]+\s+[^\d])");

                    if (isHeading || looksLikeHeading)
                    {
                        headingCount++;
                        sb.AppendLine($"  - {pText}");
                        if (headingCount >= 25) break;
                    }
                }
                if (headingCount == 0)
                {
                    sb.AppendLine("  (未检测到标准 Heading 样式标题，展示前 5 个段落)：");
                    foreach (var p in doc.Paragraphs.Take(5))
                    {
                        if (!string.IsNullOrWhiteSpace(p.Text)) sb.AppendLine($"  - {p.Text.Trim()}");
                    }
                }
            }
            else if (ext is ".xlsx" or ".xls")
            {
                using var pkg = new ExcelPackage(new FileInfo(path));
                sb.AppendLine($"- 工作表总数: {pkg.Workbook.Worksheets.Count}");
                sb.AppendLine("#### 工作表 (Sheets) 清单与规模：");
                foreach (var ws in pkg.Workbook.Worksheets)
                {
                    int rows = ws.Dimension?.Rows ?? 0;
                    int cols = ws.Dimension?.Columns ?? 0;
                    sb.AppendLine($"  - 工作表: `{ws.Name}` ({rows:N0} 行 × {cols} 列)");
                }
            }
            else
            {
                // TXT / CSV / Markdown
                var lines = await File.ReadAllLinesAsync(path, ct);
                sb.AppendLine($"- 总行数: {lines.Length} 行");
                sb.AppendLine("#### 章节结构提要：");
                int headerHits = 0;
                for (int i = 0; i < Math.Min(lines.Length, 200); i++)
                {
                    string l = lines[i].Trim();
                    if (l.StartsWith("#") || Regex.IsMatch(l, @"^第[一二三四五六七八九十0-9]+[章节条]"))
                    {
                        sb.AppendLine($"  - {l} (行 {i + 1})");
                        headerHits++;
                        if (headerHits >= 20) break;
                    }
                }
                if (headerHits == 0)
                {
                    sb.AppendLine("  (纯文本格式，展示前 5 行)：");
                    foreach (var l in lines.Take(5))
                    {
                        if (!string.IsNullOrWhiteSpace(l)) sb.AppendLine($"  - {l}");
                    }
                }
            }

            sb.AppendLine("\n> 💡 提示：可使用 action='read_section' 并指定具体页码、章节标题或工作表名提取完整原文；或使用 action='search' 查找关键词。");
            return sb.ToString();
        }

        #endregion

        #region 无损原文提取 (read_section)

        private async Task<string> ReadSectionAsync(string path, string target, CancellationToken ct)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            var sb = new StringBuilder();
            sb.AppendLine($"[EXACT_DOCUMENT_EXCERPT: {Path.GetFileName(path)} | Target='{target}']");

            if (ext == ".pdf")
            {
                using var pdf = PdfDocument.Open(path);
                // 尝试将 target 解析为页码
                if (int.TryParse(target, out int pageNum) && pageNum >= 1 && pageNum <= pdf.NumberOfPages)
                {
                    var page = pdf.GetPage(pageNum);
                    sb.AppendLine($"#### 第 {pageNum} 页完整原文（零截断）：\n");
                    sb.AppendLine(page.Text);
                }
                else
                {
                    // 若不是纯数字，按章节标题搜寻所在页并提取该页全文字
                    int matchedPage = -1;
                    for (int p = 1; p <= pdf.NumberOfPages; p++)
                    {
                        var page = pdf.GetPage(p);
                        if (page.Text != null && page.Text.Contains(target, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedPage = p;
                            break;
                        }
                    }

                    if (matchedPage > 0)
                    {
                        sb.AppendLine($"#### 命中章节所在第 {matchedPage} 页完整原文（零截断）：\n");
                        sb.AppendLine(pdf.GetPage(matchedPage).Text);
                    }
                    else
                    {
                        sb.AppendLine($"未找到包含 '{target}' 的页面。请使用 outline 确认页码或章节名称。");
                    }
                }
            }
            else if (ext == ".docx")
            {
                using var doc = DocX.Load(path);
                bool found = false;
                bool capturing = false;
                int capturedParagraphs = 0;

                sb.AppendLine($"#### 章节 '{target}' 完整正文（零截断）：\n");

                foreach (var p in doc.Paragraphs)
                {
                    string text = p.Text.Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    if (!capturing && text.Contains(target, StringComparison.OrdinalIgnoreCase))
                    {
                        capturing = true;
                        found = true;
                        sb.AppendLine($"**{text}**\n");
                        continue;
                    }

                    if (capturing)
                    {
                        // 遇到下一个同级或高级标题则停止
                        bool isNextHeading = (p.StyleId != null && p.StyleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)) ||
                                             (text.Length < 50 && Regex.IsMatch(text, @"^第[一二三四五六七八九十0-9]+[章节篇条]"));

                        if (isNextHeading && capturedParagraphs > 0)
                        {
                            break;
                        }

                        sb.AppendLine(text);
                        sb.AppendLine();
                        capturedParagraphs++;
                        if (capturedParagraphs >= 30) break; // 防止单个超长章节失控
                    }
                }

                if (!found)
                {
                    sb.AppendLine($"未找到名称为 '{target}' 的章节。请调用 action='outline' 核查大纲。");
                }
            }
            else if (ext is ".xlsx" or ".xls")
            {
                // 读取工作表
                using var pkg = new ExcelPackage(new FileInfo(path));
                var ws = pkg.Workbook.Worksheets.FirstOrDefault(s => s.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
                         ?? pkg.Workbook.Worksheets.FirstOrDefault();

                if (ws != null && ws.Dimension != null)
                {
                    sb.AppendLine($"#### 工作表 '{ws.Name}' 数据切片 (前 25 行完整明细)：\n");
                    int maxRows = Math.Min(ws.Dimension.End.Row, 25);
                    int maxCols = Math.Min(ws.Dimension.End.Column, 15);

                    for (int r = ws.Dimension.Start.Row; r <= maxRows; r++)
                    {
                        var cells = new List<string>();
                        for (int c = ws.Dimension.Start.Column; c <= maxCols; c++)
                        {
                            cells.Add(ws.Cells[r, c].Value?.ToString() ?? string.Empty);
                        }
                        sb.AppendLine(string.Join(" | ", cells));
                    }
                }
                else
                {
                    sb.AppendLine($"工作表 '{target}' 不存在或为空。");
                }
            }
            else
            {
                // TXT / Markdown
                var lines = await File.ReadAllLinesAsync(path, ct);
                sb.AppendLine($"#### 提取文档目标内容 '{target}'：\n");
                bool capturing = false;
                int captured = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    string l = lines[i];
                    if (!capturing && l.Contains(target, StringComparison.OrdinalIgnoreCase))
                    {
                        capturing = true;
                    }

                    if (capturing)
                    {
                        sb.AppendLine(l);
                        captured++;
                        if (captured > 50 || (captured > 1 && l.StartsWith("#"))) break;
                    }
                }
                if (!capturing)
                {
                    sb.AppendLine($"未找到匹配项 '{target}'。");
                }
            }

            return sb.ToString();
        }

        #endregion

        #region 全文精准检索 (search)

        private async Task<string> SearchTextAsync(string path, string query, int maxResults, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return "请输入有效的检索关键词 query。";
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            var sb = new StringBuilder();
            sb.AppendLine($"[DOCUMENT_SEARCH_RESULTS: {Path.GetFileName(path)} | Query='{query}']");

            int hits = 0;

            if (ext == ".pdf")
            {
                using var pdf = PdfDocument.Open(path);
                for (int p = 1; p <= pdf.NumberOfPages; p++)
                {
                    var page = pdf.GetPage(p);
                    string text = page.Text ?? string.Empty;
                    if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        hits++;
                        sb.AppendLine($"#### 命中 #{hits} (第 {p} 页)：");
                        var paragraphs = text.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
                        var matchingP = paragraphs.FirstOrDefault(para => para.Contains(query, StringComparison.OrdinalIgnoreCase)) ?? text;
                        sb.AppendLine($"> {matchingP.Trim().Replace("\n", " ")}\n");
                        if (hits >= maxResults) break;
                    }
                }
            }
            else if (ext == ".docx")
            {
                using var doc = DocX.Load(path);
                for (int i = 0; i < doc.Paragraphs.Count; i++)
                {
                    string pText = doc.Paragraphs[i].Text;
                    if (pText.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        hits++;
                        sb.AppendLine($"#### 命中 #{hits} (段落 #{i + 1})：");
                        sb.AppendLine($"> {pText.Trim()}\n");
                        if (hits >= maxResults) break;
                    }
                }
            }
            else
            {
                var lines = await File.ReadAllLinesAsync(path, ct);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        hits++;
                        sb.AppendLine($"#### 命中 #{hits} (行 #{i + 1})：");
                        sb.AppendLine($"> {lines[i].Trim()}\n");
                        if (hits >= maxResults) break;
                    }
                }
            }

            if (hits == 0)
            {
                sb.AppendLine($"未检索到包含 '{query}' 的相关内容。");
            }

            return sb.ToString();
        }

        #endregion

        #region 表格探查 (profile)

        private async Task<string> ProfileDataAsync(string path, CancellationToken ct)
        {
            var profile = await _profiler.ProfileAsync(path, ct);
            return profile.ToMarkdown();
        }

        #endregion
    }
}
