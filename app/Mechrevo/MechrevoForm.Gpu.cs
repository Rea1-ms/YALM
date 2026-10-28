using GHelper.UI;

namespace YALM.Mechrevo;

internal sealed partial class MechrevoForm
{
    private readonly Dictionary<GpuDisplayRoute, RButton> _gpuModeButtons = new();
    private readonly Label _gpuModeTitle = TextLabel("显卡模式：未确认");
    private readonly Label _gpuModeStatus = TextLabel(string.Empty);
    private GpuRouteSnapshot? _gpuRoute;
    private bool _gpuRouteRefreshing;

    private void BuildGpuLayout(TableLayoutPanel layout)
    {
        _gpuModeTitle.Font = new Font(Font, FontStyle.Bold);
        AddRow(layout, Heading(_gpuModeTitle, _gpuValues, GHelper.Properties.Resources.icons8_video_card_32));
        var modes = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 3, 0, 0)
        };
        var definitions = new[]
        {
            (GpuDisplayRoute.Hybrid, "混合模式", colorStandard, GHelper.Properties.Resources.icons8_spa_flower_48),
            (GpuDisplayRoute.Direct, "独显直连", colorTurbo, GHelper.Properties.Resources.icons8_game_controller_48)
        };
        foreach (var (route, name, color, image) in definitions)
        {
            var button = Button(name, color);
            button.Image = image;
            button.ImageAlign = ContentAlignment.BottomCenter;
            button.TextImageRelation = TextImageRelation.ImageAboveText;
            button.Height = 76;
            button.Click += async (_, _) => await SwitchGpuModeAsync(route);
            modes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            modes.Controls.Add(button, modes.Controls.Count, 0);
            _gpuModeButtons.Add(route, button);
            _keyboardTips.SetToolTip(button, "保存重启后的显示目标；需自行重启，才会改变实际路由");
        }
        AddRow(layout, modes);
        _gpuModeStatus.ForeColor = SystemColors.GrayText;
        _gpuModeStatus.Margin = new Padding(2, 3, 2, 16);
        AddRow(layout, _gpuModeStatus);
    }

    private async Task RefreshGpuRouteAsync()
    {
        if (_demo || _closing || _gpuRouteRefreshing) return;
        _gpuRouteRefreshing = true;
        try
        {
            GpuRouteSnapshot route = await Task.Run(GpuRouteReader.Read, _lifetime.Token);
            if (!_closing) _gpuRoute = route;
        }
        catch (OperationCanceledException) when (_closing) { }
        catch
        {
            if (!_closing) _gpuRoute = new() { LastError = "显示路由读取失败。", UpdatedAt = DateTimeOffset.UtcNow };
        }
        finally
        {
            _gpuRouteRefreshing = false;
            if (!_closing) RenderSnapshot();
        }
    }

    private void RenderGpuMode()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GcuSnapshot? snapshot = _controller?.Snapshot;
        bool routeFresh = _demo || _gpuRoute?.IsFresh(now) == true;
        GpuDisplayRoute actual = _demo ? GpuDisplayRoute.Direct
            : routeFresh ? _gpuRoute!.Route : GpuDisplayRoute.Unknown;
        bool targetFresh = snapshot?.IsGpuTargetFresh(now) == true;
        bool supported = snapshot?.GpuSwitchSupported == true && snapshot.IsGpuSwitchSupportFresh(now);
        GpuDisplayRoute? target = _demo ? GpuDisplayRoute.Direct : targetFresh ? snapshot!.GpuTarget : null;
        bool available = !_demo && !_startupTest && !_closing && !_connecting && !_switching && !_keyboardBusy &&
            _machineBlockReason is null && _machine.SupportsPeripheralControl && supported && targetFresh && routeFresh &&
            actual is GpuDisplayRoute.Hybrid or GpuDisplayRoute.Direct;
        foreach (var (route, button) in _gpuModeButtons)
        {
            button.Activated = actual == route;
            button.Enabled = available;
            button.AccessibleName = GpuModeName(route) + (actual == route ? "，当前模式" : "") +
                (target == route && actual != route ? "，待重启" : "");
        }

        _gpuModeTitle.Text = "显卡模式：" + GpuModeName(actual);
        _gpuModeStatus.Text = _demo ? "切换需重启"
            : !routeFresh ? "正在等待内屏路由，切换需重启"
            : snapshot?.IsConnected != true ? "连接后可读取切换目标，切换需重启"
            : !supported ? "显示切换能力未确认"
            : !targetFresh ? "重启后目标未确认"
            : target != actual ? "重启后：" + GpuModeName(target) + "（待重启）"
            : "切换需重启";
    }

    private async Task SwitchGpuModeAsync(GpuDisplayRoute target)
    {
        if (_demo || _startupTest || _closing || _connecting || _switching || _keyboardBusy || _controller is null) return;
        _machineBlockReason = CurrentControlBlockReason();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!_machine.SupportsPeripheralControl || _machineBlockReason is not null ||
            _gpuRoute?.IsFresh(now) != true || _gpuRoute.Route is not (GpuDisplayRoute.Hybrid or GpuDisplayRoute.Direct) ||
            !_controller.Snapshot.CanSetGpuTarget(target, now))
        {
            ShowActionStatus(_machineBlockReason ?? "显示模式尚未确认或状态已过期，暂时不能切换。");
            RenderSnapshot();
            return;
        }
        if (_controller.Snapshot.GpuTarget == target) return;
        _switching = true;
        ShowActionStatus("正在保存重启后的显示目标…");
        RenderSnapshot();
        try
        {
            GcuWriteResult result = await _controller.SetGpuTargetAsync(target, _lifetime.Token);
            await RefreshGpuRouteAsync();
            if (!_closing)
                ShowActionStatus(!result.Confirmed ? SafeMessage(result.Message)
                    : _gpuRoute?.IsFresh() == true && _gpuRoute.Route == target
                        ? "已恢复为当前模式：" + GpuModeName(target) + "。"
                        : "已保存：" + GpuModeName(target) + "。请保存工作后自行重启。");
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            if (!_closing) ShowActionStatus("显示目标未确认：" + SafeMessage(ex.Message));
        }
        finally
        {
            _switching = false;
            if (!_closing) RenderSnapshot();
        }
    }

    private static string GpuModeName(GpuDisplayRoute? route) => route switch
    {
        GpuDisplayRoute.Hybrid => "混合模式", GpuDisplayRoute.Direct => "独显直连",
        GpuDisplayRoute.IgpuOnly => "核显输出", _ => "未确认"
    };
}
