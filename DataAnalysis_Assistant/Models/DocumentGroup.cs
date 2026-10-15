using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;

namespace DataAnalysis_Assistant.Models
{
    /// <summary>
    /// 文档组核心实体：用户将多个关联文档（CSV + PDF + Word等）打包为一个业务分析单元
    /// </summary>
    public class DocumentGroup : INotifyPropertyChanged
    {
        private string _groupName = "未命名文档组";
        private bool _isSelected = false;
        private bool _isExpanded = true;
        private DateTime _lastActiveAt = DateTime.Now;

        /// <summary>
        /// 折叠/展开状态（默认展开）
        /// </summary>
        [JsonIgnore]
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SessionsVisibility));
                    OnPropertyChanged(nameof(ExpandChevronGlyph));
                    OnPropertyChanged(nameof(ChevronPathData));
                }
            }
        }

        [JsonIgnore]
        public Visibility SessionsVisibility => _isExpanded ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// 下拉折叠箭头图标（展开状态显示 ⌃，折叠状态显示 ⌄）
        /// </summary>
        [JsonIgnore]
        public string ExpandChevronGlyph => _isExpanded ? "⌃" : "⌄";

        /// <summary>
        /// 平滑圆角下拉折叠箭头 Path 数据（折叠时朝下，展开时朝上，圆润平滑）
        /// </summary>
        [JsonIgnore]
        public string ChevronPathData => _isExpanded ? "M 2.5 7.5 L 6 4 L 9.5 7.5" : "M 2.5 4.5 L 6 8 L 9.5 4.5";

        /// <summary>
        /// 属于当前文档组的关联历史会话集合
        /// </summary>
        [JsonIgnore]
        public ObservableCollection<ChatSessionItem> AssociatedSessions { get; } = new();

        /// <summary>
        /// 文档组全局唯一标识符 (GUID)
        /// </summary>
        public string GroupId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// 文档组名称（例如：2025Q1财务与销售复盘）
        /// </summary>
        public string GroupName
        {
            get => _groupName;
            set
            {
                if (_groupName != value)
                {
                    _groupName = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 组内包含的文档唯一标识符列表 (DocId)
        /// </summary>
        public List<string> DocumentIds { get; set; } = new();

        /// <summary>
        /// 文档组创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

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
                }
            }
        }

        /// <summary>
        /// 格式化最后活跃时间（用于 UI 显示）
        /// </summary>
        [JsonIgnore]
        public string FormattedLastActive => LastActiveAt.ToString("MM-dd HH:mm");

        /// <summary>
        /// 文档组包含的文件数量
        /// </summary>
        [JsonIgnore]
        public int DocumentCount => DocumentIds.Count;

        [JsonIgnore]
        public string DocCountBadge => $"{DocumentCount} 个文档";

        /// <summary>
        /// UI 选中状态
        /// </summary>
        [JsonIgnore]
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
                    OnPropertyChanged(nameof(BorderBrush));
                }
            }
        }

        [JsonIgnore]
        public string BackgroundBrush => IsSelected ? "#FFFFFF" : "Transparent";

        [JsonIgnore]
        public string BorderBrush => IsSelected ? "#E2DDD0" : "Transparent";

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

