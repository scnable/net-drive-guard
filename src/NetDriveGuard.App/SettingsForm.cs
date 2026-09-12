using NetDriveGuard.Core.Localization;
using NetDriveGuard.Core.Configuration;
using NetDriveGuard.Core.Scheduling;
using NetDriveGuard.Core.Security;

namespace NetDriveGuard.App;

internal sealed class SettingsForm : Form
{
    private readonly TextBox _driveLetter = new() { Width = 80, CharacterCasing = CharacterCasing.Upper };
    private readonly TextBox _sharePath = new() { Width = 320 };
    private readonly ComboBox _profile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly NumericUpDown _customInterval = new() { Minimum = 1, Maximum = 1800, Value = 5 };
    private readonly NumericUpDown _failureThreshold = new() { Minimum = 2, Maximum = 10, Value = 3 };
    private readonly NumericUpDown _tcpTimeout = new()
    {
        Minimum = 100,
        Maximum = 10000,
        Increment = 100,
        Value = 800,
    };
    private readonly CheckBox _repairMissingMapping = new()
    {
        Text = AppText.Get("服务器恢复可用后自动重连（也修复缺失映射）"),
        AutoSize = true,
    };
    private readonly CheckBox _removeMappingWhenOffline = new()
    {
        Text = AppText.Get("确认服务器离线后取消失效映射"),
        AutoSize = true,
    };
    private readonly CheckBox _startAtLogon = new()
    {
        Text = AppText.Get("开机自启动"),
        AutoSize = true,
    };
    private readonly TextBox _dataDirectory = new() { ReadOnly = true, Width = 330 };
    private readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly TextBox _credentialUserName = new() { Width = 240 };
    private readonly TextBox _credentialPassword = new()
    {
        Width = 240,
        UseSystemPasswordChar = true,
        MaxLength = 1280,
    };
    private readonly Label _validationMessage = new()
    {
        AutoSize = true,
        ForeColor = Color.Firebrick,
        Dock = DockStyle.Fill,
    };

    private bool _wasStartAtLogonEnabled;
    private bool _updatingMonitoringFields;

    public SettingsForm(MonitorConfiguration? currentConfiguration, string? dataDirectory = null)
    {
        Text = AppText.Get("NetDriveGuard 设置");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(660, 520);

        _profile.FormattingEnabled = true;
        _profile.Format += (_, args) => args.Value = args.ListItem switch
        {
            MonitoringProfile.Default => AppText.Get("标准（推荐）"),
            MonitoringProfile.Fast => AppText.Get("快速"),
            MonitoringProfile.EnergySaving => AppText.Get("节能"),
            MonitoringProfile.Custom => AppText.Get("自定义"),
            _ => args.ListItem?.ToString(),
        };
        // 在加载配置前完成绑定，避免首次显示页签时才选中默认项并覆盖已有数值。
        _profile.BindingContext = new BindingContext();
        _profile.DataSource = Enum.GetValues<MonitoringProfile>();
        _profile.SelectedValueChanged += (_, _) => ApplySelectedProfile();
        _repairMissingMapping.CheckedChanged += (_, _) => UpdateCredentialState();
        _language.Items.AddRange(["简体中文", "English"]);
        BuildLayout();
        LoadConfiguration(currentConfiguration);
        _dataDirectory.Text = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetDriveGuard");
        _wasStartAtLogonEnabled = currentConfiguration?.StartAtLogon ?? false;
    }

    public MonitorConfiguration? SelectedConfiguration { get; private set; }

    public string SelectedDataDirectory => _dataDirectory.Text;

    private NetworkCredentialSecret? PendingCredential { get; set; }

    /// <summary>把待保存凭据交给调用方；调用方保存后必须释放，避免密码长期留在内存。</summary>
    public NetworkCredentialSecret? TakePendingCredential()
    {
        var credential = PendingCredential;
        PendingCredential = null;
        return credential;
    }

    /// <summary>按用途分为三页；小屏幕只滚动内容，保存和取消始终留在底部。</summary>
    private void BuildLayout()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var basic = CreatePage(tabs, AppText.Get("常用设置"), 6);
        var monitoring = CreatePage(tabs, AppText.Get("监控设置"), 5);
        var advanced = CreatePage(tabs, AppText.Get("高级设置"), 8);
        AddRow(basic, 0, AppText.Get("盘符"), _driveLetter);
        AddRow(basic, 1, AppText.Get("共享地址"), _sharePath);
        AddOption(basic, 2, _removeMappingWhenOffline);
        AddOption(basic, 3, _repairMissingMapping);
        AddOption(basic, 4, _startAtLogon);
        var hint = new Label
        {
            Text = AppText.Get("本工具用于离线时处理失效映射、恢复后自动重连。请按需选择上面的两项自动操作；都不选时仅监控提醒。取消映射不会强制关闭文件，迁移前请停用旧脚本。"),
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 12, 3, 3),
        };
        basic.Controls.Add(hint, 0, 5);
        basic.SetColumnSpan(hint, 2);

        AddRow(monitoring, 0, AppText.Get("检测模式"), _profile);
        AddRow(monitoring, 1, AppText.Get("检查间隔（秒）"), _customInterval);
        AddRow(monitoring, 2, AppText.Get("离线确认次数"), _failureThreshold);
        AddRow(monitoring, 3, AppText.Get("连接超时（毫秒）"), _tcpTimeout);
        var monitoringHint = new Label
        {
            Text = AppText.Get("预设模式下参数只读；选择自定义后可修改。选择预设模式会填入对应周期，并恢复 3 次离线确认和 800 毫秒超时。检查间隔是常规服务器连接检查周期，不是完成断开或重连所需的时间。"),
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
        };
        monitoring.Controls.Add(monitoringHint, 0, 4);
        monitoring.SetColumnSpan(monitoringHint, 2);

        AddRow(advanced, 0, AppText.Get("用户名（可选）"), _credentialUserName);
        AddRow(advanced, 1, AppText.Get("密码（可选）"), _credentialPassword);
        var credentialHint = new Label
        {
            Text = AppText.Get("仅自动重连需要独立账号时填写；都留空则使用 Windows 当前身份或已保存凭据。"),
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
        };
        advanced.Controls.Add(credentialHint, 0, 2);
        advanced.SetColumnSpan(credentialHint, 2);

        AddRow(advanced, 3, AppText.Get("文件保存目录"), _dataDirectory);
        var directoryActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var browseDirectory = new Button { Text = AppText.Get("选择文件夹…"), AutoSize = true };
        var openDirectory = new Button { Text = AppText.Get("打开文件夹"), AutoSize = true };
        browseDirectory.Click += (_, _) => SelectDataDirectory();
        openDirectory.Click += (_, _) => OpenDataDirectory();
        directoryActions.Controls.AddRange([browseDirectory, openDirectory]);
        advanced.Controls.Add(directoryActions, 1, 4);
        var directoryHint = new Label
        {
            Text = AppText.Get("请选择本机目录。保存后迁移配置和日志，旧文件保留；凭据和自启动任务仍由 Windows 保存。"),
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
        };
        advanced.Controls.Add(directoryHint, 0, 5);
        advanced.SetColumnSpan(directoryHint, 2);

        AddRow(advanced, 6, AppText.Get("界面语言"), _language);
        var languageHint = new Label
        {
            Text = AppText.Get("语言修改将在下次启动时生效，历史日志保留原文。"),
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
        };
        advanced.Controls.Add(languageHint, 0, 7);
        advanced.SetColumnSpan(languageHint, 2);

        var saveButton = new Button { Text = AppText.Get("保存"), AutoSize = true, MinimumSize = new Size(90, 32) };
        var cancelButton = new Button
        {
            Text = AppText.Get("取消"),
            AutoSize = true,
            MinimumSize = new Size(90, 32),
            DialogResult = DialogResult.Cancel,
        };
        saveButton.Click += HandleSave;
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        actions.Controls.AddRange([cancelButton, saveButton]);
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(tabs, 0, 0);
        root.Controls.Add(_validationMessage, 0, 1);
        root.Controls.Add(actions, 0, 2);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
        Controls.Add(root);
    }

    private static TableLayoutPanel CreatePage(TabControl tabs, string title, int rows)
    {
        var page = new TabPage(title) { AutoScroll = true, Padding = new Padding(12) };
        var fields = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = rows,
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < rows; row++)
        {
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        page.Controls.Add(fields);
        tabs.TabPages.Add(page);
        return fields;
    }

    private void LoadConfiguration(MonitorConfiguration? configuration)
    {
        _driveLetter.Text = configuration?.DriveLetter ?? "Z:";
        _sharePath.Text = configuration?.SharePath ?? string.Empty;
        // 加载时显示实际生效的周期，不能由控件事件把已有配置改成其他模式。
        _updatingMonitoringFields = true;
        _profile.SelectedItem = configuration?.Profile ?? MonitoringProfile.Default;
        _customInterval.Value = (decimal)Math.Clamp(
            MonitoringIntervals.ForProfile(
                configuration?.Profile ?? MonitoringProfile.Default,
                configuration?.CustomTcpInterval ?? TimeSpan.FromSeconds(5)).TcpProbe.TotalSeconds,
            (double)_customInterval.Minimum,
            (double)_customInterval.Maximum);
        _failureThreshold.Value = configuration?.OfflineFailureThreshold ?? 3;
        _tcpTimeout.Value = (decimal)(configuration?.EffectiveTcpTimeout.TotalMilliseconds ?? 800);
        _updatingMonitoringFields = false;
        UpdateMonitoringFieldState();
        _repairMissingMapping.Checked = configuration?.RepairMissingMapping ?? false;
        _removeMappingWhenOffline.Checked = configuration?.RemoveMappingWhenOffline ?? false;
        _startAtLogon.Checked = configuration?.StartAtLogon ?? false;
        _language.SelectedIndex = (configuration?.Language ?? AppText.Language) == "en" ? 1 : 0;
        UpdateCredentialState();
    }

    private void SelectDataDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = AppText.Get("选择配置和日志的保存目录"),
            UseDescriptionForTitle = true,
            SelectedPath = _dataDirectory.Text,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _dataDirectory.Text = dialog.SelectedPath;
        }
    }

    private void OpenDataDirectory()
    {
        if (!Directory.Exists(_dataDirectory.Text))
        {
            _validationMessage.Text = AppText.Get("文件夹尚不存在，请选择已有文件夹或保存设置后再打开。");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _dataDirectory.Text,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _validationMessage.Text = AppText.Get("无法打开文件夹，请检查路径是否可访问。");
        }
    }

    /// <summary>预设填入只读数值；切换到自定义时保留当前数值并开放编辑。</summary>
    private void ApplySelectedProfile()
    {
        if (_updatingMonitoringFields || _profile.SelectedItem is not MonitoringProfile profile)
        {
            return;
        }

        UpdateMonitoringFieldState();
        if (profile == MonitoringProfile.Custom)
        {
            return;
        }

        _updatingMonitoringFields = true;
        try
        {
            _customInterval.Value = (decimal)MonitoringIntervals.ForProfile(profile).TcpProbe.TotalSeconds;
            _failureThreshold.Value = 3;
            _tcpTimeout.Value = 800;
        }
        finally
        {
            _updatingMonitoringFields = false;
        }
    }

    private void UpdateMonitoringFieldState()
    {
        var editable = _profile.SelectedItem is MonitoringProfile.Custom;
        _customInterval.Enabled = editable;
        _failureThreshold.Enabled = editable;
        _tcpTimeout.Enabled = editable;
    }

    private void UpdateCredentialState()
    {
        _credentialUserName.Enabled = _repairMissingMapping.Checked;
        _credentialPassword.Enabled = _repairMissingMapping.Checked;
    }

    /// <summary>验证输入并返回配置。凭据与启动项由调用方保存，此窗口不直接修改系统。</summary>
    private void HandleSave(object? sender, EventArgs eventArgs)
    {
        var profile = _profile.SelectedItem is MonitoringProfile selected
            ? selected
            : MonitoringProfile.Default;
        var configuration = new MonitorConfiguration(
            _driveLetter.Text.Trim().ToUpperInvariant(),
            _sharePath.Text.Trim(),
            profile,
            profile == MonitoringProfile.Custom
                ? TimeSpan.FromSeconds((double)_customInterval.Value)
                : null,
            (int)_failureThreshold.Value,
            RemoveMappingWhenOffline: _removeMappingWhenOffline.Checked,
            RepairMissingMapping: _repairMissingMapping.Checked,
            StartAtLogon: _startAtLogon.Checked,
            TcpTimeout: TimeSpan.FromMilliseconds((double)_tcpTimeout.Value),
            OverallCheckTimeout: TimeSpan.FromSeconds(30),
            Language: _language.SelectedIndex == 1 ? "en" : "zh-CN");
        var validation = MonitorConfigurationValidator.Validate(configuration);
        _validationMessage.ForeColor = Color.Firebrick;
        if (!validation.IsValid)
        {
            _validationMessage.Text = string.Join(
                Environment.NewLine,
                validation.Issues.Select(issue => $"• {AppText.Get(issue.Message)}"));
            return;
        }

        var userName = _credentialUserName.Text.Trim();
        var password = _credentialPassword.Text;
        if ((userName.Length == 0) != (password.Length == 0))
        {
            _validationMessage.Text = AppText.Get("• 用户名和密码必须同时填写；都留空时使用当前 Windows 身份或已保存凭据。");
            return;
        }

        if (!_repairMissingMapping.Checked && (userName.Length > 0 || password.Length > 0))
        {
            _validationMessage.Text = AppText.Get("• 只有启用自动恢复映射后才能更新映射凭据。");
            return;
        }

        if (_removeMappingWhenOffline.Checked)
        {
            var confirmation = MessageBox.Show(
                this,
                AppText.Get("服务器连续离线达到确认次数后，程序会取消当前盘符到配置共享的映射。若有文件正在使用，取消操作会失败而不会强制断开。是否启用？"),
                AppText.Get("确认自动取消映射"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                return;
            }
        }

        if (_wasStartAtLogonEnabled && !_startAtLogon.Checked)
        {
            var confirmation = MessageBox.Show(
                this,
                AppText.Get("关闭开机自启动会删除 NetDriveGuard 自己创建的登录任务，不会删除程序文件或遗留任务。是否继续？"),
                AppText.Get("确认关闭开机自启动"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                return;
            }
        }

        PendingCredential?.Dispose();
        PendingCredential = userName.Length == 0
            ? null
            : new NetworkCredentialSecret(userName, password.AsSpan());
        _credentialPassword.Clear();

        SelectedConfiguration = configuration;
        DialogResult = DialogResult.OK;
        Close();
    }

    // 旧脚本迁移改为手动填写常用设置；程序不再读取或解析脚本。

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PendingCredential?.Dispose();
            PendingCredential = null;
            _credentialPassword.Clear();
        }

        base.Dispose(disposing);
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        WindowPlacement.FitToWorkingArea(this);
    }

    private static void AddRow(TableLayoutPanel panel, int row, string label, Control control)
    {
        panel.Controls.Add(
            new Label
            {
                Text = label,
                AutoSize = true,
                Margin = new Padding(3, 6, 3, 10),
            },
            0,
            row);
        control.Margin = new Padding(3, 3, 3, 10);
        control.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        panel.Controls.Add(control, 1, row);
    }

    private static void AddOption(TableLayoutPanel panel, int row, CheckBox option)
    {
        option.Margin = new Padding(3, 3, 3, 10);
        panel.Controls.Add(option, 1, row);
    }
}
