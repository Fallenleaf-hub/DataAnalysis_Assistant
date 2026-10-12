using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DataAnalysis_Assistant.Models
{
    public class DocumentItem : INotifyPropertyChanged
    {
        private string _status = "待解析";
        private string _extractedText = string.Empty;

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        public string FormattedSize { get; set; } = string.Empty;
        public long SizeInBytes { get; set; }
        public DateTime UploadTime { get; set; } = DateTime.Now;

        public string ExtractedText
        {
            get => _extractedText;
            set
            {
                if (_extractedText != value)
                {
                    _extractedText = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CharacterCount));
                    OnPropertyChanged(nameof(EstimatedTokens));
                }
            }
        }

        public int CharacterCount => ExtractedText?.Length ?? 0;
        public int EstimatedTokens => (int)Math.Ceiling(CharacterCount / 4.0);

        public string Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged();
                }
            }
        }

        public string BadgeBackground => Extension.ToLowerInvariant() switch
        {
            ".pdf" => "#E05345",
            ".docx" or ".doc" => "#2B579A",
            ".xlsx" or ".xls" or ".csv" => "#217346",
            ".txt" or ".md" => "#795548",
            _ => "#607D8B"
        };

        public string ExtensionLabel => Extension.TrimStart('.').ToUpperInvariant();
        private DataSchemaProfile? _schemaProfile;

        /// <summary>
        /// 是否属于结构化大表格（CSV/Excel）
        /// </summary>
        public bool IsTabularData => Extension.ToLowerInvariant() switch
        {
            ".csv" or ".tsv" or ".xlsx" or ".xls" => true,
            _ => false
        };

        /// <summary>
        /// 表格元数据骨架（践行计算下推规范）
        /// </summary>
        public DataSchemaProfile? SchemaProfile
        {
            get => _schemaProfile;
            set
            {
                if (_schemaProfile != value)
                {
                    _schemaProfile = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TableSummary));
                }
            }
        }

        public string TableSummary => _schemaProfile != null
            ? $"{_schemaProfile.TotalRows:N0} 行 × {_schemaProfile.TotalColumns} 列"
            : string.Empty;

        public string SummaryText => IsTabularData && _schemaProfile != null
            ? $"{FormattedSize} · {TableSummary}"
            : $"{FormattedSize} · {Status}";

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
