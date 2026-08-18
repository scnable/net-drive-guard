using NetDriveGuard.Core.Configuration;
using NetDriveGuard.Core.Scheduling;

namespace NetDriveGuard.App;

internal sealed class SettingsForm : Form
{
    private readonly TextBox _driveLetter = new() { Width = 80, CharacterCasing = CharacterCasing.Upper };
    private readonly TextBox _sharePath = new() { Width = 320 };
    private readonly ComboBox _profile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly NumericUpDown _customInterval = new() { Minimum = 1, Maximum = 1800, Value = 5 };
    private readonly NumericUpDown _failureThreshold = new() { Minimum = 2, Maximum = 10, Value = 3 };
    private readonly NumericUpDown _tcpTimeout = new()
    {
        Minimum = 100,
        Maximum = 10000,
        Increment = 100,
        Value = 800,
    };
    private readonly Label _validationMessage = new()
    {
        AutoSize = true,
        ForeColor = Color.Firebrick,
        MaximumSize = new Size(440, 0),
    };

    public SettingsForm(MonitorConfiguration? currentConfiguration)
    {
        Text = "NetDriveGuard 设置（只读观察模式）";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(540, 430);

        _profile.DataSource = Enum.GetValues<MonitoringProfile>();
        _profile.SelectedValueChanged += (_, _) => UpdateCustomIntervalState();
        BuildLayout();
        LoadConfiguration(currentConfiguration);
    }

    public MonitorConfiguration? SelectedConfiguration { get; private set; }

    private void BuildLayout()
    {
        var form = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 2,
            RowCount = 9,
        };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(form, 0, "盘符", _driveLetter);
        AddRow(form, 1, "共享地址", _sharePath);
        AddRow(form, 2, "检测模式", _profile);
        AddRow(form, 3, "自定义 TCP 周期（秒）", _customInterval);
        AddRow(form, 4, "离线确认次数", _failureThreshold);
        AddRow(form, 5, "TCP 超时（毫秒）", _tcpTimeout);

        var readOnly = new CheckBox
        {
            Text = "只读观察，不建立、取消或修复映射",
            Checked = true,
            Enabled = false,
            AutoSize = true,
        };
        form.Controls.Add(readOnly, 1, 6);

        form.Controls.Add(_validationMessage, 0, 7);
        form.SetColumnSpan(_validationMessage, 2);

        var saveButton = new Button { Text = "保存", AutoSize = true };
        var cancelButton = new Button
        {
            Text = "取消",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
        };
        saveButton.Click += HandleSave;
        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
        };
        actions.Controls.AddRange([cancelButton, saveButton]);
        form.Controls.Add(actions, 0, 8);
        form.SetColumnSpan(actions, 2);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        Controls.Add(form);
    }

    private void LoadConfiguration(MonitorConfiguration? configuration)
    {
        _driveLetter.Text = configuration?.DriveLetter ?? "Z:";
        _sharePath.Text = configuration?.SharePath ?? string.Empty;
        _profile.SelectedItem = configuration?.Profile ?? MonitoringProfile.Default;
        _customInterval.Value = (decimal)Math.Clamp(
            configuration?.CustomTcpInterval?.TotalSeconds ?? 5,
            (double)_customInterval.Minimum,
            (double)_customInterval.Maximum);
        _failureThreshold.Value = configuration?.OfflineFailureThreshold ?? 3;
        _tcpTimeout.Value = (decimal)(configuration?.EffectiveTcpTimeout.TotalMilliseconds ?? 800);
        UpdateCustomIntervalState();
    }

    private void UpdateCustomIntervalState()
    {
        _customInterval.Enabled = _profile.SelectedItem is MonitoringProfile.Custom;
    }

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
            RemoveMappingWhenOffline: false,
            TcpTimeout: TimeSpan.FromMilliseconds((double)_tcpTimeout.Value),
            OverallCheckTimeout: TimeSpan.FromSeconds(30));
        var validation = MonitorConfigurationValidator.Validate(configuration);
        if (!validation.IsValid)
        {
            _validationMessage.Text = string.Join(
                Environment.NewLine,
                validation.Issues.Select(issue => $"• {issue.Message}"));
            return;
        }

        SelectedConfiguration = configuration;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static void AddRow(TableLayoutPanel panel, int row, string label, Control control)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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
        panel.Controls.Add(control, 1, row);
    }
}
