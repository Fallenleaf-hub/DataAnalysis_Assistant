using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;

namespace DataAnalysis_Assistant.Models
{
    public class DocumentGroup : INotifyPropertyChanged
    {
        private string _groupName = "未命名文档组";
        private bool _isSelected = false;
        private bool _isExpanded = true;
        private DateTime _lastActiveAt = DateTime.Now;

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

        [JsonIgnore]
        public string ExpandChevronGlyph => _isExpanded ? "⌃" : "⌄";

        [JsonIgnore]
        public string ChevronPathData => _isExpanded ? "M 2.5 7.5 L 6 4 L 9.5 7.5" : "M 2.5 4.5 L 6 8 L 9.5 4.5";

        [JsonIgnore]
        public ObservableCollection<ChatSessionItem> AssociatedSessions { get; } = new();

        public string GroupId { get; set; } = Guid.NewGuid().ToString("N");

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

        public List<string> DocumentIds { get; set; } = new();

        public DateTime CreatedAt { get; set; } = DateTime.Now;

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

        [JsonIgnore]
        public string FormattedLastActive => LastActiveAt.ToString("MM-dd HH:mm");

        /// 是否为系统内置的独立自由会话专区（不可重命名，不可物理删除）
        [JsonIgnore]
        public bool IsSystemGroup => string.Equals(GroupId, Services.Persistence.LibraryPersistenceService.StandaloneGroupId, StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public int DocumentCount => DocumentIds.Count;

        [JsonIgnore]
        public string DocCountBadge => $"{DocumentCount} 个文档";

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
