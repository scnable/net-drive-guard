using NetDriveGuard.Core.Localization;
using NetDriveGuard.Core;
using NetDriveGuard.Core.Events;
using NetDriveGuard.Core.Monitoring;

namespace NetDriveGuard.App;

public partial class Form1 : Form
{
    private readonly Label _stateValue = CreateValueLabel(AppText.Get("尚未配置"));
    private readonly Label _lastCheckValue = CreateValueLabel(AppText.Get("尚未执行"));
    private readonly Label _lastChangeValue = CreateValueLabel(AppText.Get("尚无记录"));
    private readonly Label _nextCheckValue = CreateValueLabel(AppText.Get("等待配置"));
    private readonly Label _lastErrorValue = new()
    {
        Text = AppText.Get("尚无记录"),
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
        Text = AppText.Get("显示 0 / 共 0 条"),
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 5, 0, 0),
    };
    private readonly Button _checkNowButton = new() { Text = AppText.Get("立即检查"), AutoSize = true };
    private readonly Button _pauseButton = new() { Text = AppText.Get("暂停监控"), AutoSize = true };
    private readonly Button _hideButton = new() { Text = AppText.Get("隐藏到托盘"), AutoSize = true };
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
            ? AppText.Get("演示状态由内置脚本生成，不访问网络，也不会连接或断开网络驱动器。")
            : AppText.Get("当前为只读原型，尚未配置监控目标，不会连接或断开网络驱动器。");
        InitializeComponent();
        BuildLayout();
        Text = AppText.Format("NetDriveGuard {0} 监控面板", CoreModule.PrototypeVersion);
        FormClosing += HandleFormClosing;
        _checkNowButton.Click += (_, _) => CheckRequested?.Invoke(this, EventArgs.Empty);
        _pauseButton.Click += (_, _) => TogglePaused();
        _hideButton.Click += (_, _) => Hide();
        _eventsList.Columns.Add(AppText.Get("时间"), 145);
        _eventsList.Columns.Add(AppText.Get("级别"), AppText.Language == "en" ? 75 : 60);
        _eventsList.Columns.Add(AppText.Get("状态"), AppText.Language == "en" ? 155 : 90);
        _eventsList.Columns.Add(AppText.Get("事件"), 280);
        _eventFilter.Items.AddRange(
        [
            new EventFilterOption(AppText.Get("全部事件"), MonitorEventFilter.All),
            new EventFilterOption(AppText.Get("警告及错误"), MonitorEventFilter.WarningAndError),
            new EventFilterOption(AppText.Get("仅错误"), MonitorEventFilter.ErrorOnly),
        ]);
        _eventFilter.SelectedIndexChanged += (_, _) => RefreshEventList();
        _eventFilter.SelectedIndex = 0;
        _eventStateFilter.Items.Add(new EventStateFilterOption(AppText.Get("全部状态"), null));
        foreach (var state in Enum.GetValues<MonitorState>())
        {
            _eventStateFilter.Items.Add(
                new EventStateFilterOption(GetStateText(state), state));
        }

        _eventStateFilter.SelectedIndexChanged += (_, _) => RefreshEventList();
        _eventStateFilter.SelectedIndex = 0;
        if (AppText.Language == "en")
        {
            _eventFilter.Width = 170;
            _eventStateFilter.Width = 185;
        }
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        WindowPlacement.FitToWorkingArea(this);
    }

    public void RecordUnconfiguredCheckRequest()
    {
        _lastCheckValue.Text = AppText.Format("{0}（未配置，未探测）", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        _stateValue.Text = AppText.Get("尚未配置");
        _nextCheckValue.Text = AppText.Get("等待配置");
    }

    public void ApplySnapshot(MonitorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _stateValue.Text = GetStateText(snapshot.CurrentState);
        _lastCheckValue.Text = snapshot.LastObservationAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        _lastChangeValue.Text = snapshot.LastTransitionAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        _nextCheckValue.Text = snapshot.CurrentState switch
        {
            MonitorState.NotConfigured => AppText.Get("等待配置"),
            MonitorState.Paused => AppText.Get("已暂停"),
            _ => AppText.Get("按动态周期"),
        };
    }

    /// <summary>分别说明离线处理与恢复重连是否启用，避免把仅观察误认为已在自动维护。</summary>
    public void SetConfiguredMode(bool repairMissingMapping, bool removeMappingWhenOffline)
    {
        _modeHint.Text = (repairMissingMapping, removeMappingWhenOffline) switch
        {
            (false, false) =>
                AppText.Get("仅监控提醒：未启用离线取消或恢复重连。需要自动维护时，请在常用设置中选择。"),
            (true, false) =>
                AppText.Get("已启用恢复重连：服务器可用且映射缺失时尝试连接；离线时不取消映射。"),
            (false, true) =>
                AppText.Get("已启用离线取消：确认离线后取消目标映射；恢复重连未开启，需要手动连接。"),
            _ =>
                AppText.Get("自动维护已启用：按当前配置管理所选盘符，正常断开旧映射后连接新目标，不强制关闭文件。"),
        };
    }

    public void UpdateRecentEvents(IReadOnlyList<MonitorEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        _allEvents = events.ToArray();
        var lastError = _allEvents.FirstOrDefault(
            monitorEvent => monitorEvent.Severity == MonitorEventSeverity.Error);
        _lastErrorValue.Text = lastError is null
            ? AppText.Get("尚无记录")
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

        _eventCount.Text = AppText.Format("显示 {0} / 共 {1} 条", events.Count, _allEvents.Count);
    }

    public void ShowConfigurationProblem(string message)
    {
        _stateValue.Text = AppText.Get("配置不可用");
        _lastCheckValue.Text = AppText.Get("未执行");
        _nextCheckValue.Text = AppText.Get("等待修正配置");
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
        _pauseButton.Text = IsPaused ? AppText.Get("恢复监控") : AppText.Get("暂停监控");
        _checkNowButton.Enabled = !IsPaused;
        _stateValue.Text = IsPaused
            ? _isDemoMode ? AppText.Get("已暂停（安全演示）") : AppText.Get("已暂停（尚未配置）")
            : _isDemoMode ? GetStateText(MonitorState.Healthy) : AppText.Get("尚未配置");
        _nextCheckValue.Text = IsPaused ? AppText.Get("已暂停") : AppText.Get("等待配置");
        PauseToggled?.Invoke(this, EventArgs.Empty);
    }

    private static string GetStateText(MonitorState state)
    {
        return state switch
        {
            MonitorState.NotConfigured => AppText.Get("尚未配置"),
            MonitorState.Checking => AppText.Get("正在确认"),
            MonitorState.Healthy => AppText.Get("正常"),
            MonitorState.ServerOffline => AppText.Get("服务器离线"),
            MonitorState.MappingMissing => AppText.Get("映射缺失"),
            MonitorState.Repairing => AppText.Get("正在修复"),
            MonitorState.AuthenticationFailed => AppText.Get("认证失败"),
            MonitorState.ShareUnavailable => AppText.Get("共享不可用"),
            MonitorState.DriveConflict => AppText.Get("盘符冲突"),
            MonitorState.Paused => AppText.Get("已暂停"),
            MonitorState.InternalError => AppText.Get("内部错误"),
            MonitorState.MappingFailed => AppText.Get("网络盘操作失败"),
            _ => AppText.Get("未知状态"),
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
            Text = _isDemoMode ? AppText.Get("网络驱动器监控（安全演示）") : AppText.Get("网络驱动器监控"),
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
        stateGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        stateGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(stateGrid, AppText.Get("当前状态"), _stateValue);
        AddRow(stateGrid, AppText.Get("上次检查"), _lastCheckValue);
        AddRow(stateGrid, AppText.Get("上次变化"), _lastChangeValue);
        AddRow(stateGrid, AppText.Get("下次检查"), _nextCheckValue);
        AddRow(stateGrid, AppText.Get("最近错误"), _lastErrorValue);

        var eventsTitle = new Label
        {
            Text = _isDemoMode ? AppText.Get("最近事件（演示模式不落盘）") : AppText.Get("最近事件"),
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
            Text = AppText.Get("级别"),
            AutoSize = true,
            Margin = new Padding(0, 5, 0, 0),
        });
        eventFilters.Controls.Add(_eventFilter);
        eventFilters.Controls.Add(new Label
        {
            Text = AppText.Get("状态"),
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

        // 使用单列表格给每一行完整宽度，避免 FlowLayoutPanel 把状态值挤成竖排。
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(20),
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 7; row++)
        {
            content.RowStyles.Add(new RowStyle(row == 5 ? SizeType.Percent : SizeType.AutoSize, row == 5 ? 100 : 0));
        }

        _modeHint.Dock = DockStyle.Fill;
        stateGrid.Dock = DockStyle.Fill;
        _eventsList.Dock = DockStyle.Fill;
        eventFilters.Dock = DockStyle.Fill;
        eventFilters.WrapContents = true;
        actions.Dock = DockStyle.Fill;
        Control[] rows = [title, _modeHint, stateGrid, eventsTitle, eventFilters, _eventsList, actions];
        for (var row = 0; row < rows.Length; row++)
        {
            content.Controls.Add(rows[row], 0, row);
        }

        Controls.Add(content);
    }

    private static string GetSeverityText(MonitorEventSeverity severity)
    {
        return severity switch
        {
            MonitorEventSeverity.Information => AppText.Get("信息"),
            MonitorEventSeverity.Warning => AppText.Get("警告"),
            MonitorEventSeverity.Error => AppText.Get("错误"),
            _ => AppText.Get("未知"),
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
