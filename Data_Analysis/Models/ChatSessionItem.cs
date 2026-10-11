using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace Data_Analysis.Models
{
    public class ChatSessionItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _title = "新建对话";

        public string Id { get; set; } = Guid.NewGuid().ToString();

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

        public string ShortTitle => Title.Length > 4 ? Title.Substring(0, 4) : Title;

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string FormattedDate => CreatedAt.ToString("MM-dd HH:mm");

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

        // 选中高亮与非选中浅底色/灰色样式辅助属性
        public string BackgroundBrush => IsSelected ? "#FFFFFF" : "Transparent";
        public string ForegroundBrush => IsSelected ? "#2E2822" : "#7D746A";
        public string BorderBrush => IsSelected ? "#E2DDD0" : "Transparent";
        public Thickness BorderThicknessValue => IsSelected ? new Thickness(1) : new Thickness(0);

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

