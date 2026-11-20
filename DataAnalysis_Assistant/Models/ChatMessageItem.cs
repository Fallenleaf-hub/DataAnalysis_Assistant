using System;
using System.Collections.Generic;
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
        private bool _isReasoningExpanded = false;
        private bool _isThinking = false;
        private double _thinkingSeconds = 0;
        private bool _isStopped = false;
        private AgentTerminationReason _terminationReason = AgentTerminationReason.None;
        private bool _isStreamTruncated = false;
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
        public string Sender { get; set; } = "Assistant";

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

        private string? _activeSkillName;

        public string? ActiveSkillName
        {
            get => _activeSkillName;
            set
            {
                if (_activeSkillName != value)
                {
                    _activeSkillName = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasActiveSkill));
                    OnPropertyChanged(nameof(ActiveSkillVisibility));
                }
            }
        }

        [JsonIgnore]
        public bool HasActiveSkill => !string.IsNullOrEmpty(ActiveSkillName);

        [JsonIgnore]
        public Visibility ActiveSkillVisibility => HasActiveSkill ? Visibility.Visible : Visibility.Collapsed;

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
        public string ReasoningToggleText => _isReasoningExpanded ? "收起思考 ⌃" : "展开思考 ⌄";

        [JsonIgnore]
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
                return _thinkingSeconds > 0 ? $"已完成深度思考 (耗时 {_thinkingSeconds:0.#}s)" : "已完成深度思考";
            }
        }

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

        public string? AgentTaskId { get; set; }

        public int TaskSequenceNumber { get; set; } = 0;

        public AgentTerminationReason TerminationReason
        {
            get => _terminationReason;
            set
            {
                if (_terminationReason != value)
                {
                    _terminationReason = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TerminationNoticeVisibility));
                    OnPropertyChanged(nameof(TerminationNoticeMessage));
                }
            }
        }

        public bool IsStreamTruncated
        {
            get => _isStreamTruncated;
            set
            {
                if (_isStreamTruncated != value)
                {
                    _isStreamTruncated = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TruncatedNoticeVisibility));
                }
            }
        }

        [JsonIgnore]
        public Visibility TerminationNoticeVisibility =>
            (_terminationReason == AgentTerminationReason.MaxIterationsReached ||
             _terminationReason == AgentTerminationReason.ConsecutiveErrorsTripped)
            ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public string TerminationNoticeMessage => _terminationReason switch
        {
            AgentTerminationReason.MaxIterationsReached =>
                "本次分析已达到最大探索上限，Agent 已综合当前阶段所有探索结果为您收敛汇聚本份研报。",
            AgentTerminationReason.ConsecutiveErrorsTripped =>
                "由于执行过程多次持续遇阻，Agent 已根据现有探索成果收敛汇聚本份研报。",
            _ => string.Empty
        };

        [JsonIgnore]
        public Visibility TruncatedNoticeVisibility =>
            _isStreamTruncated ? Visibility.Visible : Visibility.Collapsed;

        /// 本回合执行的结构化工具调用明细列表（支持安全序列化落盘，旧版本 JSON 反序列化时自动为空）
        public List<ToolCallRecordItem> ToolCalls { get; set; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
