using NetDriveGuard.Core;
using NetDriveGuard.Core.Events;
using NetDriveGuard.Core.Monitoring;

namespace NetDriveGuard.App;

public partial class Form1 : Form
{
    private readonly Label _stateValue = CreateValueLabel("尚未配置");
    private readonly Label _lastCheckValue = CreateValueLabel("尚未执行");
    private readonly Label _lastChangeValue = CreateValueLabel("尚无记录");
    private readonly Label _nextCheckValue = CreateValueLabel("等待配置");
    private readonly Label _lastErrorValue = new()
    {
        Text = "尚无记录",
        AutoSize = true,
        MaximumSize = new Size(440, 0),
        Margin = new Padding(3, 4, 3, 8),
    };
    private readonly ListView _eventsList = new()
    {
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        HideSelection = false,
        MultiSelect = false,
        Size = new Size(600, 170),
        Margin = new Padding(0, 6, 0, 0),
    };
    private readonly ComboBox _eventFilter = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 140,
        Margin = new Padding(6, 0, 18, 0),
    };
    private readonly ComboBox _eventStateFilter = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 125,
        Margin = new Padding(6, 0, 18, 0),
    };
    private readonly Label _eventCount = new()
    {
        Text = "显示 0 / 共 0 条",
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 5, 0, 0),
    };
    private readonly Button _checkNowButton = new() { Text = "立即检查", AutoSize = true };
    private readonly Button _pauseButton = new() { Text = "暂停监控", AutoSize = true };
    private readonly Button _hideButton = new() { Text = "隐藏到托盘", AutoSize = true };
    private readonly Label _modeHint = new()
    {
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 0, 0, 16),
    };
    private readonly bool _isDemoMode;
    private IReadOnlyList<MonitorEvent> _allEvents = [];
    private bool _allowExit;

    public event EventHandler? CheckRequested;

    public event EventHandler? PauseToggled;

    public bool IsPaused { get; private set; }

    public Form1(bool isDemoMode = false)
    {
        _isDemoMode = isDemoMode;
        _modeHint.Text = isDemoMode
            ? "演示状态由内置脚本生成，不访问网络，也不会连接或断开网络驱动器。"
            : "当前为只读原型，尚未配置监控目标，不会连接或断开网络驱动器。";
        InitializeComponent();
        BuildLayout();
        Text = $"NetDriveGuard {CoreModule.PrototypeVersion} 监控面板";
        FormClosing += HandleFormClosing;
        _checkNowButton.Click += (_, _) => CheckRequested?.Invoke(this, EventArgs.Empty);
        _pauseButton.Click += (_, _) => TogglePaused();
        _hideButton.Click += (_, _) => Hide();
        _eventsList.Columns.Add("时间", 145);
        _eventsList.Columns.Add("级别", 60);
        _eventsList.Columns.Add("状态", 90);
        _eventsList.Columns.Add("事件", 280);
        _eventFilter.Items.AddRange(
        [
            new EventFilterOption("全部事件", MonitorEventFilter.All),
            new EventFilterOption("警告及错误", MonitorEventFilter.WarningAndError),
            new EventFilterOption("仅错误", MonitorEventFilter.ErrorOnly),
        ]);
        _eventFilter.SelectedIndexChanged += (_, _) => RefreshEventList();
        _eventFilter.SelectedIndex = 0;
        _eventStateFilter.Items.Add(new EventStateFilterOption("全部状态", null));
        foreach (var state in Enum.GetValues<MonitorState>())
        {
            _eventStateFilter.Items.Add(
                new EventStateFilterOption(GetStateText(state), state));
        }

        _eventStateFilter.SelectedIndexChanged += (_, _) => RefreshEventList();
        _eventStateFilter.SelectedIndex = 0;
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    public void RecordUnconfiguredCheckRequest()
    {
        _lastCheckValue.Text = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}（未配置，未探测）";
        _stateValue.Text = "尚未配置";
        _nextCheckValue.Text = "等待配置";
    }

    public void ApplySnapshot(MonitorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _stateValue.Text = GetStateText(snapshot.CurrentState);
        _lastCheckValue.Text = snapshot.LastObservationAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        _lastChangeValue.Text = snapshot.LastTransitionAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        _nextCheckValue.Text = snapshot.CurrentState switch
        {
            MonitorState.NotConfigured => "等待配置",
            MonitorState.Paused => "已暂停",
            _ => "按动态周期",
        };
    }

    public void SetConfiguredReadOnlyMode()
    {
        _modeHint.Text = "正在只读观察配置目标：查询映射、TCP 连通性和共享可访问性，不会修改网络驱动器。";
    }

    public void UpdateRecentEvents(IReadOnlyList<MonitorEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        _allEvents = events.ToArray();
        var lastError = _allEvents.FirstOrDefault(
            monitorEvent => monitorEvent.Severity == MonitorEventSeverity.Error);
        _lastErrorValue.Text = lastError is null
            ? "尚无记录"
            : $"{lastError.OccurredAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {lastError.Description}";
        RefreshEventList();
    }

    private void RefreshEventList()
    {
        var filter = _eventFilter.SelectedItem is EventFilterOption option
            ? option.Filter
            : MonitorEventFilter.All;
        var state = _eventStateFilter.SelectedItem is EventStateFilterOption stateOption
            ? stateOption.State
            : null;
        var events = MonitorEventQuery.Apply(_allEvents, filter, state);

        _eventsList.BeginUpdate();
        try
        {
            _eventsList.Items.Clear();
            foreach (var monitorEvent in events)
            {
                var item = new ListViewItem(
                    monitorEvent.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                item.SubItems.Add(GetSeverityText(monitorEvent.Severity));
                item.SubItems.Add(GetStateText(monitorEvent.State));
                item.SubItems.Add(monitorEvent.Description);
                item.ForeColor = monitorEvent.Severity switch
                {
                    MonitorEventSeverity.Information => SystemColors.WindowText,
                    MonitorEventSeverity.Warning => Color.DarkOrange,
                    MonitorEventSeverity.Error => Color.Firebrick,
                    _ => SystemColors.WindowText,
                };
                _eventsList.Items.Add(item);
            }
        }
        finally
        {
            _eventsList.EndUpdate();
        }

        _eventCount.Text = $"显示 {events.Count} / 共 {_allEvents.Count} 条";
    }

    public void ShowConfigurationProblem(string message)
    {
        _stateValue.Text = "配置不可用";
        _lastCheckValue.Text = "未执行";
        _nextCheckValue.Text = "等待修正配置";
        _modeHint.Text = message;
    }

    public void PerformPauseToggle()
    {
        TogglePaused();
    }

    public void AllowExit()
    {
        _allowExit = true;
    }

    private void TogglePaused()
    {
        IsPaused = !IsPaused;
        _pauseButton.Text = IsPaused ? "恢复监控" : "暂停监控";
        _checkNowButton.Enabled = !IsPaused;
        _stateValue.Text = IsPaused
            ? _isDemoMode ? "已暂停（安全演示）" : "已暂停（尚未配置）"
            : _isDemoMode ? GetStateText(MonitorState.Healthy) : "尚未配置";
        _nextCheckValue.Text = IsPaused ? "已暂停" : "等待配置";
        PauseToggled?.Invoke(this, EventArgs.Empty);
    }

    private static string GetStateText(MonitorState state)
    {
        return state switch
        {
            MonitorState.NotConfigured => "尚未配置",
            MonitorState.Checking => "正在确认",
            MonitorState.Healthy => "正常",
            MonitorState.ServerOffline => "服务器离线",
            MonitorState.MappingMissing => "映射缺失",
            MonitorState.Repairing => "正在修复",
            MonitorState.AuthenticationFailed => "认证失败",
            MonitorState.ShareUnavailable => "共享不可用",
            MonitorState.DriveConflict => "盘符冲突",
            MonitorState.Paused => "已暂停",
            MonitorState.InternalError => "内部错误",
            _ => "未知状态",
        };
    }

    private void HandleFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_allowExit || eventArgs.CloseReason == CloseReason.WindowsShutDown)
        {
            return;
        }

        eventArgs.Cancel = true;
        Hide();
    }

    private void BuildLayout()
    {
        var title = new Label
        {
            Text = _isDemoMode ? "网络驱动器监控（安全演示）" : "网络驱动器监控",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 12),
        };

        var stateGrid = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Dock = DockStyle.Top,
        };
        stateGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        stateGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(stateGrid, "当前状态", _stateValue);
        AddRow(stateGrid, "上次检查", _lastCheckValue);
        AddRow(stateGrid, "上次变化", _lastChangeValue);
        AddRow(stateGrid, "下次检查", _nextCheckValue);
        AddRow(stateGrid, "最近错误", _lastErrorValue);

        var eventsTitle = new Label
        {
            Text = _isDemoMode ? "最近事件（演示模式不落盘）" : "最近事件",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 18, 0, 0),
        };

        var eventFilters = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 6, 0, 0),
        };
        eventFilters.Controls.Add(new Label
        {
            Text = "级别",
            AutoSize = true,
            Margin = new Padding(0, 5, 0, 0),
        });
        eventFilters.Controls.Add(_eventFilter);
        eventFilters.Controls.Add(new Label
        {
            Text = "状态",
            AutoSize = true,
            Margin = new Padding(0, 5, 0, 0),
        });
        eventFilters.Controls.Add(_eventStateFilter);
        eventFilters.Controls.Add(_eventCount);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 20, 0, 0),
        };
        actions.Controls.AddRange([_checkNowButton, _pauseButton, _hideButton]);

        var content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(24),
            AutoScroll = true,
        };
        content.Controls.AddRange(
            [title, _modeHint, stateGrid, eventsTitle, eventFilters, _eventsList, actions]);
        Controls.Add(content);
    }

    private static string GetSeverityText(MonitorEventSeverity severity)
    {
        return severity switch
        {
            MonitorEventSeverity.Information => "信息",
            MonitorEventSeverity.Warning => "警告",
            MonitorEventSeverity.Error => "错误",
            _ => "未知",
        };
    }

    private static Label CreateValueLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(3, 4, 3, 8),
        };
    }

    private static void AddRow(TableLayoutPanel panel, string name, Control value)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label
        {
            Text = name,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 4, 3, 8),
        }, 0, row);
        panel.Controls.Add(value, 1, row);
    }

    private sealed record EventFilterOption(string Text, MonitorEventFilter Filter)
    {
        public override string ToString() => Text;
    }

    private sealed record EventStateFilterOption(string Text, MonitorState? State)
    {
        public override string ToString() => Text;
    }
}
