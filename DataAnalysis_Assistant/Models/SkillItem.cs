using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DataAnalysis_Assistant.Models
{
    /// <summary>
    /// 符合标准 Skill 规范的自定义技能模型
    /// </summary>
    public class SkillItem : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _displayName = string.Empty;
        private string _description = string.Empty;
        private string _directoryPath = string.Empty;
        private string _skillMdPath = string.Empty;
        private bool _isEnabled = true;
        private string _sourceLocation = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 技能标识唯一名称（如 math-modeling, nature-figure）
        /// </summary>
        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 技能显示标题
        /// </summary>
        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (_displayName != value)
                {
                    _displayName = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 技能描述（来自 SKILL.md Frontmatter）
        /// </summary>
        public string Description
        {
            get => _description;
            set
            {
                if (_description != value)
                {
                    _description = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 技能文件夹绝对路径
        /// </summary>
        public string DirectoryPath
        {
            get => _directoryPath;
            set
            {
                if (_directoryPath != value)
                {
                    _directoryPath = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 对应的 SKILL.md 路径
        /// </summary>
        public string SkillMdPath
        {
            get => _skillMdPath;
            set
            {
                if (_skillMdPath != value)
                {
                    _skillMdPath = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 技能是否已开启启用
        /// </summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 来源目录标识
        /// </summary>
        public string SourceLocation
        {
            get => _sourceLocation;
            set
            {
                if (_sourceLocation != value)
                {
                    _sourceLocation = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 所在文件夹名称
        /// </summary>
        public string FolderName => string.IsNullOrEmpty(DirectoryPath) ? string.Empty : System.IO.Path.GetFileName(DirectoryPath);

        /// <summary>
        /// 快捷斜杠指令触发词（如 /nature-figure）
        /// </summary>
        public string CommandTrigger => $"/{Name}";

        private string? _fullInstructions;

        /// <summary>
        /// 提取的完整 Markdown 业务指令与规约内容（去除 YAML 前置元数据）
        /// </summary>
        public string? FullInstructions
        {
            get => _fullInstructions;
            set
            {
                if (_fullInstructions != value)
                {
                    _fullInstructions = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasInstructions));
                }
            }
        }

        /// <summary>
        /// 是否已成功加载技能规约正文
        /// </summary>
        public bool HasInstructions => !string.IsNullOrWhiteSpace(_fullInstructions);

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
