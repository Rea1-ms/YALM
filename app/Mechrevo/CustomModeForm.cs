using GHelper.UI;

namespace YALM.Mechrevo;

internal sealed partial class CustomModeForm : RForm
{
    private sealed record SettingEditor(
        GcuCustomWriteField Field,
        string Title,
        string Unit,
        RNumericUpDown Input,
        RTrackBar Slider,
        RButton Apply);

    private readonly GcuController _controller;
    private readonly bool _demo;
    private readonly Func<EcFanSnapshot?> _ecSnapshot;
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 5000 };
    private readonly RComboBox _profile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Panel _pages = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, (RButton Button, Control Page)> _navigation = new();
    private readonly RButton _activateProfile = ActionButton("切换档位", RForm.colorCustom);
    private readonly RButton _refresh = ActionButton("重新读取", RForm.colorStandard);
    private readonly RButton _fanBoost = ActionButton("风扇增强", RForm.colorEco);
    private readonly RButton _tccEnabled = ActionButton("温度墙", RForm.colorStandard);
    private readonly RButton _fanSwitchEnabled = ActionButton("风扇切换", RForm.colorStandard);
    private readonly Label _summary = InfoLabel("尚未读取自定义设置。");
    private readonly Label _status = InfoLabel("");
    private readonly Label _readonlyValues = InfoLabel("");
    private readonly Label _curveTable = InfoLabel("风扇表：未收到");
    private readonly FanCurveEditor _cpuCurve = new("CPU 风扇配置文件，%/℃");
    private readonly FanCurveEditor _gpuCurve = new("GPU 风扇配置文件，%/℃");
    private readonly RButton _applyCpuCurve = ActionButton("应用 CPU 曲线", RForm.colorStandard);
    private readonly RButton _applyGpuCurve = ActionButton("应用 GPU 曲线", RForm.colorTurbo);
    private readonly List<SettingEditor> _settings = [];
    private CancellationTokenSource _lifetime = new();
    private GpuDriverPower? _driverPower;
    private bool _busy;
    private bool _closing;
    private int? _displayedProfile;
    private string? _editorIdentity;

    private GcuSnapshot Snapshot => _demo ? DemoSnapshot() : _controller.Snapshot;

    public CustomModeForm(GcuController? controller, Func<EcFanSnapshot?> ecSnapshot, bool demo = false)
    {
        if (!demo) ArgumentNullException.ThrowIfNull(controller);
        _controller = controller!;
        _demo = demo;
        _ecSnapshot = ecSnapshot ?? throw new ArgumentNullException(nameof(ecSnapshot));

        Text = "YALM · 风扇与功耗";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9F);
        ClientSize = new Size(850, 710);
        MinimumSize = new Size(820, 650);
        StartPosition = FormStartPosition.CenterParent;
        Padding = new Padding(10);

        BuildLayout();
        InitTheme(true);
        ApplyThemeColors();
        if (!_demo) _controller.Changed += OnControllerChanged;
        _refreshTimer.Tick += async (_, _) => await RefreshCustomAsync(false);
        Shown += async (_, _) =>
        {
            if (_demo) RenderSnapshot(loadEditors: true);
            else
            {
                _refreshTimer.Start();
                await RefreshCustomAsync(true);
                await RefreshGpuPowerAsync();
            }
        };
        FormClosed += OnClosed;
        RenderSnapshot(loadEditors: false);
    }

    private async Task RefreshCustomAsync(bool reloadEditors)
    {
        if (_demo || _busy || _closing) return;
        _busy = true;
        _status.Text = "正在读取服务状态…";
        RenderSnapshot(loadEditors: false);
        try
        {
            GcuWriteResult result = await _controller.RefreshCustomAsync(_lifetime.Token);
            _status.Text = result.Message;
            if (result.Status == GcuWriteStatus.Confirmed)
                RenderSnapshot(reloadEditors);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch
        {
            _status.Text = "读取自定义设置失败。";
        }
        finally
        {
            _busy = false;
            RenderSnapshot(loadEditors: false);
        }
    }

    private async Task RefreshGpuPowerAsync()
    {
        if (_demo || _closing) return;
        try
        {
            GpuDriverPower? power = await GpuPowerReader.ReadAsync(_lifetime.Token);
            if (!_closing)
            {
                _driverPower = power;
                RenderSnapshot(loadEditors: false);
            }
        }
        catch (OperationCanceledException) when (_closing) { }
    }

    private async Task RunAsync(
        string pending,
        Func<Task<GcuWriteResult>> action,
        bool requireCustomMode = true)
    {
        if (_demo || _busy || _closing) return;
        if (!CanWrite(requireCustomMode))
        {
            _status.Text = "当前状态不允许写入：请连接服务、切换到自定义模式，并等待新鲜状态。";
            return;
        }
        _busy = true;
        _status.Text = pending;
        RenderSnapshot(loadEditors: false);
        try
        {
            GcuWriteResult result = await action();
            _status.Text = result.Message;
            RenderSnapshot(loadEditors: false);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch
        {
            _status.Text = "参数请求失败，实际状态尚未确认。";
        }
        finally
        {
            _busy = false;
            RenderSnapshot(loadEditors: false);
        }
    }

    private async Task ApplyCurveAsync(FanCurveTarget target, FanCurveEditor editor)
    {
        if (!editor.TryGetDuties(out int[] duties, out string error))
        {
            _status.Text = error;
            return;
        }
        await RunAsync("正在写入风扇曲线…", () => _controller.SetFanCurveAsync(target, duties, _lifetime.Token));
    }
    private bool CanWrite(bool requireCustomMode = true)
    {
        if (_demo) return false;
        GcuSnapshot snapshot = Snapshot;
        if (!snapshot.IsConnected || (requireCustomMode && snapshot.Mode != 3) ||
            !snapshot.IsCustomSettingsFresh() || !snapshot.IsFanTableFresh())
            return false;
        EcFanSnapshot? ec = _ecSnapshot();
        return ec is not { GenericThermalProtect: true } && ec?.IsFresh() != false;
    }

    private void RenderSnapshot(bool loadEditors)
    {
        if (_closing || IsDisposed) return;
        GcuSnapshot snapshot = Snapshot;
        GcuCustomSettings? settings = snapshot.CustomSettings;
        FanCurveTable? table = snapshot.FanTable;
        bool connected = snapshot.IsConnected;
        bool customFresh = connected && snapshot.IsCustomSettingsFresh();
        bool mode3 = snapshot.Mode == 3;
        bool fanTableFresh = connected && snapshot.IsFanTableFresh();
        bool canWrite = CanWrite() && !_busy;
        EcFanSnapshot? ec = _ecSnapshot();
        int? currentProfile = settings?.ProfileIndex;

        string identity = $"{currentProfile}:{table?.Name}";
        loadEditors |= identity != _editorIdentity;
        _editorIdentity = identity;
        if (currentProfile is int profile && currentProfile != _displayedProfile)
        {
            _profile.SelectedIndex = Math.Clamp(profile, 0, _profile.Items.Count - 1);
            _displayedProfile = profile;
        }
        _activateProfile.Enabled = !_demo && connected && !_busy;
        _refresh.Enabled = connected && !_busy;
        _profile.Enabled = !_demo && connected && !_busy;
        _fanBoost.Enabled = canWrite;
        _fanBoost.Activated = customFresh && settings?.FanBoostEnabled == true;
        _fanBoost.Text = "风扇增强：" + State(settings?.FanBoostEnabled, customFresh);
        _tccEnabled.Enabled = canWrite;
        _tccEnabled.Activated = customFresh && settings?.TccEnabled == true;
        _tccEnabled.Text = "温度墙：" + State(settings?.TccEnabled, customFresh);
        _fanSwitchEnabled.Enabled = canWrite;
        _fanSwitchEnabled.Activated = customFresh && settings?.FanSwitchSpeedEnabled == true;
        _fanSwitchEnabled.Text = "风扇切换：" + State(settings?.FanSwitchSpeedEnabled, customFresh);

        _summary.Text =
            (mode3
                ? $"当前自定义档位：{(currentProfile.HasValue ? (currentProfile + 1).ToString() : "未收到")}；" +
                  (settings?.ProfileName is { Length: > 0 } name ? $"服务名称：{name}；" : "")
                : "当前不在自定义模式；") +
            $"风扇增强：{State(settings?.FanBoostEnabled, customFresh)}";

        string driverPower = _driverPower switch
        {
            { Error: not null } => $"驱动功率：{_driverPower.Error}",
            { MinimumLimit: double minimum, MaximumLimit: double maximum } =>
                $"驱动功率范围：{minimum:0.#}～{maximum:0.#} 瓦\r\n" +
                $"默认：{Metric(_driverPower.DefaultLimit, " 瓦")}\r\n" +
                $"当前限制：{Metric(_driverPower.CurrentLimit, " 瓦")}",
            _ => "驱动功率：尚未读到。"
        };
        _readonlyValues.Text =
            $"服务目标：{Value(settings?.GpuTgp, customFresh, " 瓦")}\r\n" +
            $"动态升压：{Value(settings?.GpuDynamicBoost, customFresh, " 瓦")}\r\n" +
            $"核心偏移：{Value(settings?.GpuCoreClockOffset, customFresh, " 兆赫")}\r\n" +
            $"显存偏移：{Value(settings?.GpuMemoryClockOffset, customFresh, " 兆赫")}\r\n\r\n" +
            driverPower + "\r\n" +
            "服务目标由厂商模式决定；驱动范围以本机 GPU 和固件为准。" +
            (ec is { FanSafetyProtect: true } ? "\r\n风扇安全保护正在触发，预设模式切换不受影响；自定义写入仍受通用热保护和读数新鲜度约束。" : "");

        _curveTable.Text =
            $"风扇表：{(table?.Name is { Length: > 0 } tableName ? tableName : "未收到")}；" +
            $"激活：{State(table?.Activated, fanTableFresh)}；" +
            $"独立输出：{State(table?.ControlRespective, fanTableFresh)}（只读）";

        UpdateSettings(settings, canWrite, loadEditors);
        _cpuCurve.SetCurve(table?.Cpu, identity, loadEditors);
        _gpuCurve.SetCurve(table?.Gpu, identity, loadEditors);
        _cpuCurve.SetEditable((canWrite || _demo) && fanTableFresh);
        _gpuCurve.SetEditable((canWrite || _demo) && fanTableFresh);
        _applyCpuCurve.Enabled = canWrite && fanTableFresh && _cpuCurve.HasChanges;
        _applyGpuCurve.Enabled = canWrite && fanTableFresh && _gpuCurve.HasChanges;
        if (_demo) _status.Text = "演示 · 拖点仅预览，不连接服务、不控制硬件；重新读取可恢复示例。";
        if (!connected)
            _status.Text = snapshot.LastError ?? "尚未连接厂商服务。";
    }

    private void UpdateSettings(GcuCustomSettings? settings, bool canWrite, bool loadEditors)
    {
        foreach (SettingEditor editor in _settings)
        {
            (int? value, int? minimum, int? maximum, int step) = editor.Field switch
            {
                GcuCustomWriteField.Pl1 => (settings?.Pl1, settings?.Pl1Minimum, settings?.Pl1Maximum, 1),
                GcuCustomWriteField.Pl2 => (settings?.Pl2, settings?.Pl2Minimum, settings?.Pl2Maximum, 1),
                GcuCustomWriteField.Pl4 => (settings?.Pl4, settings?.Pl4Minimum, settings?.Pl4Maximum,
                    settings?.Pl4HalfWatt == true ? 2 : 1),
                GcuCustomWriteField.TccTarget => (settings?.TccTarget, 75, settings?.TjMax is { } tjMax ? Math.Min(95, tjMax) : 95, 1),
                GcuCustomWriteField.FanSwitchSpeed => (settings?.FanSwitchSpeed, 100, 25500, 100),
                _ => (null, null, null, 1)
            };
            bool available = (canWrite || _demo) && value.HasValue && minimum.HasValue && maximum.HasValue && minimum <= maximum;
            editor.Input.Enabled = available;
            editor.Slider.Enabled = available;
            editor.Apply.Enabled = available && !_demo;
            if (minimum.HasValue && maximum.HasValue && minimum <= maximum)
            {
                editor.Input.Maximum = Math.Max(editor.Input.Maximum, maximum.Value);
                editor.Input.Minimum = minimum.Value;
                editor.Input.Maximum = maximum.Value;
                editor.Input.Increment = step;
                editor.Slider.Maximum = Math.Max(editor.Slider.Maximum, maximum.Value);
                editor.Slider.Minimum = minimum.Value;
                editor.Slider.Maximum = maximum.Value;
                editor.Slider.SmallChange = step;
                editor.Slider.LargeChange = step * 5;
            }
            if (loadEditors && value.HasValue && minimum.HasValue && maximum.HasValue && minimum <= maximum)
                editor.Input.Value = Math.Clamp(value.Value, minimum.Value, maximum.Value);
        }
    }

    private void OnControllerChanged()
    {
        if (_closing || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((Action)(() => RenderSnapshot(loadEditors: false))); }
        catch (InvalidOperationException) { }
    }

    private void ApplyThemeColors()
    {
        BackColor = RForm.formBack;
        ForeColor = RForm.foreMain;
        _summary.ForeColor = RForm.foreMain;
        _status.ForeColor = RForm.foreMain;
        _readonlyValues.ForeColor = RForm.foreMain;
        _curveTable.ForeColor = RForm.foreMain;
        _cpuCurve.ApplyThemeColors();
        _gpuCurve.ApplyThemeColors();
    }

    private void OnClosed(object? sender, FormClosedEventArgs e)
    {
        _closing = true;
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
        if (!_demo) _controller.Changed -= OnControllerChanged;
    }

    private static Label InfoLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(265, 0),
        Margin = new Padding(4, 6, 4, 6)
    };

    private static string Metric(double? value, string unit) =>
        value.HasValue ? $"{value.Value:0.#}{unit}" : "未报告";

    private static Label TextLabel(string text, float size = 9.5F, FontStyle style = FontStyle.Regular) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Microsoft YaHei UI", size, style),
        Margin = new Padding(4, 6, 8, 6)
    };

    private static RButton ActionButton(string text, Color color) => new()
    {
        Text = text,
        Height = 38,
        Width = 120,
        Margin = new Padding(4),
        BorderColor = color,
        BorderRadius = 5
    };

    private static string State(bool? value, bool fresh) => !fresh || !value.HasValue ? "未收到" : value.Value ? "开" : "关";

    private static string Value(int? value, bool fresh, string unit) => fresh && value.HasValue ? value.Value + unit : "未收到";
}
