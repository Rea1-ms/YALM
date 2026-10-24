using GHelper.UI;

namespace YALM.Mechrevo;

internal sealed class MechrevoForm : RForm
{
    private readonly MachineInfo _machine;
    private readonly bool _demo;
    private readonly bool _smokeTest;
    private readonly bool _startupTest;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 5000 };
    private readonly System.Windows.Forms.Timer _ecRefreshTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _smokeTimer = new() { Interval = 1500 };
    private readonly EcFanReader _ecFanReader = new();
    private readonly Label _connectionStatus = TextLabel("尚未连接");
    private readonly Label _cpuValues = TextLabel("温度：未收到\n风扇：未收到");
    private readonly Label _gpuValues = TextLabel("温度：未收到\n风扇：未收到");
    private readonly Label _actualSpeeds = TextLabel("实际转速：未收到");
    private readonly TableLayoutPanel _details = new() { Dock = DockStyle.Top, AutoSize = true, Visible = false, ColumnCount = 1 };
    private readonly Label _ecStatus = TextLabel("实际转速来源：正在读取 EC…");
    private readonly Label _reportedMode = TextLabel("服务回报模式：未收到");
    private readonly Label _controlStatus = TextLabel(string.Empty);
    private readonly Label _actionStatus = TextLabel(string.Empty);
    private readonly TextBox _clientId = new() { Dock = DockStyle.Fill };
    private readonly TextBox _userName = new() { Dock = DockStyle.Fill };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 13688, Dock = DockStyle.Fill };
    private readonly GroupBox _connectionSettings = new() { Text = "本机服务连接", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 20, 12, 12) };
    private readonly RButton _connect = Button("连接", colorStandard);
    private readonly RButton _disconnect = Button("断开", colorGray);
    private readonly RButton _openCustomMode = Button("风扇与功耗", colorTurbo);
    private readonly Dictionary<PerformanceMode, RButton> _modeButtons = new();
    private GcuController? _controller;
    private CustomModeForm? _customModeForm;
    private string? _machineBlockReason;
    private bool _autoConnect;
    private bool _connecting;
    private bool _refreshing;
    private bool _ecRefreshing;
    private bool _switching;
    private bool _closing;
    private bool _allowClose;
    private EcFanSnapshot? _ecSnapshot;

    public MechrevoForm(MachineInfo machine, bool demo = false, bool smokeTest = false, bool startupTest = false)
    {
        _machine = machine;
        _demo = demo;
        _smokeTest = smokeTest;
        _startupTest = startupTest;
        if (startupTest) _smokeTimer.Interval = 12000;
        Text = "YALM — " + (string.IsNullOrWhiteSpace(machine.DisplayName) ? "机械革命助手" : machine.DisplayName) + (demo ? " · 演示" : "");
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(500, 390);
        MinimumSize = new Size(500, 390);
        StartPosition = FormStartPosition.Manual;
        Padding = new Padding(18);
        AutoScroll = true;

        BuildLayout();
        InitTheme(true);
        if (!_demo) LoadConnectionEnvironment();
        _machineBlockReason = _demo ? null : _machine.GetModeControlBlockReason();
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _ecRefreshTimer.Tick += async (_, _) => await RefreshEcAsync();
        _smokeTimer.Tick += (_, _) => { _smokeTimer.Stop(); Close(); };
        Shown += OnShown;
        SizeChanged += (_, _) =>
        {
            if (Visible && WindowState == FormWindowState.Normal)
                PositionAtScreenCorner(Screen.FromControl(this));
        };
        FormClosing += OnFormClosing;
        RenderSnapshot();
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _reportedMode.Font = new Font(Font, FontStyle.Bold);
        AddRow(layout, Heading(_reportedMode, _cpuValues, GHelper.Properties.Resources.icons8_gauge_32));
        var modes = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 3, 0, 20) };
        var definitions = new[]
        {
            (PerformanceMode.Silent, "安静", colorEco, (Image?)GHelper.Properties.Resources.icons8_bicycle_48__1_),
            (PerformanceMode.Balanced, "均衡", colorStandard, (Image?)GHelper.Properties.Resources.icons8_fiat_500_48),
            (PerformanceMode.Turbo, "狂暴", colorTurbo, (Image?)GHelper.Properties.Resources.icons8_rocket_48)
        };
        for (int i = 0; i < definitions.Length; i++)
        {
            var (mode, name, color, image) = definitions[i];
            var button = Button(name, color);
            button.Image = image;
            button.ImageAlign = ContentAlignment.BottomCenter;
            button.TextImageRelation = TextImageRelation.ImageAboveText;
            button.Height = 76;
            button.Click += async (_, _) => await SwitchModeAsync(mode);
            modes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            modes.Controls.Add(button, i, 0);
            _modeButtons.Add(mode, button);
        }
        modes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        _openCustomMode.Height = 76;
        _openCustomMode.Text = "风扇与功耗";
        _openCustomMode.Image = GHelper.Properties.Resources.icons8_fan_48;
        _openCustomMode.ImageAlign = ContentAlignment.BottomCenter;
        _openCustomMode.TextImageRelation = TextImageRelation.ImageAboveText;
        _openCustomMode.Secondary = true;
        _openCustomMode.Click += (_, _) => OpenCustomMode();
        modes.Controls.Add(_openCustomMode, 3, 0);
        AddRow(layout, modes);
        var gpuTitle = TextLabel("GPU 与风扇");
        gpuTitle.Font = new Font(Font, FontStyle.Bold);
        AddRow(layout, Heading(gpuTitle, _gpuValues, GHelper.Properties.Resources.icons8_video_card_32));
        _actualSpeeds.Margin = new Padding(0, 6, 0, 18);
        AddRow(layout, _actualSpeeds);
        AddRow(layout, _controlStatus);
        AddRow(layout, _actionStatus);

        var footer = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 10, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        _connectionStatus.Anchor = AnchorStyles.Left;
        footer.Controls.Add(_connectionStatus, 0, 0);
        var settings = Button("更多设置", colorGray);
        settings.Height = 32;
        settings.Click += (_, _) =>
        {
            _details.Visible = !_details.Visible;
            _connectionSettings.Visible = _details.Visible && !_demo;
            if (_details.Visible) Height = Math.Max(Height, 620 * DeviceDpi / 96);
        };
        footer.Controls.Add(settings, 1, 0);
        var quit = Button("退出", colorGray);
        quit.Height = 32;
        quit.Click += (_, _) => Close();
        footer.Controls.Add(quit, 2, 0);
        AddRow(layout, footer);
        _details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(_details, TextLabel(_machine.DisplayName));
        AddRow(_details, _ecStatus);
        AddRow(_details, _connectionSettings);
        BuildConnectionSettings();
        AddRow(_details, TextLabel("仅手动切换模式；关闭窗口不会关闭厂商服务。"));
        AddRow(layout, _details);
        Controls.Add(layout);
    }

    private void BuildConnectionSettings()
    {
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(fields, "客户端标识", _clientId);
        AddField(fields, "用户名", _userName);
        AddField(fields, "密码", _password);
        AddField(fields, "本机端口", _port);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        _connect.Width = 100;
        _disconnect.Width = 100;
        _connect.Click += async (_, _) => await ConnectAsync();
        _disconnect.Click += async (_, _) => await DisconnectAsync();
        actions.Controls.Add(_connect);
        actions.Controls.Add(_disconnect);
        fields.Controls.Add(actions, 1, fields.RowCount++);
        var note = TextLabel("仅连接本机。连接信息只保存在本次运行内存中。\n需要已有厂商服务；此面板不会安装或启动它。");
        fields.Controls.Add(note, 0, fields.RowCount++);
        fields.SetColumnSpan(note, 2);
        _connectionSettings.Controls.Add(fields);
        _connectionSettings.Visible = !_demo;
    }

    private void LoadConnectionEnvironment()
    {
        _clientId.Text = GcuEnvironment.Get("CLIENT_ID") ?? string.Empty;
        _userName.Text = GcuEnvironment.Get("USERNAME") ?? string.Empty;
        _password.Text = GcuEnvironment.Get("PASSWORD") ?? string.Empty;
        var port = GcuEnvironment.Get("PORT");
        bool validPort = string.IsNullOrWhiteSpace(port);
        if (int.TryParse(port, out int number) && number is >= 1 and <= 65535)
        {
            _port.Value = number;
            validPort = true;
        }
        _autoConnect = validPort && HasConnectionValues();
        if (!_autoConnect && validPort && GcuConnectionDiscovery.Find(_machine) is { } discovered)
        {
            _clientId.Text = discovered.ClientId;
            _userName.Text = discovered.UserName;
            _password.Text = discovered.Password;
            _port.Value = discovered.Port;
            _autoConnect = true;
        }
        _connectionSettings.Visible = !_autoConnect;
        if (!_autoConnect) _details.Visible = true;
        if (!validPort) _actionStatus.Text = "环境变量中的端口无效，请在连接设置中填写。";
    }

    private async void OnShown(object? sender, EventArgs e)
    {
        _refreshTimer.Start();
        if (!_demo && _machine.SupportsModeControl) _ecRefreshTimer.Start();
        if (_smokeTest || _startupTest) _smokeTimer.Start();
        if (_smokeTest) OpenCustomMode();
        if (_autoConnect && !_demo) await ConnectAsync();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        PositionAtScreenCorner(Screen.PrimaryScreen ?? Screen.FromControl(this));
    }

    private void PositionAtScreenCorner(Screen screen)
    {
        Rectangle area = screen.WorkingArea;
        Location = new Point(Math.Max(area.Left, area.Right - 10 - Width),
            Math.Max(area.Top, area.Bottom - 10 - Height));
    }

    private bool HasConnectionValues() => !string.IsNullOrWhiteSpace(_clientId.Text) &&
        !string.IsNullOrWhiteSpace(_userName.Text) && !string.IsNullOrEmpty(_password.Text);

    private async Task ConnectAsync()
    {
        if (_demo || _closing || _connecting || _switching) return;
        if (!HasConnectionValues())
        {
            _actionStatus.Text = "请填写客户端标识、用户名和密码；程序没有内置凭据。";
            _connectionSettings.Visible = true;
            return;
        }
        _connecting = true;
        _actionStatus.Text = "正在连接本机服务…";
        RenderSnapshot();
        try
        {
            if (_controller is null)
            {
                _controller = new GcuController(new GcuMqttTransport(), readAcPower: MachineInfo.ReadAcPower);
                _controller.Changed += OnControllerChanged;
            }
            else
            {
                await _controller.DisconnectAsync();
            }
            var options = new GcuConnectionOptions(_clientId.Text.Trim(), _userName.Text.Trim(), _password.Text, (int)_port.Value)
            {
                TurboSupported = _machine.TurboSupported
            };
            await _controller.ConnectAsync(options, _lifetime.Token);
            if (_closing) return;
            _actionStatus.Text = "已连接，正在读取服务状态。";
            _connectionSettings.Visible = false;
            await _controller.RefreshAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            if (!_closing)
            {
                _actionStatus.Text = "连接失败：" + SafeMessage(ex.Message);
                _connectionSettings.Visible = true;
            }
        }
        finally
        {
            _connecting = false;
            if (!_closing) RenderSnapshot();
        }
    }

    private async Task DisconnectAsync()
    {
        if (_controller is null || _connecting || _switching || _closing) return;
        _connecting = true;
        RenderSnapshot();
        try
        {
            await _controller.DisconnectAsync();
            if (!_closing) _actionStatus.Text = "已断开本面板连接，厂商服务仍正常运行。";
        }
        catch (Exception ex)
        {
            if (!_closing) _actionStatus.Text = "断开连接时出错：" + SafeMessage(ex.Message);
        }
        finally
        {
            _connecting = false;
            if (!_closing) RenderSnapshot();
        }
    }

    private async Task RefreshAsync()
    {
        if (_closing || _refreshing || _connecting || _switching) return;
        _refreshing = true;
        try
        {
            if (!_demo) _machineBlockReason = _machine.GetModeControlBlockReason();
            RenderSnapshot();
            if (_controller?.Snapshot.IsConnected == true)
                await _controller.RefreshAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            if (!_closing) _actionStatus.Text = "读取状态失败：" + SafeMessage(ex.Message);
        }
        finally
        {
            _refreshing = false;
            if (!_closing) RenderSnapshot();
        }
    }

    private async Task RefreshEcAsync()
    {
        if (_demo || !_machine.SupportsModeControl || _closing || _ecRefreshing) return;
        _ecRefreshing = true;
        try
        {
            _ecSnapshot = await _ecFanReader.ReadAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_closing || _lifetime.IsCancellationRequested) { }
        catch
        {
            _ecSnapshot = new EcFanSnapshot
            {
                LastError = "EC 风扇读取任务失败。",
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
        finally
        {
            _ecRefreshing = false;
            if (!_closing) RenderSnapshot();
        }
    }

    private async Task SwitchModeAsync(PerformanceMode mode)
    {
        if (_demo || _startupTest || _closing || _connecting || _switching || _controller is null) return;
        _machineBlockReason = _machine.GetModeControlBlockReason();
        var now = DateTimeOffset.UtcNow;
        bool thermalProtectionActive = _ecSnapshot?.IsFresh(now) == true &&
            _ecSnapshot.GenericThermalProtect == true;
        bool actualRpmFresh = HasFreshActualRpm(now);
        if (thermalProtectionActive || !actualRpmFresh || _machineBlockReason is not null ||
            !_controller.Snapshot.CanSwitchMode(mode))
        {
            _actionStatus.Text = thermalProtectionActive ? "EC 报告的通用热保护已触发，暂时禁止切换模式。"
                : !actualRpmFresh ? "两路实际转速不完整或已过期，暂时不能切换模式。"
                : _machineBlockReason ?? "状态不完整或已过期，暂时不能切换模式。";
            RenderSnapshot();
            return;
        }
        _switching = true;
        _actionStatus.Text = "等待服务回报模式…";
        RenderSnapshot();
        try
        {
            var result = await _controller.SetModeAsync(mode, _lifetime.Token);
            if (!_closing)
                _actionStatus.Text = result.ServiceConfirmed ? "服务已回报，硬件效果待实测。" : SafeMessage(result.Message);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            if (!_closing) _actionStatus.Text = "切换失败：" + SafeMessage(ex.Message);
        }
        finally
        {
            _switching = false;
            if (!_closing) RenderSnapshot();
        }
    }

    private void OpenCustomMode()
    {
        if (_startupTest || (!_demo && _controller is null) || _closing) return;
        if (_customModeForm is { IsDisposed: false })
        {
            if (_customModeForm.WindowState == FormWindowState.Minimized)
                _customModeForm.WindowState = FormWindowState.Normal;
            _customModeForm.Activate();
            return;
        }

        _customModeForm = new CustomModeForm(_controller, () => _ecSnapshot, _demo) { Owner = this };
        _customModeForm.FormClosed += (_, _) => _customModeForm = null;
        _customModeForm.Show(this);
    }

    private void OnControllerChanged()
    {
        if (_closing || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((Action)RenderSnapshot); }
        catch (InvalidOperationException) { }
    }

    private void RenderSnapshot()
    {
        if (_closing || IsDisposed) return;
        if (_demo)
        {
            _connectionStatus.Text = "演示 · 未连接服务";
            _cpuValues.Text = "CPU: 48 ℃  风扇: 35%";
            _gpuValues.Text = "GPU: 43 ℃  风扇: 35%";
            _actualSpeeds.Text = "实际转速：演示中不读取";
            _ecStatus.Text = "示例状态 · 不读取 EC 实际转速";
            _reportedMode.Text = "性能模式：均衡";
            _controlStatus.Text = "演示布局，可打开风扇与功耗预览曲线。";
            foreach (var (mode, button) in _modeButtons)
            {
                button.Enabled = false;
                button.Activated = mode == PerformanceMode.Balanced;
            }
            _openCustomMode.Enabled = true;
            _connect.Enabled = _disconnect.Enabled = false;
            return;
        }

        var snapshot = _controller?.Snapshot;
        var now = DateTimeOffset.UtcNow;
        bool connected = snapshot?.IsConnected == true;
        bool ecFresh = _ecSnapshot?.IsFresh(now) == true;
        bool fanSafetyActive = ecFresh && _ecSnapshot?.FanSafetyProtect == true;
        bool thermalProtectionActive = ecFresh && _ecSnapshot?.GenericThermalProtect == true;
        int? cpuRpm = ecFresh ? _ecSnapshot!.CpuFanRpm
            : connected && snapshot!.IsCpuFanFresh(now) ? snapshot.CpuFanRpm : null;
        int? gpuRpm = ecFresh ? _ecSnapshot!.GpuFanRpm
            : connected && snapshot!.IsGpuFanFresh(now) ? snapshot.GpuFanRpm : null;
        bool bothRpmFresh = cpuRpm.HasValue && gpuRpm.HasValue;
        _connectionStatus.Text = _connecting ? "正在处理连接…" : connected ? "已连接本机服务" : "尚未连接";
        if (!connected && !string.IsNullOrWhiteSpace(snapshot?.LastError))
            _connectionStatus.Text = "连接不可用：" + SafeMessage(snapshot.LastError);
        _cpuValues.Text = $"CPU: {Value(snapshot?.CpuTemperature, connected && snapshot!.IsCpuTemperatureFresh(now), " ℃")}  风扇: {Value(snapshot?.CpuFanDuty, connected && snapshot!.IsCpuFanDutyFresh(now), "%")}";
        _gpuValues.Text = $"GPU: {Value(snapshot?.GpuTemperature, connected && snapshot!.IsGpuTemperatureFresh(now), " ℃")}  风扇: {Value(snapshot?.GpuFanDuty, connected && snapshot!.IsGpuFanDutyFresh(now), "%")}";
        _actualSpeeds.Text = $"实际转速 · CPU {Value(cpuRpm, cpuRpm.HasValue, " 转/分")} · GPU {Value(gpuRpm, gpuRpm.HasValue, " 转/分")}";
        _ecStatus.Text = !_machine.SupportsModeControl ? "此机型未验证，不读取 EC 实际转速。"
            : _ecSnapshot switch
        {
            { LastError: not null } => "EC 实际转速不可用：" + _ecSnapshot.LastError,
            _ when fanSafetyActive => "实际转速来源：EC 直读；风扇安全保护已触发。",
            _ when thermalProtectionActive => "实际转速来源：EC 直读；通用热保护已触发。",
            _ when ecFresh => $"实际转速来源：EC 直读；保护位：{ProtectionText(_ecSnapshot!)}。",
            _ when _ecSnapshot is not null => "EC 实际转速已过期，等待下一次读取。",
            _ => "实际转速来源：正在读取 EC…"
        };
        bool modeFresh = connected && snapshot!.IsStatusFresh();
        _reportedMode.Text = "性能模式：" + (modeFresh ? ModeName(snapshot!.Mode) : "未收到");
        _controlStatus.Text = thermalProtectionActive ? "EC 报告的通用热保护已触发，模式切换暂时禁用。"
            : fanSafetyActive ? "风扇安全保护已触发；预设模式仍可切换，后台已提高散热量。"
            : _machineBlockReason ?? (!connected ? "连接后读取状态；不会自动切换性能模式。"
            : !modeFresh || !snapshot!.IsFanFresh() ? "等待完整、新鲜的模式与风扇状态，暂时不能切换。"
            : snapshot.TurboSupported != true ? "尚未确认此机支持狂暴模式，该选项暂不可用。"
            : snapshot.IsAc != true ? "未确认已接通电源，狂暴模式暂不可用。"
            : !bothRpmFresh ? "未收到两路实际转速，不能据此确认风扇实际转动。"
            : "风扇仍由厂商服务与固件管理。");
        foreach (var (mode, button) in _modeButtons)
        {
            button.Activated = modeFresh && snapshot!.Mode == (int)mode;
            button.Enabled = !_startupTest && !_connecting && !_switching && !thermalProtectionActive && bothRpmFresh &&
                _machineBlockReason is null && connected && snapshot!.CanSwitchMode(mode, now);
        }
        _openCustomMode.Enabled = !_startupTest && !_connecting && !_switching && connected && _controller is not null;
        _connect.Enabled = !_connecting && !_switching;
        _disconnect.Enabled = connected && !_connecting && !_switching;
        _clientId.Enabled = _userName.Enabled = _password.Enabled = _port.Enabled = !_connecting && !_switching;
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _refreshTimer.Stop();
        _ecRefreshTimer.Stop();
        _smokeTimer.Stop();
        _lifetime.Cancel();
        try
        {
            if (_customModeForm is { IsDisposed: false })
                _customModeForm.Close();
            if (_controller is not null)
            {
                _controller.Changed -= OnControllerChanged;
                await _controller.DisposeAsync();
            }
        }
        catch { /* Closing the panel must not send commands to the shared service. */ }
        finally
        {
            _refreshTimer.Dispose();
            _ecRefreshTimer.Dispose();
            _smokeTimer.Dispose();
            _lifetime.Dispose();
            _password.Clear();
            _allowClose = true;
            if (IsHandleCreated) BeginInvoke((Action)Close);
        }
    }

    private string SafeMessage(string message) => string.IsNullOrEmpty(_password.Text)
        ? message : message.Replace(_password.Text, "[已隐藏]", StringComparison.Ordinal);

    private static string Value(int? value, bool fresh, string unit) => fresh && value.HasValue
        ? value.Value.ToString() + unit : "未收到";

    private bool HasFreshActualRpm(DateTimeOffset now)
    {
        if (_ecSnapshot?.IsFresh(now) == true) return true;
        var snapshot = _controller?.Snapshot;
        return snapshot?.IsConnected == true && snapshot.IsCpuFanFresh(now) && snapshot.IsGpuFanFresh(now);
    }

    private static string ModeName(int? mode) => mode switch { 0 => "安静", 1 => "均衡", 2 => "狂暴", 3 => "自定义", _ => "未收到" };

    private static string ProtectionText(EcFanSnapshot snapshot)
    {
        if (snapshot.FanSafetyProtect == true) return "风扇安全保护已触发";
        if (snapshot.GenericThermalProtect == true) return "通用热保护已触发";
        return "未触发";
    }

    private static Label TextLabel(string text) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(0, 3, 0, 6)
    };

    private static RButton Button(string text, Color color) => new()
    {
        Text = text, Height = 40, Dock = DockStyle.Fill, Margin = new Padding(2), BorderColor = color, BorderRadius = 5
    };

    private Control Heading(Label title, Label values, Image icon)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        title.Anchor = AnchorStyles.Left;
        values.Anchor = AnchorStyles.Right;
        row.Controls.Add(MechrevoUi.Heading(this, title, icon), 0, 0);
        row.Controls.Add(values, 1, 0);
        return row;
    }

    private static void AddRow(TableLayoutPanel layout, Control control)
    {
        int row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(control, 0, row);
    }

    private static void AddField(TableLayoutPanel fields, string title, Control control)
    {
        int row = fields.RowCount++;
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(TextLabel(title), 0, row);
        fields.Controls.Add(control, 1, row);
    }
}
