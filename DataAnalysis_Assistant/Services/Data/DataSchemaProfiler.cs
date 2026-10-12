using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataAnalysis_Assistant.Models;
using OfficeOpenXml;

namespace DataAnalysis_Assistant.Services.Data
{
    /// <summary>
    /// 数据表骨架探查器：实现“数据不动代码动”规范
    /// 深入探查结构化表格元数据，杜绝把原始大表硬塞进 Prompt 导致的截断与失真
    /// </summary>
    public class DataSchemaProfiler
    {
        static DataSchemaProfiler()
        {
            // 初始化 GBK / GB2312 / GB18030 中文编码支持
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            // 确保 EPPlus 授权为非商业个人模式
            ExcelPackage.License.SetNonCommercialPersonal("DataAnalysisAssistant");
        }

        /// <summary>
        /// 异步探查指定表格文件的全量骨架信息
        /// </summary>
        public async Task<DataSchemaProfile> ProfileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                throw new FileNotFoundException("待探查的数据文件不存在", filePath);
            }

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext is ".csv" or ".tsv" or ".txt")
            {
                return await ProfileCsvAsync(filePath, cancellationToken);
            }
            else if (ext is ".xlsx" or ".xls")
            {
                return await Task.Run(() => ProfileExcel(filePath), cancellationToken);
            }
            else
            {
                throw new NotSupportedException($"暂不支持探查格式为 {ext} 的文件骨架");
            }
        }

        #region CSV / TSV 探查引擎

        private async Task<DataSchemaProfile> ProfileCsvAsync(string filePath, CancellationToken cancellationToken)
        {
            var encoding = DetectEncoding(filePath);
            char delimiter = DetectDelimiter(filePath, encoding);

            var profile = new DataSchemaProfile
            {
                FilePath = Path.GetFullPath(filePath),
                FileName = Path.GetFileName(filePath),
                FileType = "csv",
                EncodingName = encoding.WebName
            };

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, true);
            using var reader = new StreamReader(stream, encoding);

            string? headerLine = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(headerLine))
            {
                return profile;
            }

            var headers = ParseCsvLine(headerLine, delimiter);
            if (headers.Count == 0) return profile;

            profile.TotalColumns = headers.Count;
            profile.HeadPreviewHeaders = new List<string>(headers);

            int colCount = headers.Count;
            var trackers = new ColumnStatTracker[colCount];
            for (int i = 0; i < colCount; i++)
            {
                trackers[i] = new ColumnStatTracker(headers[i]);
            }

            long rowCount = 0;
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                rowCount++;

                var cells = ParseCsvLine(line, delimiter);

                // 记录前 5 行作为样例数据
                if (profile.HeadPreviewRows.Count < 5)
                {
                    var previewRow = new List<string>();
                    for (int c = 0; c < colCount; c++)
                    {
                        previewRow.Add(c < cells.Count ? cells[c] : string.Empty);
                    }
                    profile.HeadPreviewRows.Add(previewRow);
                }

                // 更新列统计
                for (int c = 0; c < colCount; c++)
                {
                    string val = c < cells.Count ? cells[c] : string.Empty;
                    trackers[c].AddValue(val);
                }
            }

            profile.TotalRows = rowCount;

            // 组装最终列信息
            for (int i = 0; i < colCount; i++)
            {
                profile.Columns.Add(trackers[i].ToSchemaInfo(rowCount));
            }

            return profile;
        }

        private static Encoding DetectEncoding(string filePath)
        {
            byte[] bom = new byte[4];
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int read = fs.Read(bom, 0, 4);
                if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                {
                    return Encoding.UTF8;
                }
                if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
                {
                    return Encoding.Unicode; // UTF-16LE
                }
                if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
                {
                    return Encoding.BigEndianUnicode;
                }
            }

            // 针对无 BOM 文本，进行 UTF-8 有效性检测
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] buffer = new byte[Math.Min(4096, fs.Length)];
                int count = fs.Read(buffer, 0, buffer.Length);
                if (IsValidUtf8(buffer, count))
                {
                    return new UTF8Encoding(false);
                }
            }
            catch { }

            // 默认回退至兼容中文 Windows Excel 的 GB18030/GBK
            try
            {
                return Encoding.GetEncoding("GB18030");
            }
            catch
            {
                return Encoding.UTF8;
            }
        }

        private static bool IsValidUtf8(byte[] bytes, int length)
        {
            int i = 0;
            while (i < length)
            {
                if (bytes[i] <= 0x7F) { i++; continue; }
                if (bytes[i] >= 0xC2 && bytes[i] <= 0xDF)
                {
                    if (i + 1 >= length || (bytes[i + 1] & 0xC0) != 0x80) return false;
                    i += 2;
                }
                else if (bytes[i] >= 0xE0 && bytes[i] <= 0xEF)
                {
                    if (i + 2 >= length || (bytes[i + 1] & 0xC0) != 0x80 || (bytes[i + 2] & 0xC0) != 0x80) return false;
                    i += 3;
                }
                else if (bytes[i] >= 0xF0 && bytes[i] <= 0xF4)
                {
                    if (i + 3 >= length || (bytes[i + 1] & 0xC0) != 0x80 || (bytes[i + 2] & 0xC0) != 0x80 || (bytes[i + 3] & 0xC0) != 0x80) return false;
                    i += 4;
                }
                else
                {
                    return false;
                }
            }
            return true;
        }

        private static char DetectDelimiter(string filePath, Encoding encoding)
        {
            using var reader = new StreamReader(filePath, encoding);
            string? firstLine = reader.ReadLine();
            if (string.IsNullOrEmpty(firstLine)) return ',';

            int commas = firstLine.Count(c => c == ',');
            int tabs = firstLine.Count(c => c == '\t');
            int semicolons = firstLine.Count(c => c == ';');

            if (tabs > commas && tabs > semicolons) return '\t';
            if (semicolons > commas && semicolons > tabs) return ';';
            return ',';
        }

        private static List<string> ParseCsvLine(string line, char delimiter)
        {
            var result = new List<string>();
            var cur = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '\"')
                    {
                        cur.Append('\"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == delimiter && !inQuotes)
                {
                    result.Add(cur.ToString().Trim());
                    cur.Clear();
                }
                else
                {
                    cur.Append(c);
                }
            }
            result.Add(cur.ToString().Trim());
            return result;
        }

        #endregion

        #region Excel 探查引擎 (EPPlus)

        private DataSchemaProfile ProfileExcel(string filePath)
        {
            var profile = new DataSchemaProfile
            {
                FilePath = Path.GetFullPath(filePath),
                FileName = Path.GetFileName(filePath),
                FileType = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant(),
                EncodingName = "binary"
            };

            using var package = new ExcelPackage(new FileInfo(filePath));
            var ws = package.Workbook.Worksheets.FirstOrDefault(s => s.Dimension != null);
            if (ws == null || ws.Dimension == null)
            {
                return profile;
            }

            int startRow = ws.Dimension.Start.Row;
            int endRow = ws.Dimension.End.Row;
            int startCol = ws.Dimension.Start.Column;
            int endCol = ws.Dimension.End.Column;

            long totalDataRows = Math.Max(0, endRow - startRow);
            int totalCols = endCol - startCol + 1;

            profile.TotalRows = totalDataRows;
            profile.TotalColumns = totalCols;

            // 读取表头
            var headers = new List<string>();
            for (int col = startCol; col <= endCol; col++)
            {
                string headerName = ws.Cells[startRow, col].Value?.ToString()?.Trim() ?? $"Col_{col}";
                headers.Add(headerName);
            }
            profile.HeadPreviewHeaders = new List<string>(headers);

            var trackers = new ColumnStatTracker[totalCols];
            for (int i = 0; i < totalCols; i++)
            {
                trackers[i] = new ColumnStatTracker(headers[i]);
            }

            // 采样分析行 (为了极致速度与内存安全，对超大表采用前 5000 行精确统计)
            int sampleEndRow = Math.Min(endRow, startRow + 5000);
            for (int r = startRow + 1; r <= sampleEndRow; r++)
            {
                var rowCells = new List<string>();
                for (int c = startCol; c <= endCol; c++)
                {
                    string cellVal = ws.Cells[r, c].Value?.ToString()?.Trim() ?? string.Empty;
                    rowCells.Add(cellVal);
                    trackers[c - startCol].AddValue(cellVal);
                }

                if (profile.HeadPreviewRows.Count < 5)
                {
                    profile.HeadPreviewRows.Add(rowCells);
                }
            }

            for (int i = 0; i < totalCols; i++)
            {
                profile.Columns.Add(trackers[i].ToSchemaInfo(totalDataRows));
            }

            return profile;
        }

        #endregion

        #region 列统计器辅助类

        private sealed class ColumnStatTracker
        {
            public string Name { get; }
            private long _nonNullCount = 0;
            private long _nullCount = 0;
            private long _intCount = 0;
            private long _floatCount = 0;
            private long _dateCount = 0;
            private long _boolCount = 0;
            private double _minNum = double.MaxValue;
            private double _maxNum = double.MinValue;
            private double _sumNum = 0;
            private readonly HashSet<string> _distinctValues = new(StringComparer.OrdinalIgnoreCase);
            private readonly List<string> _sampleValues = new();

            public ColumnStatTracker(string name)
            {
                Name = name;
            }

            public void AddValue(string val)
            {
                if (string.IsNullOrWhiteSpace(val))
                {
                    _nullCount++;
                    return;
                }

                _nonNullCount++;

                if (_distinctValues.Count < 100)
                {
                    _distinctValues.Add(val);
                }

                if (_sampleValues.Count < 4 && !_sampleValues.Contains(val))
                {
                    _sampleValues.Add(val);
                }

                // 类型推断
                if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long lVal))
                {
                    _intCount++;
                    _minNum = Math.Min(_minNum, lVal);
                    _maxNum = Math.Max(_maxNum, lVal);
                    _sumNum += lVal;
                }
                else if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double dVal))
                {
                    _floatCount++;
                    _minNum = Math.Min(_minNum, dVal);
                    _maxNum = Math.Max(_maxNum, dVal);
                    _sumNum += dVal;
                }
                else if (bool.TryParse(val, out _))
                {
                    _boolCount++;
                }
                else if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    _dateCount++;
                }
            }

            public ColumnSchemaInfo ToSchemaInfo(long totalRows)
            {
                string inferredType = "String";
                if (_nonNullCount > 0)
                {
                    if ((double)_intCount / _nonNullCount > 0.85) inferredType = "Int64";
                    else if ((double)(_intCount + _floatCount) / _nonNullCount > 0.85) inferredType = "Float64";
                    else if ((double)_boolCount / _nonNullCount > 0.85) inferredType = "Boolean";
                    else if ((double)_dateCount / _nonNullCount > 0.85) inferredType = "DateTime";
                }

                double missingPct = totalRows > 0 ? ((double)_nullCount / totalRows) * 100.0 : 0.0;

                string? minStr = null;
                string? maxStr = null;
                double? meanVal = null;

                if (inferredType is "Int64" or "Float64" && _minNum <= _maxNum)
                {
                    minStr = _minNum.ToString("G", CultureInfo.InvariantCulture);
                    maxStr = _maxNum.ToString("G", CultureInfo.InvariantCulture);
                    long numCount = _intCount + _floatCount;
                    if (numCount > 0)
                    {
                        meanVal = _sumNum / numCount;
                    }
                }

                return new ColumnSchemaInfo
                {
                    Name = Name,
                    DataType = inferredType,
                    NullCount = _nullCount,
                    MissingPercentage = missingPct,
                    DistinctCount = _distinctValues.Count,
                    Min = minStr,
                    Max = maxStr,
                    Mean = meanVal,
                    SampleValues = new List<string>(_sampleValues)
                };
            }
        }

        #endregion
    }
}

