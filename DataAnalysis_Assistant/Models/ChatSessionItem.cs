using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;

namespace DataAnalysis_Assistant.Models
{
    public class ChatSessionItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _title = "新建对话";
        private string? _bindGroupId;
        private string? _bindGroupName;
        private DateTime _lastActiveAt = DateTime.Now;

        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// 兼容持久化主键别名 (与 Id 等价)
        /// </summary>
        public string SessionId
        {
            get => Id;
            set => Id = value;
        }

        public string Title
        {
            get => _title;
            set
            {
                if (_title != value)
                {
                    _title = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShortTitle));
                }
            }
        }

        [JsonIgnore]
        public string ShortTitle => Title.Length > 4 ? Title.Substring(0, 4) : Title;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [JsonIgnore]
        public string FormattedDate => CreatedAt.ToString("MM-dd HH:mm");

        /// <summary>
        /// 绑定的文档组ID（可为空，表示无附件独立会话）
        /// </summary>
        public string? BindGroupId
        {
            get => _bindGroupId;
            set
            {
                if (_bindGroupId != value)
                {
                    _bindGroupId = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasBoundGroup));
                    OnPropertyChanged(nameof(GroupBadgeText));
                }
            }
        }

        /// <summary>
        /// 绑定的文档组名称（便于历史列表快速展示，如 [财务2025组]）
        /// </summary>
        public string? BindGroupName
        {
            get => _bindGroupName;
            set
            {
                if (_bindGroupName != value)
                {
                    _bindGroupName = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(GroupBadgeText));
                }
            }
        }

        /// <summary>
        /// 最后活跃时间
        /// </summary>
        public DateTime LastActiveAt
        {
            get => _lastActiveAt;
            set
            {
                if (_lastActiveAt != value)
                {
                    _lastActiveAt = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FormattedLastActive));
                    OnPropertyChanged(nameof(FriendlyRelativeTime));
                }
            }
        }

        [JsonIgnore]
        public string FormattedLastActive => LastActiveAt.ToString("MM-dd HH:mm");

        /// <summary>
        /// 友好相对时间显示（如：6m, 2h, 1d，对应界面简写样式）
        /// </summary>
        [JsonIgnore]
        public string FriendlyRelativeTime
        {
            get
            {
                var diff = DateTime.Now - LastActiveAt;
                if (diff.TotalMinutes < 1)
                {
                    return "刚刚";
                }
                if (diff.TotalMinutes < 60)
                {
                    return $"{(int)diff.TotalMinutes}m";
                }
                if (diff.TotalHours < 24)
                {
                    return $"{(int)diff.TotalHours}h";
                }
                if (diff.TotalDays < 7)
                {
                    return $"{(int)diff.TotalDays}d";
                }
                return LastActiveAt.ToString("MM-dd");
            }
        }

        [JsonIgnore]
        public bool HasBoundGroup => !string.IsNullOrWhiteSpace(_bindGroupId);

        [JsonIgnore]
        public string GroupBadgeText => string.IsNullOrWhiteSpace(_bindGroupName) ? "独立会话" : _bindGroupName;

        /// <summary>
        /// 会话内的完整消息历史记录集合
        /// </summary>
        public List<ChatMessageItem> Messages { get; set; } = new();

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(BackgroundBrush));
                    OnPropertyChanged(nameof(ForegroundBrush));
                    OnPropertyChanged(nameof(BorderThicknessValue));
                }
            }
        }

        private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush BrushActiveBg = new(Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
        private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush BrushTransparent = new(Microsoft.UI.Colors.Transparent);
        private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush BrushActiveFg = new(Windows.UI.Color.FromArgb(255, 0x2E, 0x28, 0x22));
        private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush BrushInactiveFg = new(Windows.UI.Color.FromArgb(255, 0x7D, 0x74, 0x6A));
        private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush BrushActiveBorder = new(Windows.UI.Color.FromArgb(255, 0xE2, 0xDD, 0xD0));

        // 选中高亮与非选中浅底色/灰色样式辅助属性
        [JsonIgnore]
        public Microsoft.UI.Xaml.Media.Brush BackgroundBrush => IsSelected ? BrushActiveBg : BrushTransparent;

        [JsonIgnore]
        public Microsoft.UI.Xaml.Media.Brush ForegroundBrush => IsSelected ? BrushActiveFg : BrushInactiveFg;

        [JsonIgnore]
        public Microsoft.UI.Xaml.Media.Brush BorderBrush => IsSelected ? BrushActiveBorder : BrushTransparent;

        [JsonIgnore]
        public Thickness BorderThicknessValue => IsSelected ? new Thickness(1) : new Thickness(0);

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

