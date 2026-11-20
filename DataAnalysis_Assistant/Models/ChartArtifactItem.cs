using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DataAnalysis_Assistant.Models
{
    public class ChartArtifactItem : INotifyPropertyChanged
    {
        private string _title = string.Empty;
        private string _filePath = string.Empty;
        private bool _isPinnedToDashboard = false;
        private BitmapImage? _bitmap;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string FilePath
        {
            get => _filePath;
            set
            {
                if (_filePath != value)
                {
                    _filePath = value;
                    _bitmap = null;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FileName));
                    OnPropertyChanged(nameof(ImageUri));
                    OnPropertyChanged(nameof(FormattedSize));
                    OnPropertyChanged(nameof(Bitmap));
                }
            }
        }

        [JsonIgnore]
        public string FileName => string.IsNullOrEmpty(_filePath) ? string.Empty : Path.GetFileName(_filePath);

        public string Title
        {
            get => string.IsNullOrWhiteSpace(_title) ? FileName : _title;
            set
            {
                if (_title != value)
                {
                    _title = value;
                    OnPropertyChanged();
                }
            }
        }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [JsonIgnore]
        public string FormattedTime => CreatedAt.ToString("HH:mm:ss");

        [JsonIgnore]
        public long FileSize
        {
            get
            {
                try
                {
                    if (File.Exists(_filePath))
                    {
                        return new FileInfo(_filePath).Length;
                    }
                }
                catch { }
                return 0;
            }
        }

        [JsonIgnore]
        public string FormattedSize
        {
            get
            {
                long bytes = FileSize;
                if (bytes < 1024) return $"{bytes} B";
                if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F1} KB";
                return $"{(bytes / (1024.0 * 1024.0)):F2} MB";
            }
        }

        [JsonIgnore]
        public bool IsFileExisting
        {
            get
            {
                try
                {
                    return !string.IsNullOrEmpty(_filePath) && File.Exists(_filePath);
                }
                catch
                {
                    return false;
                }
            }
        }

        [JsonIgnore]
        public bool IsMissing => !IsFileExisting;

        [JsonIgnore]
        public Uri? ImageUri
        {
            get
            {
                if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath)) return null;
                try
                {
                    return new Uri(_filePath);
                }
                catch
                {
                    return null;
                }
            }
        }

        [JsonIgnore]
        public BitmapImage? Bitmap
        {
            get
            {
                if (_bitmap == null && ImageUri != null)
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.UriSource = ImageUri;
                        _bitmap = bmp;
                    }
                    catch
                    {
                    }
                }
                return _bitmap;
            }
            set
            {
                _bitmap = value;
                OnPropertyChanged();
            }
        }

        public bool IsPinnedToDashboard
        {
            get => _isPinnedToDashboard;
            set
            {
                if (_isPinnedToDashboard != value)
                {
                    _isPinnedToDashboard = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PinButtonTooltip));
                }
            }
        }

        [JsonIgnore]
        public string PinButtonTooltip => _isPinnedToDashboard ? "已在可视化看板中" : "推送到可视化看板";

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
