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
        public string SummaryText => $"{FormattedSize} · {Status}";

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
