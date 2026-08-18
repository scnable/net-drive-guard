using System.Threading.Channels;
using NetDriveGuard.Core.Abstractions;
using NetDriveGuard.Core.Configuration;
using NetDriveGuard.Core.Events;
using NetDriveGuard.Core.Monitoring;
using NetDriveGuard.Core.Notifications;
using NetDriveGuard.Core.Runtime;
using NetDriveGuard.Core.Scheduling;
using NetDriveGuard.Core.Simulation;
using NetDriveGuard.Windows;

namespace NetDriveGuard.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly Form1 _statusWindow;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _checkNowItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly System.Windows.Forms.Timer _scheduleTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IConfigurationStore _configurationStore;
    private readonly IMonitorEventStore? _eventStore;
    private readonly Channel<MonitorEvent>? _eventQueue;
    private readonly Task? _eventWriterTask;
    private readonly SynchronizationContext? _uiContext;
    private MonitorCoordinator _coordinator;
    private ScheduledMonitorRunner _runner;
    private readonly NotificationGate _notificationGate = new(TimeSpan.FromMinutes(5));
    private readonly RecentEventBuffer _recentEvents = new(100);
    private readonly bool _isDemoMode;
    private MonitorConfiguration? _currentConfiguration;
    private Task? _activeCheck;
    private Task? _backgroundInitialization;
    private MonitorState _lastDecisionState = MonitorState.NotConfigured;
    private int _eventPersistenceAvailable;
    private int _eventPersistenceFailureReported;
    private bool _isExiting;

    public TrayApplicationContext(bool isDemoMode = false)
    {
        _isDemoMode = isDemoMode;
        _statusWindow = new Form1(isDemoMode);
        _uiContext = SynchronizationContext.Current;
        _statusWindow.CheckRequested += HandleCheckRequested;
        _statusWindow.PauseToggled += HandlePauseToggled;

        _statusItem = new ToolStripMenuItem(
            isDemoMode ? "状态：演示准备中" : "状态：尚未配置")
        {
            Enabled = false,
        };
        _checkNowItem = new ToolStripMenuItem("立即检查", null, HandleCheckRequested);
        _pauseItem = new ToolStripMenuItem("暂停监控", null, (_, _) => TogglePaused());
        _settingsItem = new ToolStripMenuItem("设置", null, OpenSettings)
        {
            Enabled = !isDemoMode,
            Text = isDemoMode ? "设置（演示模式不可用）" : "设置",
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开状态面板", null, (_, _) => _statusWindow.ShowFromTray());
        menu.Items.Add(_checkNowItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = isDemoMode ? "NetDriveGuard：安全演示" : "NetDriveGuard：尚未配置",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => _statusWindow.ShowFromTray();

        var clock = new SystemClock();
        (_coordinator, _runner) = CreateRuntime(clock, null, isDemoMode);
        var applicationDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetDriveGuard");
        _configurationStore = new JsonFileConfigurationStore(
            Path.Combine(applicationDataDirectory, "config.json"));
        if (!isDemoMode)
        {
            _eventStore = new JsonLinesMonitorEventStore(
                Path.Combine(applicationDataDirectory, "events.jsonl"));
            _eventQueue = Channel.CreateUnbounded<MonitorEvent>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false,
                });
            _eventPersistenceAvailable = 1;
            _eventWriterTask = ProcessEventQueueAsync(_eventQueue.Reader);
        }

        _scheduleTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _scheduleTimer.Tick += HandleScheduleTimerTick;
        if (isDemoMode)
        {
            _scheduleTimer.Start();
            _ = RunScheduledAsync(ScheduleTrigger.ConfigurationSaved, showNoChecks: false);
        }
        else
        {
            _backgroundInitialization = InitializeAsync();
        }
    }

    private async Task InitializeAsync()
    {
        await LoadRecentEventsAsync();
        await LoadConfigurationAsync();
    }

    private async Task LoadRecentEventsAsync()
    {
        if (_eventStore is null)
        {
            return;
        }

        try
        {
            var events = await _eventStore.LoadRecentAsync(100, _lifetime.Token);
            for (var index = events.Count - 1; index >= 0; index--)
            {
                _recentEvents.TryAdd(events[index]);
            }

            _statusWindow.UpdateRecentEvents(_recentEvents.Snapshot());
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 应用退出时停止恢复历史，不显示额外错误。
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RecordEvent(
                new MonitorEvent(
                    DateTimeOffset.UtcNow,
                    MonitorState.NotConfigured,
                    MonitorEventSeverity.Warning,
                    "events.load-failed",
                    "历史事件无法读取，本次运行仍会继续监控。"),
                persist: false);
        }
    }

    private async Task LoadConfigurationAsync()
    {
        ConfigurationLoadResult result;
        try
        {
            result = await _configurationStore.LoadAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        if (result.IsLoaded && result.Configuration is not null)
        {
            await ApplyConfigurationAsync(result.Configuration);
            return;
        }

        if (result.Status is ConfigurationLoadStatus.Invalid
            or ConfigurationLoadStatus.UnsupportedVersion
            or ConfigurationLoadStatus.StorageError)
        {
            _statusWindow.ShowConfigurationProblem("配置文件无法安全加载，请打开设置重新保存。未执行任何网络检查。");
            _statusItem.Text = "状态：配置不可用";
            _trayIcon.Text = "NetDriveGuard：配置不可用";
            _trayIcon.Icon = SystemIcons.Error;
            RecordEvent(new MonitorEvent(
                DateTimeOffset.UtcNow,
                MonitorState.NotConfigured,
                MonitorEventSeverity.Error,
                "configuration.load-failed",
                "配置文件无法安全加载，网络检查未启动。"));
        }
    }

    private async void OpenSettings(object? sender, EventArgs eventArgs)
    {
        if (_isDemoMode || _isExiting)
        {
            return;
        }

        _scheduleTimer.Stop();
        if (_backgroundInitialization is not null)
        {
            await _backgroundInitialization;
        }

        if (_activeCheck is not null)
        {
            await _activeCheck;
        }

        using var settings = new SettingsForm(_currentConfiguration);
        var result = settings.ShowDialog(_statusWindow);
        if (result != DialogResult.OK || settings.SelectedConfiguration is null)
        {
            if (!_statusWindow.IsPaused)
            {
                _scheduleTimer.Start();
            }

            return;
        }

        var save = await _configurationStore.SaveAsync(
            settings.SelectedConfiguration,
            _lifetime.Token);
        if (!save.Success)
        {
            RecordEvent(new MonitorEvent(
                DateTimeOffset.UtcNow,
                _coordinator.Snapshot.CurrentState,
                MonitorEventSeverity.Error,
                "configuration.save-failed",
                "配置保存失败，现有监控状态未改变。"));
            MessageBox.Show(
                _statusWindow,
                "配置保存失败，现有监控状态未改变。",
                "NetDriveGuard",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            if (!_statusWindow.IsPaused)
            {
                _scheduleTimer.Start();
            }

            return;
        }

        await ApplyConfigurationAsync(settings.SelectedConfiguration);
        MessageBox.Show(
            _statusWindow,
            "配置已保存并进入只读观察模式。",
            "NetDriveGuard",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private async Task ApplyConfigurationAsync(MonitorConfiguration configuration)
    {
        _scheduleTimer.Stop();
        if (_activeCheck is not null)
        {
            await _activeCheck;
        }

        var oldCoordinator = _coordinator;
        var clock = new SystemClock();
        (_coordinator, _runner) = CreateRuntime(clock, configuration, demoMode: false);
        oldCoordinator.Dispose();
        _currentConfiguration = configuration;
        _lastDecisionState = _coordinator.Snapshot.CurrentState;
        _statusWindow.SetConfiguredReadOnlyMode();
        RecordEvent(new MonitorEvent(
            DateTimeOffset.UtcNow,
            _coordinator.Snapshot.CurrentState,
            MonitorEventSeverity.Information,
            "configuration.applied",
            "配置已应用，只读监控已启动。"));

        await RunScheduledAsync(ScheduleTrigger.ConfigurationSaved, showNoChecks: false);
        if (!_statusWindow.IsPaused && !_isExiting)
        {
            _scheduleTimer.Start();
        }
    }

    private async void HandleCheckRequested(object? sender, EventArgs eventArgs)
    {
        await RunScheduledAsync(ScheduleTrigger.Manual, showNoChecks: true);
    }

    private void HandlePauseToggled(object? sender, EventArgs eventArgs)
    {
        UpdatePausedPresentation();

        if (_statusWindow.IsPaused)
        {
            _scheduleTimer.Stop();
            RecordEvent(new MonitorEvent(
                DateTimeOffset.UtcNow,
                MonitorState.Paused,
                MonitorEventSeverity.Information,
                "monitor.paused",
                "监控已暂停。"));
            return;
        }

        _scheduleTimer.Start();
        RecordEvent(new MonitorEvent(
            DateTimeOffset.UtcNow,
            _coordinator.Snapshot.CurrentState,
            MonitorEventSeverity.Information,
            "monitor.resumed",
            "监控已恢复运行。"));
        _ = RunScheduledAsync(ScheduleTrigger.Resume, showNoChecks: false);
    }

    private void TogglePaused()
    {
        _statusWindow.PerformPauseToggle();
    }

    private void UpdatePausedPresentation()
    {
        _pauseItem.Text = _statusWindow.IsPaused ? "恢复监控" : "暂停监控";
        _checkNowItem.Enabled = !_statusWindow.IsPaused;
        if (_statusWindow.IsPaused)
        {
            _statusItem.Text = "状态：已暂停";
            _trayIcon.Text = _isDemoMode
                ? "NetDriveGuard：安全演示已暂停"
                : "NetDriveGuard：已暂停";
            _trayIcon.Icon = SystemIcons.Application;
            return;
        }

        _statusWindow.ApplySnapshot(_coordinator.Snapshot);
        UpdateTrayPresentation(_coordinator.Snapshot.CurrentState);
    }

    private async void HandleScheduleTimerTick(object? sender, EventArgs eventArgs)
    {
        await RunScheduledAsync(ScheduleTrigger.Timer, showNoChecks: false);
    }

    private async Task RunScheduledAsync(ScheduleTrigger trigger, bool showNoChecks)
    {
        if (_isExiting || _statusWindow.IsPaused)
        {
            return;
        }

        if (_activeCheck is { IsCompleted: false })
        {
            return;
        }

        _activeCheck = ExecuteScheduledAsync(trigger, showNoChecks);
        await _activeCheck;
    }

    private async Task ExecuteScheduledAsync(ScheduleTrigger trigger, bool showNoChecks)
    {
        var result = await _runner.RunAsync(trigger, _lifetime.Token);
        if (result.Run.Status == MonitorRunStatus.SkippedNoChecks)
        {
            if (showNoChecks)
            {
                _statusWindow.RecordUnconfiguredCheckRequest();
                RecordEvent(new MonitorEvent(
                    DateTimeOffset.UtcNow,
                    MonitorState.NotConfigured,
                    MonitorEventSeverity.Warning,
                    "check.skipped-not-configured",
                    "未配置监控目标，本次检查已跳过。"));
            }

            return;
        }

        if (result.Run.Status != MonitorRunStatus.Completed || result.Run.Decision is null)
        {
            return;
        }

        var decision = result.Run.Decision;
        var monitorEvent = MonitorEventFactory.Create(decision, _lastDecisionState);
        _lastDecisionState = decision.Snapshot.CurrentState;
        if (monitorEvent is not null)
        {
            RecordEvent(monitorEvent);
        }

        _statusWindow.ApplySnapshot(decision.Snapshot);
        UpdateTrayPresentation(decision.Snapshot.CurrentState);
        PublishNotification(decision);
    }

    private void UpdateTrayPresentation(MonitorState state)
    {
        var stateText = GetStateText(state);
        _statusItem.Text = $"状态：{stateText}";
        _trayIcon.Text = _isDemoMode
            ? $"NetDriveGuard 演示：{stateText}"
            : $"NetDriveGuard：{stateText}";
        _trayIcon.Icon = state switch
        {
            MonitorState.Healthy => SystemIcons.Information,
            MonitorState.NotConfigured or MonitorState.Paused => SystemIcons.Application,
            MonitorState.Checking or MonitorState.Repairing => SystemIcons.Warning,
            _ => SystemIcons.Error,
        };
    }

    private void PublishNotification(MonitorDecision decision)
    {
        if (!decision.ShouldNotify
            || !_notificationGate.TryRegister(decision.EventCode, decision.Snapshot.LastObservationAt))
        {
            return;
        }

        var recovered = decision.Snapshot.CurrentState == MonitorState.Healthy;
        _trayIcon.ShowBalloonTip(
            5000,
            recovered
                ? _isDemoMode ? "[演示] 网络驱动器已恢复" : "网络驱动器已恢复"
                : _isDemoMode ? "[演示] 网络驱动器状态发生变化" : "网络驱动器状态发生变化",
            $"当前状态：{GetStateText(decision.Snapshot.CurrentState)}",
            recovered ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    private void RecordEvent(MonitorEvent monitorEvent, bool persist = true)
    {
        if (_recentEvents.TryAdd(monitorEvent))
        {
            _statusWindow.UpdateRecentEvents(_recentEvents.Snapshot());
            if (persist
                && Volatile.Read(ref _eventPersistenceAvailable) == 1)
            {
                _eventQueue?.Writer.TryWrite(monitorEvent);
            }
        }
    }

    private async Task ProcessEventQueueAsync(ChannelReader<MonitorEvent> reader)
    {
        if (_eventStore is null)
        {
            return;
        }

        await foreach (var monitorEvent in reader.ReadAllAsync())
        {
            try
            {
                await _eventStore.AppendAsync(monitorEvent);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                Interlocked.Exchange(ref _eventPersistenceAvailable, 0);
                ReportEventPersistenceFailure();
                return;
            }
        }
    }

    private void ReportEventPersistenceFailure()
    {
        if (Interlocked.Exchange(ref _eventPersistenceFailureReported, 1) != 0
            || _uiContext is null)
        {
            return;
        }

        _uiContext.Post(
            _ =>
            {
                if (!_isExiting)
                {
                    RecordEvent(
                        new MonitorEvent(
                            DateTimeOffset.UtcNow,
                            _coordinator.Snapshot.CurrentState,
                            MonitorEventSeverity.Warning,
                            "events.write-failed",
                            "事件文件无法写入，状态仍保留在本次运行内。"),
                        persist: false);
                }
            },
            null);
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

    private static (MonitorCoordinator Coordinator, ScheduledMonitorRunner Runner) CreateRuntime(
        IClock clock,
        MonitorConfiguration? configuration,
        bool demoMode)
    {
        IProbe<MonitorObservation> probe;
        MonitoringIntervals intervals;
        MonitorPolicy policy;

        if (demoMode)
        {
            probe = new ScriptedMonitorProbe(
                clock,
                [
                    ObservationStatus.Healthy,
                    ObservationStatus.ServerUnreachable,
                    ObservationStatus.ServerUnreachable,
                    ObservationStatus.ServerUnreachable,
                    ObservationStatus.ServerUnreachable,
                    ObservationStatus.Healthy,
                    ObservationStatus.AuthenticationFailed,
                    ObservationStatus.Healthy,
                ]);
            intervals = MonitoringIntervals.ForProfile(MonitoringProfile.Default);
            policy = new MonitorPolicy(
                RemoveMappingWhenOffline: false,
                RepairMissingMapping: false);
        }
        else if (configuration is not null)
        {
            probe = new ReadOnlyNetworkDriveProbe(configuration, clock);
            intervals = MonitoringIntervals.ForProfile(
                configuration.Profile,
                configuration.CustomTcpInterval);
            policy = new MonitorPolicy(
                configuration.OfflineFailureThreshold,
                RemoveMappingWhenOffline: false,
                RepairMissingMapping: false);
        }
        else
        {
            probe = new UnconfiguredMonitorProbe(clock);
            intervals = MonitoringIntervals.ForProfile(MonitoringProfile.Default);
            policy = new MonitorPolicy(
                RemoveMappingWhenOffline: false,
                RepairMissingMapping: false);
        }

        var coordinator = new MonitorCoordinator(
            probe,
            new ReadOnlyRemediation(),
            clock,
            new MonitorStateMachine(policy),
            MonitorSnapshot.Initial(clock.UtcNow));
        var runner = new ScheduledMonitorRunner(
            coordinator,
            new LayeredSchedulePlanner(intervals),
            clock);
        return (coordinator, runner);
    }

    private async void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _scheduleTimer.Stop();
        await _lifetime.CancelAsync();
        if (_backgroundInitialization is not null)
        {
            await _backgroundInitialization;
        }

        if (_activeCheck is not null)
        {
            await _activeCheck;
        }

        _eventQueue?.Writer.TryComplete();
        if (_eventWriterTask is not null)
        {
            await _eventWriterTask;
        }

        _trayIcon.Visible = false;
        _statusWindow.AllowExit();
        _statusWindow.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scheduleTimer.Dispose();
            _lifetime.Dispose();
            _coordinator.Dispose();
            (_eventStore as IDisposable)?.Dispose();
            _trayIcon.Dispose();
            _statusWindow.Dispose();
        }

        base.Dispose(disposing);
    }
}
