using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;

namespace DataAnalysis_Assistant.Models
{
    public class ChatMessageItem : INotifyPropertyChanged
    {
        private string _content = string.Empty;
        private string _reasoningContent = string.Empty;
        private bool _isReasoningExpanded = true;
        private bool _isThinking = false;
        private double _thinkingSeconds = 0;
        private bool _isStopped = false;
        private ObservableCollection<ChartArtifactItem> _generatedPlots = new();

        public ChatMessageItem()
        {
            _generatedPlots.CollectionChanged += OnGeneratedPlotsChanged;
        }

        private void OnGeneratedPlotsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasPlots));
            OnPropertyChanged(nameof(HasPlotsVisibility));
        }

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Sender { get; set; } = "Assistant"; // "User" or "Assistant"

        [JsonIgnore]
        public string SenderName => Sender == "User" ? "我 (用户)" : "AI 助手";

        [JsonIgnore]
        public bool IsUser => Sender == "User";

        [JsonIgnore]
        public bool IsAssistant => !IsUser;

        [JsonIgnore]
        public Visibility UserBubbleVisibility => IsUser ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public Visibility AssistantBubbleVisibility => IsAssistant ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// 本轮对话或代码执行生成的图表资产列表
        /// </summary>
        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public ObservableCollection<ChartArtifactItem> GeneratedPlots
        {
            get => _generatedPlots;
            set
            {
                if (_generatedPlots != value)
                {
                    if (_generatedPlots != null)
                    {
                        _generatedPlots.CollectionChanged -= OnGeneratedPlotsChanged;
                    }
                    _generatedPlots = value ?? new();
                    _generatedPlots.CollectionChanged += OnGeneratedPlotsChanged;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasPlots));
                    OnPropertyChanged(nameof(HasPlotsVisibility));
                }
            }
        }

        [JsonIgnore]
        public bool HasPlots => GeneratedPlots.Count > 0;

        [JsonIgnore]
        public Visibility HasPlotsVisibility => HasPlots ? Visibility.Visible : Visibility.Collapsed;

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

        // 思考过程内容
        public string ReasoningContent
        {
            get => _reasoningContent;
            set
            {
                if (_reasoningContent != value)
                {
                    _reasoningContent = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasReasoning));
                    OnPropertyChanged(nameof(HasReasoningVisibility));
                    OnPropertyChanged(nameof(ReasoningBodyVisibility));
                    OnPropertyChanged(nameof(ReasoningDisplayText));
                }
            }
        }

        // 只要处于思考状态或者已有思考内容，均视为有思考过程，立即展示思考卡片
        [JsonIgnore]
        public bool HasReasoning => !string.IsNullOrWhiteSpace(_reasoningContent) || _isThinking;

        [JsonIgnore]
        public Visibility HasReasoningVisibility => HasReasoning ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public string ReasoningDisplayText
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_reasoningContent))
                {
                    return _reasoningContent;
                }
                return _isThinking ? "正在组织推导逻辑..." : string.Empty;
            }
        }

        // 思考卡片展开/收起状态
        public bool IsReasoningExpanded
        {
            get => _isReasoningExpanded;
            set
            {
                if (_isReasoningExpanded != value)
                {
                    _isReasoningExpanded = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ReasoningBodyVisibility));
                    OnPropertyChanged(nameof(ReasoningToggleText));
                }
            }
        }

        [JsonIgnore]
        public Visibility ReasoningBodyVisibility => (HasReasoning && _isReasoningExpanded) ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public string ReasoningToggleText => _isReasoningExpanded ? "收起" : "展开";

        // 思考状态与耗时
        public bool IsThinking
        {
            get => _isThinking;
            set
            {
                if (_isThinking != value)
                {
                    _isThinking = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasReasoning));
                    OnPropertyChanged(nameof(HasReasoningVisibility));
                    OnPropertyChanged(nameof(ReasoningBodyVisibility));
                    OnPropertyChanged(nameof(ReasoningDisplayText));
                    OnPropertyChanged(nameof(ThinkingIndicatorVisibility));
                    OnPropertyChanged(nameof(ThinkingStatusText));
                }
            }
        }

        [JsonIgnore]
        public Visibility ThinkingIndicatorVisibility => _isThinking ? Visibility.Visible : Visibility.Collapsed;

        public double ThinkingSeconds
        {
            get => _thinkingSeconds;
            set
            {
                if (Math.Abs(_thinkingSeconds - value) > 0.05)
                {
                    _thinkingSeconds = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ThinkingStatusText));
                }
            }
        }

        [JsonIgnore]
        public string ThinkingStatusText
        {
            get
            {
                if (_isThinking)
                {
                    return _thinkingSeconds > 0 ? $"正在思考... ({_thinkingSeconds:0.#}s)" : "正在思考...";
                }
                return _thinkingSeconds > 0 ? $"已完成思考 (耗时 {_thinkingSeconds:0.#}s)" : "已完成思考";
            }
        }

        // 手动停止标记
        public bool IsStopped
        {
            get => _isStopped;
            set
            {
                if (_isStopped != value)
                {
                    _isStopped = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StoppedNoticeVisibility));
                }
            }
        }

        [JsonIgnore]
        public Visibility StoppedNoticeVisibility => _isStopped ? Visibility.Visible : Visibility.Collapsed;

        public DateTime Timestamp { get; set; } = DateTime.Now;

        [JsonIgnore]
        public string FormattedTime => Timestamp.ToString("HH:mm:ss");

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

