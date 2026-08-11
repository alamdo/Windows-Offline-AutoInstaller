using System;
using System.ComponentModel;
using System.Windows;

namespace app_tự_động.Models
{
    public class AppSelectionItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private AppProcessState _status = AppProcessState.Ready;
        private int _progress;
        private string _message = "";

        public AppItem App { get; set; }

        public string Name => App?.Name;
        public string Description => "Tải file " + (App?.FileName ?? "");

        public Visibility CanDeleteVisibility
        {
            get
            {
                if (App == null)
                    return Visibility.Collapsed;

                return string.Equals(App.InteractiveArgs, "__CUSTOM_APP__", StringComparison.Ordinal)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public AppProcessState Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged(nameof(Status));
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case AppProcessState.Ready:
                        return "Ready";
                    case AppProcessState.Checking:
                        return "Checking";
                    case AppProcessState.Downloading:
                        return "Downloading";
                    case AppProcessState.Installing:
                        return "Installing";
                    case AppProcessState.Success:
                        return "Success";
                    case AppProcessState.Failed:
                        return "Failed";
                    case AppProcessState.Skipped:
                        return "Skipped";
                    case AppProcessState.Cancelled:
                        return "Cancelled";
                    default:
                        return "Unknown";
                }
            }
        }

        public int Progress
        {
            get => _progress;
            set
            {
                if (_progress != value)
                {
                    _progress = value;
                    OnPropertyChanged(nameof(Progress));
                }
            }
        }

        public string Message
        {
            get => _message;
            set
            {
                if (_message != value)
                {
                    _message = value;
                    OnPropertyChanged(nameof(Message));
                }
            }
        }

        public void ResetRuntimeState()
        {
            Status = AppProcessState.Ready;
            Progress = 0;
            Message = "";
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}