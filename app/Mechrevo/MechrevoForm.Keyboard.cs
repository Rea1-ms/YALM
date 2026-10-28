using GHelper;
using GHelper.Overlay;
using GHelper.UI;
using System.Runtime.InteropServices;

namespace YALM.Mechrevo;

internal sealed partial class MechrevoForm
{
    private readonly RComboBox _keyboardEffect = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly RButton _keyboardMore = Button("更多", colorGray);
    private readonly RColorButton _keyboardColor = new() { Text = "颜色", BorderRadius = 2 };
    private readonly RButton _fnLock = Button("FN-Lock", colorStandard);
    private readonly RButton _winLock = Button("Win-Lock", colorStandard);
    private readonly RButton _backlightButton = Button("背光", colorEco);
    private readonly RButton _overlayButton = Button("屏幕叠加", colorEco);
    private MoreSettingsForm? _settingsForm;
    private OverlayConfig? _overlayConfig;
    private HardwareOverlay? _hardwareOverlay;
    private bool _keyboardBusy;
    private bool _updatingKeyboard;
    private bool _overlayHotkey;
    private readonly ToolTip _keyboardTips = new();
    private DateTimeOffset? _savedKeyboardAt;
    private GcuKeyboardSavedProfile? _savedKeyboard;
    private GcuKeyboardSettings _demoKeyboard = new()
    {
        Type = "SingleZone", Effect = "Single", Brightness = 3, MonochromeIndex = 17, Power = true,
        ManualIndices = new[] { 1, 5, 9, 13, 21, 25 }, ManualInterval = 4, BreathingIndex = 1
    };
    private const int OverlayHotkeyId = 0x5941;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int id);

    private void BuildKeyboardLayout(TableLayoutPanel layout)
    {
        var title = TextLabel("笔记本键盘");
        title.Font = new Font(Font, FontStyle.Bold);
        var heading = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 5, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.Controls.Add(MechrevoUi.Heading(this, title, GHelper.Properties.Resources.icons8_keyboard_32), 0, 0);
        foreach (RButton button in new[] { _backlightButton, _overlayButton, _fnLock, _winLock })
        {
            button.Height = 20;
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.Padding = new Padding(6, 0, 6, 0);
            button.Font = new Font("Segoe UI", 7.125F, FontStyle.Bold);
            button.Secondary = true;
            button.Borderless = true;
            button.BorderRadius = 2;
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(2, 0, 2, 0);
        }
        _overlayButton.Click += (_, _) => OpenOverlayConfig();
        _overlayButton.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ToggleOverlay(); };
        _fnLock.Click += async (_, _) =>
        {
            if (_controller?.Snapshot.FnLocked is bool locked)
                await RunKeyboardAsync(() => _controller.SetFnLockAsync(!locked, _lifetime.Token));
        };
        _winLock.Click += async (_, _) => await ToggleQuickSettingAsync(GcuQuickSetting.WinLock);
        _backlightButton.Click += async (_, _) => await ToggleBacklightPowerAsync();
        heading.Controls.Add(_backlightButton, 1, 0);
        heading.Controls.Add(_overlayButton, 2, 0);
        heading.Controls.Add(_fnLock, 3, 0);
        heading.Controls.Add(_winLock, 4, 0);
        AddRow(layout, heading);

        var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 2, 0, 0) };
        for (int i = 0; i < 3; i++) row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 3));
        _keyboardEffect.Items.AddRange(["恒亮（静态）", "彩虹", "自定义"]);
        _keyboardEffect.PlaceholderText = "选择灯效";
        _keyboardEffect.AccessibleName = "笔记本键盘灯效";
        _keyboardEffect.Dock = DockStyle.Top;
        _keyboardEffect.SelectionChangeCommitted += async (_, _) =>
        {
            if (_updatingKeyboard || _keyboardEffect.SelectedIndex < 0) return;
            string effect = _keyboardEffect.SelectedIndex switch { 0 => "Single", 1 => "Rainbow", _ => "Manual" };
            if (_demo) { _demoKeyboard = _demoKeyboard with { Effect = effect }; RenderKeyboard(); return; }
            if (effect == "Manual") await EditManualColorsAsync();
            else if (CreateKeyboardDraft(effect) is { } draft)
                await ApplyKeyboardDraftAsync(draft, ReadSavedKeyboard(KeyboardSnapshot));
            RenderKeyboard();
        };
        _keyboardColor.Dock = _keyboardMore.Dock = DockStyle.Top;
        _keyboardColor.Click += async (_, _) => await EditKeyboardColorsAsync();
        _keyboardTips.SetToolTip(_keyboardEffect, "灯效名缺失时，显示与服务参数一致的厂商保存配置");
        _keyboardMore.Image = GHelper.Properties.Resources.icons8_settings_32;
        _keyboardMore.ImageAlign = ContentAlignment.MiddleRight;
        _keyboardMore.TextAlign = ContentAlignment.MiddleCenter;
        _keyboardMore.TextImageRelation = TextImageRelation.ImageBeforeText;
        _keyboardMore.BorderColor = Color.Transparent;
        _keyboardMore.BorderRadius = 2;
        _keyboardMore.AccessibleName = "更多设置";
        _keyboardMore.Secondary = true;
        _keyboardMore.Click += (_, _) => OpenMoreSettings(toggle: true);
        row.Controls.Add(_keyboardEffect, 0, 0);
        row.Controls.Add(_keyboardColor, 1, 0);
        row.Controls.Add(_keyboardMore, 2, 0);
        // RComboBox calibrates its native height after its handle is created.
        // Match the buttons' painted edges, including RButton's parent-color pen.
        row.Layout += (_, _) => AlignKeyboardRow();
        _keyboardEffect.SizeChanged += (_, _) => AlignKeyboardRow();
        AlignKeyboardRow();
        AddRow(layout, row);
    }

    private void AlignKeyboardRow()
    {
        // Same border width and inset as upstream RButton.OnPaint.
        float ratio = DeviceDpi / 192F;
        int border = (int)Math.Round((ratio * 5 - 1) / 2) * 2 + 1;
        int inset = border / 2 + 1;
        _keyboardEffect.Margin = new Padding(3, inset + 1, 3, inset + 1);
        _keyboardColor.Margin = _keyboardMore.Margin = new Padding(2, 1, 2, 1);
        _keyboardColor.Height = _keyboardMore.Height = _keyboardEffect.Height + border;
    }

    private GcuSnapshot KeyboardSnapshot => _demo ? new GcuSnapshot
    {
        IsConnected = true, FnLocked = false, FnLockUpdatedAt = DateTimeOffset.UtcNow,
        QuickSettings = GcuQuickSettings.Definitions.ToDictionary(definition => definition.Setting,
            definition => new GcuQuickSettingState(definition.Setting is GcuQuickSetting.TouchpadHotkey or GcuQuickSetting.AcRecovery, DateTimeOffset.UtcNow)),
        Keyboard = _demoKeyboard,
        KeyboardUpdatedAt = DateTimeOffset.UtcNow
    } : _controller?.Snapshot ?? new();

    private GcuKeyboardSavedProfile? ReadSavedKeyboard(GcuSnapshot snapshot, bool force = false)
    {
        if (!snapshot.IsKeyboardFresh() || snapshot.Keyboard?.SingleZone != true) return null;
        if (_demo) return new(_demoKeyboard.Effect!, _demoKeyboard, Enumerable.Repeat(new GcuRgb(255, 0, 0), 7).ToArray());
        if (force || _savedKeyboardAt != snapshot.KeyboardUpdatedAt)
        {
            _savedKeyboard = GcuKeyboardStorage.Read(_machine, snapshot.Keyboard);
            _savedKeyboardAt = snapshot.KeyboardUpdatedAt;
        }
        return _savedKeyboard;
    }

    private GcuKeyboardDraft? CreateKeyboardDraft(string effect) =>
        GcuKeyboardDraft.Create(KeyboardSnapshot.Keyboard, ReadSavedKeyboard(KeyboardSnapshot), effect);

    private async Task ApplyKeyboardDraftAsync(GcuKeyboardDraft draft, GcuKeyboardSavedProfile? expectedSaved)
    {
        if (_closing) return;
        GcuKeyboardSavedProfile? saved = ReadSavedKeyboard(KeyboardSnapshot, force: true);
        if (saved is null || expectedSaved is null || saved.Effect != expectedSaved.Effect || saved.IsCircular != expectedSaved.IsCircular ||
            !saved.Colors.SequenceEqual(expectedSaved.Colors))
        {
            ShowActionStatus("厂商保存配置已变化或无法读取，请重新读取后再应用。");
            return;
        }
        if (_controller is { } current)
            await RunKeyboardAsync(() => current.SetKeyboardEffectAsync(draft, _lifetime.Token));
    }

    private async Task EditKeyboardColorsAsync()
    {
        GcuSnapshot snapshot = KeyboardSnapshot;
        GcuKeyboardSavedProfile? saved = ReadSavedKeyboard(snapshot);
        string? effect = snapshot.Keyboard?.Effect ?? saved?.Effect;
        if (effect == "Manual") { await EditManualColorsAsync(); return; }
        if (effect != "Single" || CreateKeyboardDraft("Single") is not { } draft) return;
        GcuRgb rgb = draft.ColorAt(0);
        using var picker = new RColorPicker(Color.FromArgb(rgb.R, rgb.G, rgb.B));
        if (picker.ShowDialog(this) != DialogResult.OK || _demo) return;
        draft = draft.WithColor(0, new(picker.Color.R, picker.Color.G, picker.Color.B));
        await ApplyKeyboardDraftAsync(draft, saved);
    }

    private async Task EditManualColorsAsync()
    {
        if (CreateKeyboardDraft("Manual") is not { } draft) return;
        GcuKeyboardSavedProfile? saved = ReadSavedKeyboard(KeyboardSnapshot);
        using var editor = new KeyboardSettingsForm(draft, async () =>
        {
            if (!_demo && (_controller is not { } current ||
                !(await current.RefreshKeyboardAsync(_lifetime.Token)).Confirmed)) return null;
            saved = ReadSavedKeyboard(KeyboardSnapshot, force: true);
            return CreateKeyboardDraft("Manual");
        }, _demo);
        if (editor.ShowDialog(this) == DialogResult.OK) await ApplyKeyboardDraftAsync(editor.Draft, saved);
    }

    private void RenderKeyboard()
    {
        GcuSnapshot snapshot = KeyboardSnapshot;
        bool fresh = snapshot.IsKeyboardFresh();
        bool available = !_startupTest && !_keyboardBusy && !_connecting && !_switching &&
            (_demo || _machine.SupportsPeripheralControl && _machineBlockReason is null);
        _fnLock.Enabled = available && !_demo && snapshot.IsFnLockFresh();
        _fnLock.Activated = snapshot.IsFnLockFresh() && snapshot.FnLocked == true;
        _fnLock.BackColor = _fnLock.Activated ? colorStandard : buttonSecond;
        _fnLock.ForeColor = _fnLock.Activated ? SystemColors.ControlLightLight : SystemColors.ControlDark;
        _fnLock.AccessibleName = "功能键锁定：" + (!snapshot.IsFnLockFresh() ? "未收到" : snapshot.FnLocked == true ? "开启" : "关闭");
        GcuQuickSettingState winLock = snapshot.QuickSetting(GcuQuickSetting.WinLock);
        RenderKeyboardToggle(_winLock, "系统键锁定", snapshot.IsConnected && winLock.IsFresh(), winLock.Enabled, available, colorStandard);
        bool powerFresh = fresh && snapshot.Keyboard is { SingleZone: true, Power: not null };
        RenderKeyboardToggle(_backlightButton, "键盘背光", powerFresh, snapshot.Keyboard?.Power, available, colorEco);
        _keyboardTips.SetToolTip(_backlightButton, _backlightButton.AccessibleName + "；开关保留亮度、灯效和颜色");
        _keyboardTips.SetToolTip(_winLock, _winLock.AccessibleName + "；开启后禁用系统键");
        GcuKeyboardSavedProfile? saved = ReadSavedKeyboard(snapshot);
        string? effect = snapshot.Keyboard?.Effect ?? saved?.Effect;
        GcuKeyboardDraft? draft = GcuKeyboardDraft.Create(snapshot.Keyboard, saved, "Single");
        _keyboardEffect.Enabled = available && fresh && draft?.IsValid == true;
        _keyboardMore.Enabled = !_startupTest;
        _keyboardColor.Enabled = available && fresh && draft?.IsValid == true && effect is "Single" or "Manual";
        _keyboardColor.Multicolor = fresh && effect is "Rainbow" or "Manual";
        _keyboardColor.AccessibleDescription = effect switch
        {
            "Rainbow" => "彩虹颜色由系统循环，无需选色",
            "Manual" => "编辑六个颜色与轮换间隔", "Single" => "选择恒亮颜色", _ => "等待完整灯效配置"
        };
        _keyboardTips.SetToolTip(_keyboardColor, _keyboardColor.AccessibleDescription);
        if (fresh && effect == "Single" && draft is not null)
        {
            GcuRgb rgb = draft.ColorAt(0);
            _keyboardColor.SwatchColor = Color.FromArgb(rgb.R, rgb.G, rgb.B);
        }
        else _keyboardColor.SwatchColor = null;
        string backlightStatus = !fresh ? "背光：等待状态" : snapshot.Keyboard?.Power switch
        {
            false => "背光：已关闭", true => "背光：已开启", _ => "背光：开关未回报"
        };
        if (fresh && snapshot.Keyboard?.Effect is null && saved is not null)
            backlightStatus += " · 灯效来自厂商保存配置";
        _keyboardTips.SetToolTip(_keyboardEffect, backlightStatus);
        _keyboardTips.SetToolTip(_keyboardMore, "更多设置 · " + backlightStatus);
        _overlayButton.Enabled = !_startupTest;
        _overlayButton.Activated = !_demo && AppConfig.IsOverlay();
        _overlayButton.BackColor = _overlayButton.Activated ? colorEco : buttonSecond;
        _overlayButton.ForeColor = _overlayButton.Activated ? SystemColors.ControlLightLight : SystemColors.ControlDark;
        _overlayButton.AccessibleName = "屏幕叠加：" + (_overlayButton.Activated ? "开启" : "关闭");
        _updatingKeyboard = true;
        _keyboardEffect.SelectedIndex = !fresh ? -1 : effect switch { "Single" => 0, "Rainbow" => 1, "Manual" => 2, _ => -1 };
        _updatingKeyboard = false;
        _settingsForm?.RefreshState();
    }

    private void RenderKeyboardToggle(RButton button, string title, bool fresh, bool? enabled, bool available, Color color)
    {
        button.Enabled = available && !_demo && fresh;
        button.Activated = fresh && enabled == true;
        button.BackColor = button.Activated ? color : buttonSecond;
        button.ForeColor = button.Activated ? SystemColors.ControlLightLight : SystemColors.ControlDark;
        button.AccessibleName = title + "：" + (!fresh ? "未收到" : enabled == true ? "开启" : "关闭");
    }

    private async Task ToggleQuickSettingAsync(GcuQuickSetting setting)
    {
        if (_demo || _startupTest || _closing || _connecting || _switching || _keyboardBusy || _controller is null) return;
        GcuQuickSettingState state = _controller.Snapshot.QuickSetting(setting);
        if (!_controller.Snapshot.IsConnected || !state.IsFresh() || state.Enabled is not bool enabled) return;
        await RunKeyboardAsync(() => _controller.SetQuickSettingAsync(setting, !enabled, _lifetime.Token));
    }

    private async Task ToggleBacklightPowerAsync()
    {
        if (!_backlightButton.Enabled || _demo || _keyboardBusy || _controller is not { } controller ||
            KeyboardSnapshot.Keyboard?.Power is not bool on) return;
        await RunKeyboardAsync(() => controller.SetKeyboardPowerAsync(!on, _lifetime.Token));
    }

    private async Task RunKeyboardAsync(Func<Task<GcuWriteResult>> operation)
    {
        if (_demo || _startupTest || _closing || _keyboardBusy) return;
        RefreshMachineBlockReason();
        if (!_machine.SupportsPeripheralControl)
        {
            ShowActionStatus("此协议尚未适配键盘与快捷设置，当前仅查看。");
            return;
        }
        if (_machineBlockReason is not null)
        {
            ShowActionStatus(_machineBlockReason);
            RenderSnapshot();
            return;
        }
        _keyboardBusy = true;
        ShowActionStatus("正在应用设置…");
        RenderSnapshot();
        try { ShowActionStatus((await operation()).Message); }
        catch (OperationCanceledException) when (_closing) { }
        catch { if (!_closing) ShowActionStatus("设置未完成，请检查厂商服务。"); }
        finally { _keyboardBusy = false; if (!_closing) RenderSnapshot(); }
    }

    private void OpenMoreSettings(bool toggle = false)
    {
        if (_settingsForm is not { IsDisposed: false })
            _settingsForm = new MoreSettingsForm(this);
        if (toggle && _settingsForm.Visible)
        {
            _settingsForm.Hide();
            return;
        }
        _settingsForm.RefreshState();
        if (!_settingsForm.Visible) _settingsForm.Show(this);
        _settingsForm.Activate();
    }

    private HardwareOverlay EnsureOverlay() => _hardwareOverlay ??= new HardwareOverlay(this, ReadOverlay);

    private OverlayReadings ReadOverlay()
    {
        GcuSnapshot snapshot = DisplaySnapshot ?? new();
        if (!snapshot.IsConnected) snapshot = new();
        EcFanSnapshot? ec = _nativeReader is null ? _ecSnapshot : null;
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
        if (_overlayConfig is { IsDisposed: false })
        {
            if (_overlayConfig.Visible) _overlayConfig.Close();
            else { _overlayConfig.Show(this); _overlayConfig.Activate(); }
            return;
        }
        _overlayConfig = new OverlayConfig(ToggleOverlay, ToggleOverlayGameOnly, EnsureOverlay(), this,
            limitedTelemetry: true, readOnly: _demo);
        _overlayConfig.Icon = MechrevoUi.AppIcon;
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
        HandleTrayMessage(message.Msg);
        if (message.Msg == 0x0312 && message.WParam.ToInt32() == OverlayHotkeyId) ToggleOverlay();
        base.WndProc(ref message);
    }

    private void CloseKeyboardAndOverlay()
    {
        if (_overlayHotkey) UnregisterHotKey(Handle, OverlayHotkeyId);
        _overlayConfig?.Close();
        _settingsForm?.Dispose();
        _keyboardTips.Dispose();
        _hardwareOverlay?.Close();
    }
}
