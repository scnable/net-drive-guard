using NetDriveGuard.Core.Localization;
using System.Threading.Channels;
using NetDriveGuard.Core;
using NetDriveGuard.Core.Abstractions;
using NetDriveGuard.Core.Configuration;
using NetDriveGuard.Core.Diagnostics;
using NetDriveGuard.Core.Events;
using NetDriveGuard.Core.Monitoring;
using NetDriveGuard.Core.Notifications;
using NetDriveGuard.Core.Runtime;
using NetDriveGuard.Core.Scheduling;
using NetDriveGuard.Core.Security;
using NetDriveGuard.Core.Simulation;
using NetDriveGuard.Core.Startup;
using System.Security.Principal;
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
    private readonly ToolStripMenuItem _diagnosticsItem;
    private readonly System.Windows.Forms.Timer _scheduleTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IConfigurationStore _configurationStore;
    private readonly ApplicationDataStore _dataStore;
    private readonly ICredentialStore _credentialStore;
    private readonly IStartupManager _startupManager;
    private readonly IMonitorEventStore? _eventStore;
    private readonly Channel<MonitorEvent>? _eventQueue;
    private readonly Task? _eventWriterTask;
    private readonly SystemChangeSignalQueue _systemChangeSignals = new();
    private readonly WindowsSystemChangeMonitor? _systemChangeMonitor;
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
            isDemoMode ? AppText.Get("状态：演示准备中") : AppText.Get("状态：尚未配置"))
        {
            Enabled = false,
        };
        _checkNowItem = new ToolStripMenuItem(AppText.Get("立即检查"), null, HandleCheckRequested);
        _pauseItem = new ToolStripMenuItem(AppText.Get("暂停监控"), null, (_, _) => TogglePaused());
        _settingsItem = new ToolStripMenuItem(AppText.Get("设置"), null, OpenSettings)
        {
            Enabled = !isDemoMode,
            Text = isDemoMode ? AppText.Get("设置（演示模式不可用）") : AppText.Get("设置"),
        };
        _diagnosticsItem = new ToolStripMenuItem(AppText.Get("导出脱敏诊断..."), null, ExportDiagnostics)
        {
            Enabled = !isDemoMode,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(AppText.Get("打开状态面板"), null, (_, _) => _statusWindow.ShowFromTray());
        menu.Items.Add(_checkNowItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_settingsItem);
        menu.Items.Add(_diagnosticsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(AppText.Get("退出"), null, (_, _) => ExitApplication());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = isDemoMode ? AppText.Get("NetDriveGuard：安全演示") : AppText.Get("NetDriveGuard：尚未配置"),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => _statusWindow.ShowFromTray();

        var clock = new SystemClock();
        _credentialStore = new WindowsCredentialStore();
        _startupManager = new TaskSchedulerStartupManager();
        (_coordinator, _runner) = CreateRuntime(
            clock,
            null,
            isDemoMode,
            _credentialStore);
        var applicationDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetDriveGuard");
        _dataStore = new ApplicationDataStore(applicationDataDirectory);
        _configurationStore = _dataStore;
        if (!isDemoMode)
        {
            _eventStore = _dataStore;
            _eventQueue = Channel.CreateUnbounded<MonitorEvent>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false,
                });
            _eventPersistenceAvailable = 1;
            _eventWriterTask = ProcessEventQueueAsync(_eventQueue.Reader);
            _systemChangeMonitor = new WindowsSystemChangeMonitor(_systemChangeSignals.Publish);
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

    /// <summary>先恢复最近事件，再加载用户配置；没有配置时打开设置。</summary>
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
                    AppText.Get("历史事件无法读取，本次运行仍会继续监控。")),
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
            // 旧脚本只需迁移时手动停用，不以某个任务名称决定是否允许监控。
            await ApplyConfigurationAsync(result.Configuration);
            return;
        }

        if (result.Status == ConfigurationLoadStatus.Missing)
        {
            _statusWindow.ShowConfigurationProblem(AppText.Get("首次运行尚未配置，设置窗口即将打开；保存前不会访问网络或修改映射。"));
            _uiContext?.Post(
                _ => OpenSettings(null, EventArgs.Empty),
                null);
            return;
        }

        if (result.Status is ConfigurationLoadStatus.Invalid
            or ConfigurationLoadStatus.UnsupportedVersion
            or ConfigurationLoadStatus.StorageError)
        {
            _statusWindow.ShowConfigurationProblem(AppText.Get("配置文件无法安全加载，请打开设置重新保存。未执行任何网络检查。"));
            _statusItem.Text = AppText.Get("状态：配置不可用");
            _trayIcon.Text = AppText.Get("NetDriveGuard：配置不可用");
            _trayIcon.Icon = SystemIcons.Error;
            RecordEvent(new MonitorEvent(
                DateTimeOffset.UtcNow,
                MonitorState.NotConfigured,
                MonitorEventSeverity.Error,
                "configuration.load-failed",
                AppText.Get("配置文件无法安全加载，网络检查未启动。")));
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

        var executablePath = Environment.ProcessPath;
        var settingsConfiguration = _currentConfiguration;
        StartupRegistrationState? startupState = null;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            startupState = await _startupManager.QueryAsync(executablePath, _lifetime.Token);
            if (settingsConfiguration is not null
                && startupState.Status is StartupRegistrationStatus.Registered
                    or StartupRegistrationStatus.NotRegistered)
            {
                settingsConfiguration = settingsConfiguration with
                {
                    StartAtLogon = startupState.IsRegistered,
                };
            }
        }

        using var settings = new SettingsForm(settingsConfiguration, _dataStore.DataDirectory);
        var result = settings.ShowDialog(_statusWindow);
        if (result != DialogResult.OK || settings.SelectedConfiguration is null)
        {
            if (!_statusWindow.IsPaused)
            {
                _scheduleTimer.Start();
            }

            return;
        }

        var selectedConfiguration = settings.SelectedConfiguration;
        using var pendingCredential = settings.TakePendingCredential();
        if (pendingCredential is not null)
        {
            var credentialSave = _credentialStore.Write(
                CredentialTargetName.ForMapping(
                    selectedConfiguration.DriveLetter,
                    selectedConfiguration.SharePath),
                pendingCredential.UserName,
                pendingCredential.Password);
            if (!credentialSave.Success)
            {
                RecordEvent(new MonitorEvent(
                    DateTimeOffset.UtcNow,
                    _coordinator.Snapshot.CurrentState,
                    MonitorEventSeverity.Error,
                    "credential.save-failed",
                    AppText.Get("Windows 凭据保存失败，配置未应用。")));
                MessageBox.Show(
                    _statusWindow,
                    AppText.Get("Windows 凭据保存失败，配置未应用。密码没有写入配置文件。"),
                    "NetDriveGuard",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                if (!_statusWindow.IsPaused)
                {
                    _scheduleTimer.Start();
                }

                return;
            }
        }

        if (startupState?.RequiresUpdate(selectedConfiguration.StartAtLogon)
            ?? (selectedConfiguration.StartAtLogon != (settingsConfiguration?.StartAtLogon ?? false)))
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                ShowStartupOperationFailure();
                return;
            }

            StartupOperationResult startupResult;
            if (selectedConfiguration.StartAtLogon)
            {
                var userSid = WindowsIdentity.GetCurrent().User?.Value;
                if (string.IsNullOrWhiteSpace(userSid))
                {
                    ShowStartupOperationFailure();
                    return;
                }

                startupResult = await _startupManager.EnableAsync(
                    executablePath,
                    userSid,
                    _lifetime.Token);
            }
            else
            {
                startupResult = await _startupManager.DisableAsync(
                    executablePath,
                    _lifetime.Token);
            }

            if (!startupResult.Success)
            {
                ShowStartupOperationFailure();
                return;
            }
        }

        var save = await _dataStore.SaveAsync(
            selectedConfiguration,
            settings.SelectedDataDirectory,
            _lifetime.Token);
        if (!save.Success)
        {
            RecordEvent(new MonitorEvent(
                DateTimeOffset.UtcNow,
                _coordinator.Snapshot.CurrentState,
                MonitorEventSeverity.Error,
                "configuration.save-failed",
                AppText.Get("配置保存失败，现有监控状态未改变。")));
            MessageBox.Show(
                _statusWindow,
                save.Code switch
                {
                    "storage.destination-conflict" => AppText.Get("目标目录已有配置或日志，请选择其他目录。没有覆盖原文件，现有监控不变。"),
                    "storage.monitored-drive" => AppText.Get("保存目录不能位于被监控的网络盘。请选择本机磁盘目录。"),
                    "storage.directory-switch-failed" => AppText.Get("文件保存失败，请检查目录权限和可用空间。仍使用旧目录；新目录可能留有部分复制文件，未删除任何原文件。"),
                    _ => AppText.Get("配置保存失败，现有监控状态未改变。"),
                },
                "NetDriveGuard",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            if (!_statusWindow.IsPaused)
            {
                _scheduleTimer.Start();
            }

            return;
        }

        await ApplyConfigurationAsync(selectedConfiguration);
        var operationMode = selectedConfiguration.RepairMissingMapping
            || selectedConfiguration.RemoveMappingWhenOffline
            ? AppText.Get("受控自动操作模式")
            : AppText.Get("只读观察模式");
        MessageBox.Show(
            _statusWindow,
            (_statusWindow.IsPaused
                ? AppText.Get("配置已保存，监控仍暂停；恢复监控后开始检查。")
                : AppText.Format("配置已保存并进入{0}。", operationMode))
                + (selectedConfiguration.Language != AppText.Language
                    ? Environment.NewLine + AppText.Get("语言设置已保存，请退出程序后重新打开以切换语言。")
                    : string.Empty),
            "NetDriveGuard",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void ShowStartupOperationFailure()
    {
        RecordEvent(new MonitorEvent(
            DateTimeOffset.UtcNow,
            _coordinator.Snapshot.CurrentState,
            MonitorEventSeverity.Error,
            "startup.update-failed",
            AppText.Get("开机自启动设置更新失败，监控配置未应用。")));
        MessageBox.Show(
            _statusWindow,
            AppText.Get("开机自启动设置更新失败，监控配置未应用。系统中已有的任务不会被覆盖或删除。"),
            "NetDriveGuard",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
        if (!_statusWindow.IsPaused)
        {
            _scheduleTimer.Start();
        }
    }

    /// <summary>等待当前检查结束后替换配置，避免新旧监控同时操作同一个盘符。</summary>
    private async Task ApplyConfigurationAsync(MonitorConfiguration configuration)
    {
        _scheduleTimer.Stop();
        if (_activeCheck is not null)
        {
            await _activeCheck;
        }

        var oldCoordinator = _coordinator;
        var clock = new SystemClock();
        (_coordinator, _runner) = CreateRuntime(
            clock,
            configuration,
            demoMode: false,
            _credentialStore);
        oldCoordinator.Dispose();
        _currentConfiguration = configuration;
        _lastDecisionState = _coordinator.Snapshot.CurrentState;
        _statusWindow.SetConfiguredMode(
            configuration.RepairMissingMapping,
            configuration.RemoveMappingWhenOffline);
        var managedOperationsEnabled = configuration.RepairMissingMapping
            || configuration.RemoveMappingWhenOffline;
        RecordEvent(new MonitorEvent(
            DateTimeOffset.UtcNow,
            _coordinator.Snapshot.CurrentState,
            MonitorEventSeverity.Information,
            "configuration.applied",
            _statusWindow.IsPaused
                ? AppText.Get("配置已应用，监控仍暂停；恢复监控后开始检查。")
                : managedOperationsEnabled
                    ? AppText.Get("配置已应用，将按设置检查并执行自动操作。")
                    : AppText.Get("配置已应用，将开始只读检查。")));

        _systemChangeSignals.Clear();
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
                AppText.Get("监控已暂停。")));
            return;
        }

        _scheduleTimer.Start();
        RecordEvent(new MonitorEvent(
            DateTimeOffset.UtcNow,
            _coordinator.Snapshot.CurrentState,
            MonitorEventSeverity.Information,
            "monitor.resumed",
            AppText.Get("监控已恢复运行。")));
        _systemChangeSignals.Clear();
        _ = RunScheduledAsync(ScheduleTrigger.Resume, showNoChecks: false);
    }

    private void TogglePaused()
    {
        _statusWindow.PerformPauseToggle();
    }

    private void UpdatePausedPresentation()
    {
        _pauseItem.Text = _statusWindow.IsPaused ? AppText.Get("恢复监控") : AppText.Get("暂停监控");
        _checkNowItem.Enabled = !_statusWindow.IsPaused;
        if (_statusWindow.IsPaused)
        {
            _statusItem.Text = AppText.Get("状态：已暂停");
            _trayIcon.Text = _isDemoMode
                ? AppText.Get("NetDriveGuard：安全演示已暂停")
                : AppText.Get("NetDriveGuard：已暂停");
            _trayIcon.Icon = SystemIcons.Application;
            return;
        }

        _statusWindow.ApplySnapshot(_coordinator.Snapshot);
        UpdateTrayPresentation(_coordinator.Snapshot.CurrentState);
    }

    private async void HandleScheduleTimerTick(object? sender, EventArgs eventArgs)
    {
        if (_activeCheck is { IsCompleted: false })
        {
            return;
        }

        if (_systemChangeSignals.TryTake(out var signal))
        {
            var trigger = signal == SystemChangeSignal.PowerResumed
                ? ScheduleTrigger.Resume
                : ScheduleTrigger.NetworkChanged;
            await RunScheduledAsync(trigger, showNoChecks: false);
            return;
        }

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
                    AppText.Get("未配置监控目标，本次检查已跳过。")));
            }

            return;
        }

        if (result.Run.Status != MonitorRunStatus.Completed || result.Run.Decision is null)
        {
            return;
        }

        var decision = result.Run.Decision;
        var monitorEvent = MonitorEventFactory.Create(decision, _lastDecisionState, result.Run.Remediation);
        _lastDecisionState = decision.Snapshot.CurrentState;
        if (monitorEvent is not null)
        {
            RecordEvent(monitorEvent);
        }

        _statusWindow.ApplySnapshot(decision.Snapshot);
        UpdateTrayPresentation(decision.Snapshot.CurrentState);
        PublishNotification(decision);
    }

    private async void ExportDiagnostics(object? sender, EventArgs eventArgs)
    {
        if (_isDemoMode || _isExiting)
        {
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = AppText.Get("导出 NetDriveGuard 脱敏诊断"),
            Filter = AppText.Get("JSON 文件 (*.json)|*.json"),
            FileName = $"NetDriveGuard-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            InitialDirectory = _dataStore.DataDirectory,
            AddExtension = true,
            DefaultExt = "json",
            OverwritePrompt = true,
            RestoreDirectory = true,
        };
        if (dialog.ShowDialog(_statusWindow) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var report = DiagnosticReportJson.Serialize(
                CoreModule.PrototypeVersion,
                DateTimeOffset.UtcNow,
                _currentConfiguration,
                _coordinator.Snapshot,
                _recentEvents.Snapshot());
            await File.WriteAllTextAsync(
                dialog.FileName,
                report,
                new System.Text.UTF8Encoding(false),
                _lifetime.Token);
            MessageBox.Show(
                _statusWindow,
                AppText.Get("脱敏诊断已导出。报告不包含共享地址、用户名、密码、事件描述或本机路径。"),
                "NetDriveGuard",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 应用退出时停止导出。
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                _statusWindow,
                AppText.Get("诊断导出失败，请选择其他可写目录后重试。"),
                "NetDriveGuard",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void UpdateTrayPresentation(MonitorState state)
    {
        var stateText = GetStateText(state);
        _statusItem.Text = AppText.Format("状态：{0}", stateText);
        _trayIcon.Text = _isDemoMode
            ? AppText.Format("NetDriveGuard 演示：{0}", stateText)
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
                ? _isDemoMode ? AppText.Get("[演示] 网络驱动器已恢复") : AppText.Get("网络驱动器已恢复")
                : _isDemoMode ? AppText.Get("[演示] 网络驱动器状态发生变化") : AppText.Get("网络驱动器状态发生变化"),
            decision.Snapshot.CurrentState == MonitorState.MappingFailed
                ? AppText.Get(MonitorEventFactory.DescribeMappingFailure(decision.EventCode))
                : AppText.Format("当前状态：{0}", GetStateText(decision.Snapshot.CurrentState)),
            recovered ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    private void RecordEvent(MonitorEvent monitorEvent, bool persist = true)
    {
        // 只翻译本次新产生的事件；加载历史记录时不经过此方法。
        monitorEvent = new MonitorEvent(monitorEvent.OccurredAt, monitorEvent.State,
            monitorEvent.Severity, monitorEvent.Code, AppText.Get(monitorEvent.Description));
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
                            AppText.Get("事件文件无法写入，状态仍保留在本次运行内。")),
                        persist: false);
                }
            },
            null);
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

    private static (MonitorCoordinator Coordinator, ScheduledMonitorRunner Runner) CreateRuntime(
        IClock clock,
        MonitorConfiguration? configuration,
        bool demoMode,
        ICredentialStore credentialStore)
    {
        IProbe<MonitorObservation> probe;
        MonitoringIntervals intervals;
        MonitorPolicy policy;
        IRemediation<MonitorAction> remediation;

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
            remediation = new ReadOnlyRemediation();
        }
        else if (configuration is not null)
        {
            probe = new ReadOnlyNetworkDriveProbe(configuration, clock);
            intervals = MonitoringIntervals.ForProfile(
                configuration.Profile,
                configuration.CustomTcpInterval);
            policy = new MonitorPolicy(
                configuration.OfflineFailureThreshold,
                configuration.RemoveMappingWhenOffline,
                configuration.RepairMissingMapping);
            remediation = new NetworkDriveRemediation(
                configuration,
                new WNetNetworkDriveQuery(),
                new WNetMappingOperationExecutor(),
                credentialStore);
        }
        else
        {
            probe = new UnconfiguredMonitorProbe(clock);
            intervals = MonitoringIntervals.ForProfile(MonitoringProfile.Default);
            policy = new MonitorPolicy(
                RemoveMappingWhenOffline: false,
                RepairMissingMapping: false);
            remediation = new ReadOnlyRemediation();
        }

        var coordinator = new MonitorCoordinator(
            probe,
            remediation,
            clock,
            new MonitorStateMachine(policy),
            MonitorSnapshot.Initial(clock.UtcNow));
        var runner = new ScheduledMonitorRunner(
            coordinator,
            new LayeredSchedulePlanner(intervals),
            clock,
            hasConfiguration: configuration is not null || demoMode);
        return (coordinator, runner);
    }

    /// <summary>停止调度、取消检查并写完已排队的事件，再释放托盘和窗口。</summary>
    private async void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _scheduleTimer.Stop();
        _systemChangeMonitor?.Dispose();
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
            _systemChangeMonitor?.Dispose();
            _lifetime.Dispose();
            _coordinator.Dispose();
            _dataStore.Dispose();
            _trayIcon.Dispose();
            _statusWindow.Dispose();
        }

        base.Dispose(disposing);
    }
}
