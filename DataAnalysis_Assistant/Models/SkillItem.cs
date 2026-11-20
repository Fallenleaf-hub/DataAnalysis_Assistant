using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DataAnalysis_Assistant.Models
{
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

        public string FolderName => string.IsNullOrEmpty(DirectoryPath) ? string.Empty : System.IO.Path.GetFileName(DirectoryPath);

        public string CommandTrigger => $"/{Name}";

        private string? _fullInstructions;

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

        public bool HasInstructions => !string.IsNullOrWhiteSpace(_fullInstructions);

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
