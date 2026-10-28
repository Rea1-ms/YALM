using GHelper.UI;

namespace YALM.Mechrevo;

internal sealed partial class MechrevoForm : RForm
{
    private MachineInfo _machine;
    private readonly bool _demo;
    private readonly bool _smokeTest;
    private readonly bool _startupTest;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 5000 };
    private readonly System.Windows.Forms.Timer _ecRefreshTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _smokeTimer = new() { Interval = 1500 };
    private EcFanReader _ecFanReader;
    private readonly Label _connectionStatus = TextLabel("尚未连接");
    private readonly Label _cpuValues = TextLabel("温度：未收到\n风扇：未收到");
    private readonly Label _gpuValues = TextLabel("温度：未收到\n风扇：未收到");
    private bool _fanRpm = AppConfig.IsNotFalse("fan_rpm");
    private readonly Label _reportedMode = TextLabel("服务回报模式：未收到");
    private string _controlMessage = string.Empty;
    private string _actionMessage = string.Empty;
    private DateTimeOffset _actionMessageUntil;
    private readonly RButton _openCustomMode = Button("风扇与功耗", colorTurbo);
    private readonly Dictionary<PerformanceMode, RButton> _modeButtons = new();
    private GcuController? _controller;
    private GcuConnectionOptions? _connectionOptions;
    private string? _connectionDiscoveryFailure;
    private string? _loggedServiceState;
    private string? _lastRefreshFailure;
    private bool _diagnosticsStarted;
    private CustomModeForm? _customModeForm;
    private string? _machineBlockReason;
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
        _ecFanReader = new EcFanReader(readProtection: machine.SupportsEcProtection);
        _demo = demo;
        _smokeTest = smokeTest;
        _startupTest = startupTest;
        if (startupTest) _smokeTimer.Interval = 12000;
        Text = "YALM — " + (string.IsNullOrWhiteSpace(machine.DisplayName) ? "机械革命助手" : machine.DisplayName) + (demo ? " · 演示" : "");
        Icon = MechrevoUi.AppIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        ClientSize = new Size(500, 440);
        MinimumSize = new Size(500, 440);
        StartPosition = FormStartPosition.Manual;
        MaximizeBox = false;
        MinimizeBox = false;
        Padding = new Padding(18);
        AutoScroll = true;

        BuildLayout();
        InitTheme(true);
        InitializeTray();
        TopMost = !_demo && AppConfig.Is("topmost");
        _machineBlockReason = _demo ? null : _machine.GetModeControlBlockReason();
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _ecRefreshTimer.Tick += async (_, _) => await RefreshEcAsync();
        _smokeTimer.Tick += (_, _) => { _smokeTimer.Stop(); ExitApplication(); };
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
        foreach (Label reading in new[] { _cpuValues, _gpuValues })
        {
            reading.Cursor = Cursors.Hand;
            reading.AccessibleDescription = "点击切换百分比与实际转速";
            reading.Click += (_, _) =>
            {
                _fanRpm = !_fanRpm;
                if (!_demo) AppConfig.Set("fan_rpm", _fanRpm ? 1 : 0);
                RenderSnapshot();
            };
        }
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
        BuildGpuLayout(layout);
        BuildKeyboardLayout(layout);

        var footer = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 10, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        _connectionStatus.AutoSize = false;
        _connectionStatus.AutoEllipsis = true;
        _connectionStatus.Dock = DockStyle.Fill;
        _connectionStatus.TextAlign = ContentAlignment.MiddleLeft;
        _connectionStatus.Margin = new Padding(0, 0, 6, 0);
        _connectionStatus.Cursor = Cursors.Hand;
        _connectionStatus.AccessibleDescription = "点击重新读取或重连本机服务";
        _connectionStatus.Click += async (_, _) =>
        {
            if (_demo || _startupTest) return;
            if (_controller?.Snapshot.IsConnected == true) await RefreshAsync();
            else { LoadConnectionEnvironment(); await ConnectAsync(); }
        };
        footer.Controls.Add(_connectionStatus, 0, 0);
        var quit = Button("退出", colorGray);
        quit.Height = 32;
        quit.Click += (_, _) => ExitApplication();
        footer.Controls.Add(quit, 1, 0);
        AddRow(layout, footer);
        Controls.Add(layout);
    }

    private void LoadConnectionEnvironment()
    {
        _connectionOptions = GcuConnectionDiscovery.Find(_machine, out _connectionDiscoveryFailure, Logger.WriteLine);
        RenderConnectionStatus();
    }

    private async void OnShown(object? sender, EventArgs e)
    {
        StartTray();
        _refreshTimer.Start();
        if (!_demo && _machine.SupportsEcTelemetry) _ecRefreshTimer.Start();
        if (_smokeTest || _startupTest) _smokeTimer.Start();
        if (_smokeTest)
        {
            OpenCustomMode();
            OpenMoreSettings();
            OpenOverlayConfig();
        }
        await RefreshGpuRouteAsync();
        if (_closing) return;
        if (_connectionOptions is not null && !_demo) await ConnectAsync();
        if (!_demo && !_startupTest && AppConfig.IsOverlay()) EnsureOverlay().StartOverlay();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (!_demo)
        {
            MechrevoDiagnostics.WriteStartup(_machine, DeviceDpi);
            _diagnosticsStarted = true;
            LoadConnectionEnvironment();
            RenderSnapshot();
        }
        PositionAtScreenCorner(Screen.PrimaryScreen ?? Screen.FromControl(this));
        RegisterOverlayHotkey();
    }

    private void PositionAtScreenCorner(Screen screen)
    {
        Rectangle area = screen.WorkingArea;
        Location = new Point(Math.Max(area.Left, area.Right - 10 - Width),
            Math.Max(area.Top, area.Bottom - 10 - Height));
    }

    private async Task ConnectAsync()
    {
        if (_demo || _closing || _connecting || _switching || _keyboardBusy || _customModeForm?.IsOperationPending == true) return;
        if (_connectionOptions is null) LoadConnectionEnvironment();
        if (_connectionOptions is null)
        {
            ShowActionStatus(_connectionDiscoveryFailure ?? "未找到可用的本机连接资料，请检查厂商服务或连接环境变量。");
            return;
        }
        _connecting = true;
        ShowActionStatus("正在连接本机服务…");
        RenderSnapshot();
        try
        {
            // Re-identify on every new session; an installed worker or OEM configuration
            // may have changed since startup. Never reuse a dynamic protocol grant.
            _ecRefreshTimer.Stop();
            if (_controller is not null) await _controller.DisconnectAsync();
            _machine = await GcuPlatformDiscovery.DiscoverAsync(MachineInfo.Read(), Logger.WriteLine, _lifetime.Token);
            _ecSnapshot = null;
            _ecFanReader = new EcFanReader(readProtection: _machine.SupportsEcProtection);
            _machineBlockReason = _machine.GetModeControlBlockReason();
            if (_machine.SupportsEcTelemetry) _ecRefreshTimer.Start();
            LoadConnectionEnvironment();
            if (_connectionOptions is null) return;
            if (_controller is null)
            {
                _controller = new GcuController(new GcuMqttTransport(Logger.WriteLine), readAcPower: MachineInfo.ReadAcPower);
                _controller.Changed += OnControllerChanged;
            }
            await _controller.ConnectAsync(_connectionOptions, _lifetime.Token);
            if (_closing) return;
            ShowActionStatus("已连接，正在读取服务状态。");
            Logger.WriteLine("已连接本机服务。");
            await _controller.RefreshAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            if (!_closing)
            {
                ShowActionStatus("连接失败：" + SafeMessage(ex.Message));
                Logger.WriteLine("本机服务连接或初始化失败；" + MechrevoDiagnostics.ExceptionSummary(ex));
            }
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
            await RefreshGpuRouteAsync();
            if (_closing) return;
            RenderSnapshot();
            if (_controller?.Snapshot.IsConnected == true)
            {
                await _controller.RefreshAsync(_lifetime.Token);
                if (_lastRefreshFailure is not null) Logger.WriteLine("服务状态查询已恢复。");
                _lastRefreshFailure = null;
            }
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            if (!_closing)
            {
                ShowActionStatus("读取状态失败：" + SafeMessage(ex.Message));
                string failure = MechrevoDiagnostics.ExceptionSummary(ex);
                if (_lastRefreshFailure != failure) Logger.WriteLine("服务状态查询失败；" + failure);
                _lastRefreshFailure = failure;
            }
        }
        finally
        {
            _refreshing = false;
            if (!_closing) RenderSnapshot();
        }
    }

    private async Task RefreshEcAsync()
    {
        if (_demo || !_machine.SupportsEcTelemetry || _closing || _ecRefreshing) return;
        _ecRefreshing = true;
        MachineInfo readingMachine = _machine;
        try
        {
            EcFanSnapshot snapshot = await _ecFanReader.ReadAsync(_lifetime.Token);
            if (ReferenceEquals(readingMachine, _machine)) _ecSnapshot = snapshot;
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
        if (_demo || _startupTest || _closing || _connecting || _switching || _keyboardBusy || _controller is null) return;
        _machineBlockReason = _machine.GetModeControlBlockReason();
        var now = DateTimeOffset.UtcNow;
        bool thermalProtectionActive = _ecSnapshot?.IsFresh(now) == true &&
            _ecSnapshot.GenericThermalProtect == true;
        bool actualRpmFresh = HasFreshActualRpm(now);
        if (thermalProtectionActive || !actualRpmFresh || _machineBlockReason is not null ||
            !_controller.Snapshot.CanSwitchMode(mode))
        {
            ShowActionStatus(thermalProtectionActive ? "EC 报告的通用热保护已触发，暂时禁止切换模式。"
                : !actualRpmFresh ? "两路实际转速不完整或已过期，暂时不能切换模式。"
                : _machineBlockReason ?? "状态不完整或已过期，暂时不能切换模式。");
            RenderSnapshot();
            return;
        }
        _switching = true;
        ShowActionStatus("等待服务回报模式…");
        RenderSnapshot();
        try
        {
            var result = await _controller.SetModeAsync(mode, _lifetime.Token);
            if (!_closing)
                ShowActionStatus(result.ServiceConfirmed ? "服务已回报，硬件效果待实测。" : SafeMessage(result.Message));
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            if (!_closing) ShowActionStatus("切换失败：" + SafeMessage(ex.Message));
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
            if (!_customModeForm.Visible) _customModeForm.Show(this);
            _customModeForm.Activate();
            return;
        }

        _customModeForm = new CustomModeForm(_controller, () => _ecSnapshot, _demo,
            () => _machine.GetCustomControlBlockReason()) { Owner = this };
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
        RenderKeyboard();
        RenderGpuMode();
        LogServiceAvailability();
        if (_demo)
        {
            _cpuValues.Text = "CPU: 48 ℃  风扇: " + (_fanRpm ? "2100 RPM" : "35%");
            _gpuValues.Text = "GPU: 43 ℃  风扇: " + (_fanRpm ? "2000 RPM" : "35%");
            _reportedMode.Text = "性能模式：均衡";
            _controlMessage = "演示布局，可打开风扇与功耗预览曲线。";
            foreach (var (mode, button) in _modeButtons)
            {
                button.Enabled = false;
                button.Activated = mode == PerformanceMode.Balanced;
            }
            _openCustomMode.Enabled = true;
            RenderConnectionStatus();
            RenderTray();
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
        bool bothRpmFresh = cpuRpm.HasValue && gpuRpm.HasValue && HasFreshActualRpm(now);
        string cpuFan = _fanRpm ? Value(cpuRpm, cpuRpm.HasValue, " RPM")
            : Value(snapshot?.CpuFanDuty, connected && snapshot!.IsCpuFanDutyFresh(now), "%");
        string gpuFan = _fanRpm ? Value(gpuRpm, gpuRpm.HasValue, " RPM")
            : Value(snapshot?.GpuFanDuty, connected && snapshot!.IsGpuFanDutyFresh(now), "%");
        _cpuValues.Text = $"CPU: {Value(snapshot?.CpuTemperature, connected && snapshot!.IsCpuTemperatureFresh(now), " ℃")}  风扇: {cpuFan}";
        _gpuValues.Text = $"GPU: {Value(snapshot?.GpuTemperature, connected && snapshot!.IsGpuTemperatureFresh(now), " ℃")}  风扇: {gpuFan}";
        bool modeFresh = connected && snapshot!.IsStatusFresh();
        _reportedMode.Text = "性能模式：" + (modeFresh ? ModeName(snapshot!.Mode) : "未收到");
        _controlMessage = thermalProtectionActive ? "EC 报告的通用热保护已触发，模式切换暂时禁用。"
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
            button.Enabled = !_startupTest && !_connecting && !_switching && !_keyboardBusy && !thermalProtectionActive && bothRpmFresh &&
                _machineBlockReason is null && connected && snapshot!.CanSwitchMode(mode, now);
        }
        _openCustomMode.Enabled = !_startupTest && !_connecting && !_switching && connected && _controller is not null;
        foreach (RButton button in _modeButtons.Values) _keyboardTips.SetToolTip(button, _controlMessage);
        _keyboardTips.SetToolTip(_reportedMode, _controlMessage);
        RenderConnectionStatus();
        RenderTray();
    }

    private void ShowActionStatus(string message)
    {
        _actionMessage = message;
        _actionMessageUntil = DateTimeOffset.UtcNow.AddSeconds(8);
        RenderConnectionStatus();
    }

    private void RenderConnectionStatus()
    {
        if (_closing || IsDisposed) return;
        var snapshot = _controller?.Snapshot;
        string connection = _demo ? "演示 · 未连接服务" : _connecting ? "正在处理连接…"
            : snapshot?.IsConnected == true ? "已连接本机服务"
            : !string.IsNullOrWhiteSpace(snapshot?.LastError) ? "连接不可用：" + SafeMessage(snapshot.LastError)
            : _connectionDiscoveryFailure ?? "尚未连接";
        _connectionStatus.Text = DateTimeOffset.UtcNow < _actionMessageUntil ? _actionMessage : connection;
        _connectionStatus.AccessibleName = _connectionStatus.Text;
        _keyboardTips.SetToolTip(_connectionStatus,
            string.Join("\n", new[] { connection, _controlMessage, _actionMessage }.Where(text => !string.IsNullOrWhiteSpace(text))));
    }

    private void LogServiceAvailability()
    {
        if (_demo || !_diagnosticsStarted) return;
        GcuSnapshot snapshot = _controller?.Snapshot ?? new();
        string state = $"连接={snapshot.IsConnected}；模式={snapshot.IsConnected && snapshot.IsStatusFresh()}；处理器温度={snapshot.IsConnected && snapshot.IsCpuTemperatureFresh()}；显卡温度={snapshot.IsConnected && snapshot.IsGpuTemperatureFresh()}；键盘={snapshot.IsConnected && snapshot.IsKeyboardFresh()}";
        if (_loggedServiceState == state) return;
        _loggedServiceState = state;
        Logger.WriteLine("服务状态可用性：" + state);
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose) return;
        if (!_exitRequested && e.CloseReason == CloseReason.UserClosing && _trayIcon?.Visible == true)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        bool systemClosing = e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing;
        e.Cancel = !systemClosing;
        if (_closing) return;
        _closing = true;
        DisposeTray();
        _refreshTimer.Stop();
        _ecRefreshTimer.Stop();
        _smokeTimer.Stop();
        _lifetime.Cancel();
        try
        {
            CloseKeyboardAndOverlay();
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
            _connectionOptions = null;
            _allowClose = true;
            if (!systemClosing && IsHandleCreated && !IsDisposed) BeginInvoke((Action)Close);
        }
    }

    private string SafeMessage(string message) => string.IsNullOrEmpty(_connectionOptions?.Password)
        ? message : message.Replace(_connectionOptions.Password, "[已隐藏]", StringComparison.Ordinal);

    private static string Value(int? value, bool fresh, string unit) => fresh && value.HasValue
        ? value.Value.ToString() + unit : "未收到";

    private bool HasFreshActualRpm(DateTimeOffset now)
    {
        if (!_machine.HasVerifiedBoard && _machine.SupportsModeControl &&
            (_ecSnapshot?.IsFresh(now) != true || _ecSnapshot.GenericThermalProtect is null ||
             _ecSnapshot.FanSafetyProtect is null)) return false;
        if (_ecSnapshot?.IsFresh(now) == true) return true;
        var snapshot = _controller?.Snapshot;
        return snapshot?.IsConnected == true && snapshot.IsCpuFanFresh(now) && snapshot.IsGpuFanFresh(now);
    }

    private static string ModeName(int? mode) => mode switch { 0 => "安静", 1 => "均衡", 2 => "狂暴", 3 => "自定义", _ => "未收到" };


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
        title.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        values.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        values.Margin = Padding.Empty;
        values.TextAlign = ContentAlignment.TopRight;
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
}
