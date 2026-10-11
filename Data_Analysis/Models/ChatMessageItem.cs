using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace Data_Analysis.Models
{
    public class ChatMessageItem : INotifyPropertyChanged
    {
        private string _content = string.Empty;

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Sender { get; set; } = "Assistant"; // "User" or "Assistant"
        public string SenderName => Sender == "User" ? "我 (用户)" : "AI 助手";
        public bool IsUser => Sender == "User";
        public bool IsAssistant => !IsUser;

        public Visibility UserBubbleVisibility => IsUser ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AssistantBubbleVisibility => IsAssistant ? Visibility.Visible : Visibility.Collapsed;

        public string Content
        {
            get => _content;
            set
            {
                if (_content != value)
                {
                    _content = value;
                    OnPropertyChanged();
                }
            }
        }

        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string FormattedTime => Timestamp.ToString("HH:mm:ss");

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

