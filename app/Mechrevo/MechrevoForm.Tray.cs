using GHelper.UI;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using Resources = GHelper.Properties.Resources;

namespace YALM.Mechrevo;

internal sealed partial class MechrevoForm
{
    private NotifyIcon? _trayIcon;
    private CustomContextMenu? _trayMenu;
    private readonly Dictionary<PerformanceMode, ToolStripMenuItem> _trayModes = new();
    private readonly Dictionary<GpuDisplayRoute, ToolStripMenuItem> _trayGpuModes = new();
    private ToolStripMenuItem? _trayCustomMode, _trayFanBoost, _trayCustomSettings, _trayBw, _trayOverlay, _trayGameOnly;
    private readonly System.Windows.Forms.Timer _trayTimer = new() { Interval = 500 };
    private EventWaitHandle? _activationSignal;
    private DateTimeOffset? _trayRetryAt;
    private bool _exitRequested;
    private bool _trayStarted;
    private bool _trayDisposed;
    private bool _bwTrayIcon = AppConfig.IsBWIcon();
    private Icon? _ownedTrayIcon;
    private (GpuDisplayRoute Route, bool Monochrome, bool Dark)? _trayIconKey;
    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    private void InitializeTray()
    {
        _trayMenu = new CustomContextMenu
        {
            ShowCheckMargin = true, ShowImageMargin = false,
            ImageScalingSize = new Size(16, 16), Renderer = new CustomMenuRenderer(), Font = Font
        };
        ToolStripMenuItem Add(string text, Action? action = null)
        {
            var item = new ToolStripMenuItem(text) { Margin = new Padding(5) };
            if (action is not null) item.Click += (_, _) => action();
            else item.Enabled = false;
            _trayMenu.Items.Add(item);
            return item;
        }

        Add("打开主窗口", ShowMainWindow);
        _trayMenu.Items.Add(new ToolStripSeparator());
        Add("性能模式");
        foreach (PerformanceMode mode in Enum.GetValues<PerformanceMode>())
            _trayModes[mode] = Add(ModeName((int)mode), async () => await RunTrayActionAsync(() => SwitchModeAsync(mode)));
        _trayCustomMode = Add("自定义");
        _trayFanBoost = Add("风扇增强（仅自定义模式）", async () => await RunTrayActionAsync(ToggleTrayFanBoostAsync));
        _trayMenu.Items.Add(new ToolStripSeparator());
        Add("显卡模式（切换需重启）");
        foreach (GpuDisplayRoute route in new[] { GpuDisplayRoute.Hybrid, GpuDisplayRoute.Direct })
            _trayGpuModes[route] = Add(GpuModeName(route), async () => await RunTrayActionAsync(() => SwitchGpuModeAsync(route)));
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayCustomSettings = Add("风扇与功耗…", () => { ShowMainWindow(); OpenCustomMode(); });
        Add("更多设置…", () => { ShowMainWindow(); OpenMoreSettings(); });
        _trayBw = Add("黑白托盘图标", () =>
        {
            _bwTrayIcon = !_bwTrayIcon;
            if (!_demo) AppConfig.Set("bw_icon", _bwTrayIcon ? 1 : 0);
            RenderTray();
        });
        _trayOverlay = Add("屏幕叠加", ToggleOverlay);
        _trayGameOnly = Add("仅在游戏中显示叠加", ToggleOverlayGameOnly);
        _trayMenu.Items.Add(new ToolStripSeparator());
        Add("退出", ExitApplication);
        _trayMenu.Opening += (_, e) =>
        {
            if (_closing) { e.Cancel = true; return; }
            RefreshTrayTheme();
            RenderSnapshot();
        };
        _ownedTrayIcon = Resources.yalm;
        _trayIcon = new NotifyIcon { Text = "YALM · 机械革命助手", Icon = _ownedTrayIcon, ContextMenuStrip = _trayMenu };
        _trayIcon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleMainWindow(); };
        _trayIcon.BalloonTipClicked += (_, _) => ShowMainWindow();
        _trayTimer.Tick += (_, _) => CheckTraySignals();
    }

    private void StartTray()
    {
        // Constructing a form for offline rendering must never register a tray icon.
        if (_smokeTest || _startupTest || _closing || _trayStarted) return;
        _trayStarted = true;
        RenderTray();
        RegisterTrayIcon();
        // Match upstream's delayed registration for a shell still starting at login.
        _trayRetryAt = DateTimeOffset.UtcNow.AddSeconds(5);
        _trayTimer.Start();
    }

    internal void SetActivationSignal(EventWaitHandle signal) => _activationSignal = signal;

    private void CheckTraySignals()
    {
        if (_closing || _trayDisposed) return;
        if (_activationSignal?.WaitOne(0) == true) ShowMainWindow();
        if (_trayRetryAt is { } retryAt && DateTimeOffset.UtcNow >= retryAt)
        {
            _trayRetryAt = null;
            RegisterTrayIcon();
        }
    }

    private void RegisterTrayIcon()
    {
        if (!_trayStarted || _closing || _trayIcon is null || _trayDisposed) return;
        _trayIcon.Visible = false;
        _trayIcon.Visible = true;
    }

    private void HandleTrayMessage(int message)
    {
        if (_trayIcon is null || _closing || _trayDisposed) return;
        if (TaskbarCreated != 0 && message == TaskbarCreated) RegisterTrayIcon();
        else if (message is 0x001A or 0x031A) RefreshTrayTheme(); // Settings / theme changed.
    }

    private void RefreshTrayTheme()
    {
        InitTheme();
        foreach (RForm child in OwnedForms.OfType<RForm>()) child.InitTheme();
        RenderTray();
    }

    private static bool IsTaskbarDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int light ? light == 0 : CheckSystemDarkModeStatus();
    }

    private static Icon TrayIconFor(GpuDisplayRoute actual, bool monochrome, bool dark) => monochrome
        ? dark ? Resources.light_standard : Resources.dark_standard
        : actual == GpuDisplayRoute.Direct ? Resources.ultimate
        : actual == GpuDisplayRoute.Hybrid ? Resources.standard : Resources.yalm;

    private void RenderTray()
    {
        if (_trayIcon is null || _trayMenu is null || _trayDisposed || _closing) return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GcuSnapshot? snapshot = _controller?.Snapshot;
        GpuDisplayRoute actual = _demo ? GpuDisplayRoute.Direct
            : _gpuRoute?.IsFresh(now) == true ? _gpuRoute.Route : GpuDisplayRoute.Unknown;
        var iconKey = (Route: actual, Monochrome: _bwTrayIcon, Dark: IsTaskbarDark());
        if (_trayIconKey != iconKey)
        {
            Icon? oldIcon = _ownedTrayIcon;
            _ownedTrayIcon = TrayIconFor(iconKey.Route, iconKey.Monochrome, iconKey.Dark);
            _trayIcon.Icon = _ownedTrayIcon;
            _trayIconKey = iconKey;
            oldIcon?.Dispose();
        }
        string performance = _demo ? "均衡" : snapshot?.IsConnected == true && snapshot.IsStatusFresh(now)
            ? ModeName(snapshot.Mode) : "未收到";
        _trayIcon.Text = $"YALM{(_demo ? " · 演示" : "")} · {performance} · {GpuModeName(actual)}";
        _trayMenu.BackColor = BackColor;
        _trayMenu.ForeColor = ForeColor;
        foreach (var (mode, item) in _trayModes)
        {
            item.Checked = _modeButtons[mode].Activated;
            item.Enabled = _modeButtons[mode].Enabled;
        }
        _trayCustomMode!.Checked = snapshot?.IsConnected == true && snapshot.IsStatusFresh(now) && snapshot.Mode == 3;
        _trayFanBoost!.Checked = snapshot?.IsConnected == true && snapshot.IsStatusFresh(now) && snapshot.Mode == 3 && snapshot.IsCustomSettingsFresh(now) &&
            snapshot.CustomSettings?.FanBoostEnabled == true;
        _trayFanBoost.Enabled = CanToggleTrayFanBoost(now);
        foreach (var (route, item) in _trayGpuModes)
        {
            item.Checked = _gpuModeButtons[route].Activated;
            item.Enabled = _gpuModeButtons[route].Enabled;
            bool pending = actual is GpuDisplayRoute.Hybrid or GpuDisplayRoute.Direct &&
                snapshot?.IsGpuTargetFresh(now) == true && snapshot.GpuTarget == route && route != actual;
            item.Text = GpuModeName(route) + (pending ? "（待重启）" : "");
        }
        _trayCustomSettings!.Enabled = _openCustomMode.Enabled;
        _trayBw!.Checked = _bwTrayIcon;
        _trayOverlay!.Checked = !_demo && AppConfig.IsOverlay();
        _trayOverlay.Enabled = !_demo;
        _trayGameOnly!.Checked = !_demo && AppConfig.IsOverlayGameOnly();
        _trayGameOnly.Enabled = _trayOverlay.Enabled && _trayOverlay.Checked;
    }

    private bool CanToggleTrayFanBoost(DateTimeOffset now)
    {
        GcuSnapshot? snapshot = _controller?.Snapshot;
        return !_demo && !_startupTest && !_closing && !_connecting && !_switching && !_keyboardBusy &&
            _customModeForm?.IsOperationPending != true && _machineBlockReason is null &&
            _machine.SupportsCustomControl && HasFreshActualRpm(now) &&
            (_ecSnapshot is null || _ecSnapshot.IsFresh(now) && _ecSnapshot.GenericThermalProtect != true) &&
            snapshot?.IsConnected == true && snapshot.IsStatusFresh(now) && snapshot.Mode == 3 &&
            snapshot.IsCustomSettingsFresh(now) && snapshot.IsFanTableFresh(now) &&
            snapshot.CustomSettings?.FanBoostEnabled is not null;
    }

    private async Task ToggleTrayFanBoostAsync()
    {
        _machineBlockReason = CurrentControlBlockReason();
        if (!CanToggleTrayFanBoost(DateTimeOffset.UtcNow))
        {
            ShowActionStatus("请先进入自定义模式，并等待完整、新鲜的状态；暂时不能切换风扇增强。");
            return;
        }
        bool enabled = _controller!.Snapshot.CustomSettings!.FanBoostEnabled != true;
        _switching = true;
        ShowActionStatus("等待服务回报风扇增强状态…");
        RenderSnapshot();
        try { ShowActionStatus(SafeMessage((await _controller.SetFanBoostAsync(enabled, _lifetime.Token)).Message)); }
        catch (OperationCanceledException) when (_closing) { }
        catch { if (!_closing) ShowActionStatus("风扇增强未确认，请重新读取后检查。"); }
        finally { _switching = false; if (!_closing) RenderSnapshot(); }
    }

    private async Task RunTrayActionAsync(Func<Task> action)
    {
        DateTimeOffset previous = _actionMessageUntil;
        await action();
        if (!_closing && !Visible && previous != _actionMessageUntil && _trayIcon?.Visible == true)
            _trayIcon.ShowBalloonTip(5000, "YALM", _actionMessage, ToolTipIcon.Info);
    }

    private void ToggleMainWindow()
    {
        if (_closing) return;
        if (Visible && WindowState == FormWindowState.Normal) HideToTray();
        else ShowMainWindow();
    }

    private void HideToTray()
    {
        if (_trayIcon?.Visible != true || _closing || OwnedForms.Any(form => form.Modal && form.Visible)) return;
        _trayMenu?.Close();
        _customModeForm?.Hide();
        _settingsForm?.Hide();
        _overlayConfig?.Hide();
        Hide();
    }

    private void ShowMainWindow()
    {
        if (_closing || IsDisposed) return;
        WindowState = FormWindowState.Normal;
        PositionAtScreenCorner(Screen.FromPoint(Cursor.Position));
        if (!Visible) Show();
        Activate();
        foreach (Form dialog in OwnedForms.Where(form => form.Modal && form.Visible)) dialog.Activate();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    private void DisposeTray()
    {
        if (_trayDisposed) return;
        _trayDisposed = true;
        _trayTimer.Stop();
        _trayTimer.Dispose();
        if (_trayIcon is not null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }
        _ownedTrayIcon?.Dispose();
        _trayMenu?.Dispose();
        _activationSignal = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeTray();
            _refreshTimer.Dispose();
            _ecRefreshTimer.Dispose();
            _smokeTimer.Dispose();
            _lifetime.Dispose();
            _keyboardTips.Dispose();
        }
        base.Dispose(disposing);
    }
}
