using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DataAnalysis_Assistant.Models
{
    /// <summary>
    /// 数据分析生成的图表产物模型（PNG/SVG/JPG）
    /// </summary>
    public class ChartArtifactItem : INotifyPropertyChanged
    {
        private string _title = string.Empty;
        private string _filePath = string.Empty;
        private bool _isPinnedToDashboard = false;
        private BitmapImage? _bitmap;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// 图表文件绝对路径
        /// </summary>
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

        /// <summary>
        /// 文件名称
        /// </summary>
        [JsonIgnore]
        public string FileName => string.IsNullOrEmpty(_filePath) ? string.Empty : Path.GetFileName(_filePath);

        /// <summary>
        /// 图表业务标题（例如：销售增长多维对比图）
        /// </summary>
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

        /// <summary>
        /// 生成时间戳
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// 格式化时间
        /// </summary>
        [JsonIgnore]
        public string FormattedTime => CreatedAt.ToString("HH:mm:ss");

        /// <summary>
        /// 文件大小（字节）
        /// </summary>
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

        /// <summary>
        /// 人类可读格式化文件大小
        /// </summary>
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

        /// <summary>
        /// 用于 XAML 图片绑定的绝对文件 Uri
        /// </summary>
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

        /// <summary>
        /// 内存缓存的 BitmapImage，加速 WinUI 3 渲染
        /// </summary>
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

        /// <summary>
        /// 是否已固定/沉淀到【可视化看板】
        /// </summary>
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
