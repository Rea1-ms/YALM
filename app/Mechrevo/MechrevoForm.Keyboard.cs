using GHelper;
using GHelper.Overlay;
using GHelper.UI;
using System.Runtime.InteropServices;

namespace YALM.Mechrevo;

internal sealed partial class MechrevoForm
{
    private readonly RComboBox _keyboardEffect = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly RButton _keyboardMore = Button("更多", colorGray);
    private readonly RButton _fnLock = Button("FN-Lock", colorStandard);
    private readonly RButton _overlayButton = Button("屏幕叠加", colorEco);
    private KeyboardSettingsForm? _keyboardForm;
    private OverlayConfig? _overlayConfig;
    private HardwareOverlay? _hardwareOverlay;
    private bool _keyboardBusy;
    private bool _updatingKeyboard;
    private bool _overlayHotkey;
    private const int OverlayHotkeyId = 0x5941;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int id);

    private void BuildKeyboardLayout(TableLayoutPanel layout)
    {
        var title = TextLabel("笔记本键盘");
        title.Font = new Font(Font, FontStyle.Bold);
        var heading = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
        heading.Controls.Add(MechrevoUi.Heading(this, title, GHelper.Properties.Resources.icons8_keyboard_32), 0, 0);
        foreach (RButton button in new[] { _overlayButton, _fnLock })
        {
            button.Height = 23;
            button.Font = new Font("Segoe UI", 7.125F, FontStyle.Bold);
            button.Secondary = true;
            button.Borderless = true;
            button.BorderRadius = 2;
            button.Anchor = AnchorStyles.Right;
        }
        _overlayButton.Click += (_, _) => OpenOverlayConfig();
        _overlayButton.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ToggleOverlay(); };
        _fnLock.Click += async (_, _) =>
        {
            if (_controller?.Snapshot.FnLocked is bool locked)
                await RunKeyboardAsync(() => _controller.SetFnLockAsync(!locked, _lifetime.Token));
        };
        heading.Controls.Add(_overlayButton, 1, 0);
        heading.Controls.Add(_fnLock, 2, 0);
        AddRow(layout, heading);

        var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 2, 0, 16) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _keyboardEffect.Items.AddRange(["灯效未回报", "恒亮（静态）", "彩虹"]);
        _keyboardEffect.AccessibleName = "笔记本键盘灯效";
        _keyboardEffect.Margin = new Padding(2, 4, 6, 2);
        _keyboardEffect.SelectionChangeCommitted += async (_, _) =>
        {
            if (_updatingKeyboard || _keyboardEffect.SelectedIndex < 1 || _controller is null) return;
            GcuKeyboardSettings? settings = _controller.Snapshot.Keyboard;
            if (settings?.Brightness is not int brightness || settings.MonochromeIndex is not int color) return;
            string effect = _keyboardEffect.SelectedIndex == 1 ? "Single" : "Rainbow";
            await RunKeyboardAsync(() => _controller.SetKeyboardEffectAsync(effect, brightness, color, _lifetime.Token));
        };
        _keyboardMore.Height = 30;
        _keyboardMore.Image = GHelper.Properties.Resources.icons8_settings_32;
        _keyboardMore.TextImageRelation = TextImageRelation.ImageBeforeText;
        _keyboardMore.Secondary = true;
        _keyboardMore.Click += (_, _) => OpenKeyboardSettings();
        row.Controls.Add(_keyboardEffect, 0, 0);
        row.Controls.Add(_keyboardMore, 1, 0);
        AddRow(layout, row);
    }

    private GcuSnapshot KeyboardSnapshot => _demo ? new GcuSnapshot
    {
        IsConnected = true, FnLocked = false, FnLockUpdatedAt = DateTimeOffset.UtcNow,
        Keyboard = new() { Type = "SingleZone", Effect = "Single", Brightness = 3, MonochromeIndex = 17, Power = true },
        KeyboardUpdatedAt = DateTimeOffset.UtcNow
    } : _controller?.Snapshot ?? new();

    private void RenderKeyboard()
    {
        GcuSnapshot snapshot = KeyboardSnapshot;
        bool fresh = snapshot.IsKeyboardFresh();
        bool available = !_startupTest && !_keyboardBusy && !_connecting && !_switching;
        _fnLock.Enabled = available && !_demo && snapshot.IsFnLockFresh();
        _fnLock.Activated = snapshot.IsFnLockFresh() && snapshot.FnLocked == true;
        _fnLock.BackColor = _fnLock.Activated ? colorStandard : buttonSecond;
        _fnLock.ForeColor = _fnLock.Activated ? SystemColors.ControlLightLight : SystemColors.ControlDark;
        _fnLock.AccessibleName = "功能键锁定：" + (!snapshot.IsFnLockFresh() ? "未收到" : snapshot.FnLocked == true ? "开启" : "关闭");
        _keyboardEffect.Enabled = available && !_demo && fresh &&
            snapshot.Keyboard is { SingleZone: true, Brightness: not null, MonochromeIndex: not null };
        _keyboardMore.Enabled = available && (_demo || fresh && snapshot.Keyboard?.SingleZone == true);
        _overlayButton.Enabled = !_startupTest;
        _overlayButton.Activated = !_demo && AppConfig.IsOverlay();
        _overlayButton.BackColor = _overlayButton.Activated ? colorEco : buttonSecond;
        _overlayButton.ForeColor = _overlayButton.Activated ? SystemColors.ControlLightLight : SystemColors.ControlDark;
        _overlayButton.AccessibleName = "屏幕叠加：" + (_overlayButton.Activated ? "开启" : "关闭");
        _updatingKeyboard = true;
        _keyboardEffect.SelectedIndex = !fresh ? 0 : snapshot.Keyboard?.Effect switch { "Single" => 1, "Rainbow" => 2, _ => 0 };
        _updatingKeyboard = false;
    }

    private async Task RunKeyboardAsync(Func<Task<GcuWriteResult>> operation)
    {
        if (_demo || _startupTest || _closing || _keyboardBusy) return;
        _keyboardBusy = true;
        _actionStatus.Text = "正在设置键盘…";
        RenderSnapshot();
        try { _actionStatus.Text = (await operation()).Message; }
        catch (OperationCanceledException) when (_closing) { }
        catch { if (!_closing) _actionStatus.Text = "键盘设置未完成，请检查厂商服务。"; }
        finally { _keyboardBusy = false; if (!_closing) RenderSnapshot(); }
    }

    private void OpenKeyboardSettings()
    {
        if (_keyboardForm is { IsDisposed: false }) { _keyboardForm.Activate(); return; }
        _keyboardForm = new KeyboardSettingsForm(_controller, _demo) { Owner = this };
        _keyboardForm.Show(this);
    }

    private HardwareOverlay EnsureOverlay() => _hardwareOverlay ??= new HardwareOverlay(this, ReadOverlay);

    private OverlayReadings ReadOverlay()
    {
        GcuSnapshot snapshot = _controller?.Snapshot ?? new();
        EcFanSnapshot? ec = _ecSnapshot;
        return new()
        {
            CpuTemperature = snapshot.IsCpuTemperatureFresh() ? snapshot.CpuTemperature : null,
            GpuTemperature = snapshot.IsGpuTemperatureFresh() ? snapshot.GpuTemperature : null,
            CpuFanRpm = ec?.IsFresh() == true ? ec.CpuFanRpm : snapshot.IsCpuFanFresh() ? snapshot.CpuFanRpm : null,
            GpuFanRpm = ec?.IsFresh() == true ? ec.GpuFanRpm : snapshot.IsGpuFanFresh() ? snapshot.GpuFanRpm : null,
            CpuUsage = snapshot.IsCpuUsageFresh() ? snapshot.CpuUsage : null,
            GpuUsage = snapshot.IsGpuUsageFresh() ? snapshot.GpuUsage : null
        };
    }

    private void OpenOverlayConfig()
    {
        if (_overlayConfig is { IsDisposed: false }) { _overlayConfig.Close(); return; }
        _overlayConfig = new OverlayConfig(ToggleOverlay, ToggleOverlayGameOnly, EnsureOverlay(), this,
            limitedTelemetry: true, readOnly: _demo);
        _overlayConfig.Show(this);
    }

    private void ToggleOverlay()
    {
        if (_demo) return;
        bool enable = !AppConfig.IsOverlay();
        AppConfig.Set("overlay", enable ? 1 : 0);
        if (enable) EnsureOverlay().StartOverlay(); else _hardwareOverlay?.StopOverlay();
        RenderKeyboard();
    }

    private void ToggleOverlayGameOnly()
    {
        if (_demo) return;
        AppConfig.Set("overlay_game_only", AppConfig.IsOverlayGameOnly() ? 0 : 1);
        if (AppConfig.IsOverlay()) { EnsureOverlay().StopOverlay(); EnsureOverlay().StartOverlay(); }
    }

    private void RegisterOverlayHotkey()
    {
        if (!_demo && !_startupTest) _overlayHotkey = RegisterHotKey(Handle, OverlayHotkeyId, 0x4007, (uint)Keys.O);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && message.WParam.ToInt32() == OverlayHotkeyId) ToggleOverlay();
        base.WndProc(ref message);
    }

    private void CloseKeyboardAndOverlay()
    {
        if (_overlayHotkey) UnregisterHotKey(Handle, OverlayHotkeyId);
        _overlayConfig?.Close();
        _keyboardForm?.Close();
        _hardwareOverlay?.Close();
    }
}
