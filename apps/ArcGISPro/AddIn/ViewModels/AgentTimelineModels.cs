using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ArcGISProAIAgent.ViewModels
{
    public class ChatMessage
    {
        public string Role { get; set; } = "user";
        public string Content { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.Now;

        public ChatMessage() { }

        public ChatMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }
    }

    public enum TimelineItemKind
    {
        Message,
        TraceGroup,
        PlanCard
    }

    public enum ToolTraceStatus
    {
        Pending,
        Running,
        Completed,
        Failed,
        Blocked,
        AwaitingApproval,
        Warned
    }

    public abstract class TimelineBindable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class TimelineItem : TimelineBindable
    {
        private bool _isExpanded;
        private string _traceHeader = "";
        private string _tokenInfo = "";
        private string _cardTitle = "";
        private string _cardBadge = "";

        public TimelineItemKind Kind { get; private init; }
        public ChatMessage? Chat { get; private init; }
        public ObservableCollection<ToolTraceItem> TraceItems { get; } = new();
        public ObservableCollection<string> CardLines { get; } = new();
        public int Turn { get; private init; }

        public bool IsMessage => Kind == TimelineItemKind.Message;
        public bool IsTraceGroup => Kind == TimelineItemKind.TraceGroup;
        public bool IsPlanCard => Kind == TimelineItemKind.PlanCard;

        /// <summary>轨迹组中是否有至少一个正在执行的步骤。</summary>
        public bool HasRunningItem => IsTraceGroup && TraceItems.Any(t => t.Status == ToolTraceStatus.Running);

        /// <summary>ViewModel 每次更新轨迹状态后调用，驱动 HasRunningItem 绑定刷新。</summary>
        public void RefreshHasRunningItem() => OnPropertyChanged(nameof(HasRunningItem));

        public string TokenInfo
        {
            get => _tokenInfo;
            set => Set(ref _tokenInfo, value);
        }

        public bool HasTokenInfo => !string.IsNullOrWhiteSpace(_tokenInfo);

        public bool IsExpanded
        {
            get => _isExpanded;
            set => Set(ref _isExpanded, value);
        }

        public string TraceHeader
        {
            get => _traceHeader;
            set => Set(ref _traceHeader, value);
        }

        public string CardTitle
        {
            get => _cardTitle;
            set => Set(ref _cardTitle, value);
        }

        public string CardBadge
        {
            get => _cardBadge;
            set => Set(ref _cardBadge, value);
        }

        public bool HasCardBadge => !string.IsNullOrWhiteSpace(_cardBadge);

        public static TimelineItem Message(ChatMessage message) => new()
        {
            Kind = TimelineItemKind.Message,
            Chat = message
        };

        public static TimelineItem TraceGroup(int turn) => new()
        {
            Kind = TimelineItemKind.TraceGroup,
            Turn = turn,
            TraceHeader = $"执行轨迹 · 第 {turn} 轮"
        };

        public static TimelineItem PlanCard(int turn, string title, IEnumerable<string> lines, string? badge = null)
        {
            var item = new TimelineItem
            {
                Kind = TimelineItemKind.PlanCard,
                Turn = turn,
                CardTitle = title,
                CardBadge = badge ?? ""
            };
            foreach (var line in lines)
                item.CardLines.Add(line);
            return item;
        }
    }

    public class ToolTraceItem : TimelineBindable
    {
        private int _turn;
        private int _step;
        private string _toolName = "";
        private string _operationDesc = "";
        private ToolTraceStatus _status = ToolTraceStatus.Pending;
        private string _riskLevel = "";
        private string _argsPreview = "";
        private string _outputPreview = "";
        private string _error = "";
        private string _validationSummary = "";
        private string _progressText = "";
        private string _approvalTicketId = "";
        private string _approvalCallSignature = "";
        private long _elapsedMs;
        private bool _isExpanded;
        private DateTime _timestamp = DateTime.Now;

        public int Turn
        {
            get => _turn;
            set => Set(ref _turn, value);
        }

        public int Step
        {
            get => _step;
            set
            {
                if (Set(ref _step, value))
                    OnPropertyChanged(nameof(StepLabel));
            }
        }

        public string ToolName
        {
            get => _toolName;
            set => Set(ref _toolName, value);
        }

        public string OperationDesc
        {
            get => _operationDesc;
            set => Set(ref _operationDesc, value);
        }

        public ToolTraceStatus Status
        {
            get => _status;
            set
            {
                if (Set(ref _status, value))
                {
                    OnPropertyChanged(nameof(StatusIcon));
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(StatusBrush));
                    OnPropertyChanged(nameof(StatusBackground));
                    OnPropertyChanged(nameof(StatusBorderBrush));
                    OnPropertyChanged(nameof(StatusBadgeBackground));
                }
            }
        }

        public string RiskLevel
        {
            get => _riskLevel;
            set
            {
                if (Set(ref _riskLevel, value))
                    RaiseDetailsChanged(nameof(RiskText), nameof(HasRisk));
            }
        }

        public string ArgsPreview
        {
            get => _argsPreview;
            set
            {
                if (Set(ref _argsPreview, value))
                    RaiseDetailsChanged(nameof(HasArgs));
            }
        }

        public string OutputPreview
        {
            get => _outputPreview;
            set
            {
                if (Set(ref _outputPreview, value))
                    RaiseDetailsChanged(nameof(HasOutput));
            }
        }

        public string Error
        {
            get => _error;
            set
            {
                if (Set(ref _error, value))
                    RaiseDetailsChanged(nameof(HasError));
            }
        }

        public string ValidationSummary
        {
            get => _validationSummary;
            set
            {
                if (Set(ref _validationSummary, value))
                    RaiseDetailsChanged(nameof(HasValidation));
            }
        }

        public string ProgressText
        {
            get => _progressText;
            set
            {
                if (Set(ref _progressText, value))
                    RaiseDetailsChanged(nameof(HasProgress));
            }
        }

        public string ApprovalTicketId
        {
            get => _approvalTicketId;
            set => Set(ref _approvalTicketId, value);
        }

        public string ApprovalCallSignature
        {
            get => _approvalCallSignature;
            set => Set(ref _approvalCallSignature, value);
        }

        public long ElapsedMs
        {
            get => _elapsedMs;
            set
            {
                if (Set(ref _elapsedMs, value))
                    RaiseDetailsChanged(nameof(ElapsedText), nameof(HasElapsed));
            }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set => Set(ref _isExpanded, value);
        }

        public DateTime Timestamp
        {
            get => _timestamp;
            set => Set(ref _timestamp, value);
        }

        public string StepLabel => Step > 0 ? $"步骤 {Step}" : "步骤";
        public string RiskText => string.IsNullOrWhiteSpace(RiskLevel) ? "" : $"风险：{RiskLevel}";
        public string ElapsedText => ElapsedMs > 0 ? $"{ElapsedMs} ms" : "";
        public bool HasRisk => !string.IsNullOrWhiteSpace(RiskLevel);
        public bool HasArgs => !string.IsNullOrWhiteSpace(ArgsPreview);
        public bool HasOutput => !string.IsNullOrWhiteSpace(OutputPreview);
        public bool HasError => !string.IsNullOrWhiteSpace(Error);
        public bool HasValidation => !string.IsNullOrWhiteSpace(ValidationSummary);
        public bool HasProgress => !string.IsNullOrWhiteSpace(ProgressText);
        public bool HasElapsed => ElapsedMs > 0;
        public bool HasDetails => HasRisk || HasArgs || HasOutput || HasError || HasValidation || HasProgress || HasElapsed;

        public string StatusIcon => Status switch
        {
            ToolTraceStatus.Running => "...",
            ToolTraceStatus.Completed => "OK",
            ToolTraceStatus.Failed => "ERR",
            ToolTraceStatus.Blocked => "STOP",
            ToolTraceStatus.AwaitingApproval => "WAIT",
            ToolTraceStatus.Warned => "WARN",
            _ => "..."
        };

        public string StatusText => Status switch
        {
            ToolTraceStatus.Running => "执行中",
            ToolTraceStatus.Completed => "完成",
            ToolTraceStatus.Failed => "失败",
            ToolTraceStatus.Blocked => "已阻止",
            ToolTraceStatus.AwaitingApproval => "待授权",
            ToolTraceStatus.Warned => "警告",
            _ => "等待"
        };

        public string StatusBrush => Status switch
        {
            ToolTraceStatus.Completed => "#2E7D32",
            ToolTraceStatus.Failed => "#C62828",
            ToolTraceStatus.Blocked => "#AD3B00",
            ToolTraceStatus.AwaitingApproval => "#8A5A00",
            ToolTraceStatus.Warned => "#9A6700",
            ToolTraceStatus.Running => "#006CBE",
            _ => "#777777"
        };

        public string StatusBackground => Status switch
        {
            ToolTraceStatus.Completed => "#EEF7F1",
            ToolTraceStatus.Failed => "#FDEEEE",
            ToolTraceStatus.Blocked => "#FFF1E8",
            ToolTraceStatus.AwaitingApproval => "#FFF7E6",
            ToolTraceStatus.Warned => "#FFF8E7",
            ToolTraceStatus.Running => "#EEF6FD",
            _ => "#F7F9FC"
        };

        public string StatusBorderBrush => Status switch
        {
            ToolTraceStatus.Completed => "#D4E9DA",
            ToolTraceStatus.Failed => "#F3C9C9",
            ToolTraceStatus.Blocked => "#F0C2A7",
            ToolTraceStatus.AwaitingApproval => "#F0C36D",
            ToolTraceStatus.Warned => "#F0D9A7",
            ToolTraceStatus.Running => "#C6DEEF",
            _ => "#E1E7EE"
        };

        public string StatusBadgeBackground => Status switch
        {
            ToolTraceStatus.Completed => "#DFF1E4",
            ToolTraceStatus.Failed => "#F8DADA",
            ToolTraceStatus.Blocked => "#F8DDCF",
            ToolTraceStatus.AwaitingApproval => "#FBE8BC",
            ToolTraceStatus.Warned => "#F9E9BF",
            ToolTraceStatus.Running => "#DDEDF9",
            _ => "#ECEFF3"
        };

        private void RaiseDetailsChanged(params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
                OnPropertyChanged(propertyName);

            OnPropertyChanged(nameof(HasDetails));
        }
    }
}
