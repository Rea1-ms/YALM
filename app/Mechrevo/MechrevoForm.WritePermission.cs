namespace YALM.Mechrevo;

internal sealed partial class MechrevoForm
{
    private bool HasWritePermission => _demo || _machine.HasVerifiedBoard || _controller?.WritePermissionGranted == true;

    private bool CanToggleWritePermission =>
        !_demo && _nativeReader is null && !_machine.HasVerifiedBoard &&
        _controller is { Snapshot.IsConnected: true } controller && controller.WritePermissionAvailable;

    private string WritePermissionTitle
    {
        get
        {
            if (_demo) return "允许写入（演示不可用）";
            if (_nativeReader is not null) return "允许写入（只读接口）";
            if (_machine.HasVerifiedBoard) return "允许写入（已验证机型）";
            if (_controller is not { } controller || controller.Snapshot.IsConnected != true)
                return "允许写入（连接后可用）";
            if (!controller.WritePermissionAvailable) return "允许写入（协议未实现）";
            return "允许写入（当前连接）";
        }
    }

    private string WritePermissionDescription
    {
        get
        {
            if (_demo) return "演示模式不连接本机服务。";
            if (_nativeReader is not null) return "当前接口只提供状态读取，尚未开放写入。";
            if (_machine.HasVerifiedBoard) return "已验证机型沿用现有控制，开关由系统自动保持。";
            if (_controller is not { } controller || controller.Snapshot.IsConnected != true)
                return "未连接本机服务。";
            if (!controller.WritePermissionAvailable) return "当前协议只支持读取，尚未实现写入。";
            return "未实机验证机型默认只读；确认后仅对本次连接开放已核对的写入。";
        }
    }

    private bool WritePermissionGranted => _controller?.WritePermissionGranted == true;

    private string? CurrentMachineBlockReason()
    {
        if (_nativeReader is not null) return "此接口已接入状态读取，控制功能尚未开放。";
        if (_armouryConflict?.IsConflict == true)
            return "检测到奥创控制服务正在运行，可能与 YALM 冲突；请关闭奥创后重试。";
        string? reason = _machine.GetModeControlBlockReason();
        if (reason is not null) return reason;
        if (_controller?.Snapshot.IsConnected == true && !HasWritePermission)
            return "此机型未实机验证，当前默认只读；请在“更多设置”中确认允许写入。";
        return null;
    }

    private string? CurrentCustomControlBlockReason()
    {
        if (_armouryConflict?.IsConflict == true)
            return "检测到奥创控制服务正在运行，可能与 YALM 冲突；请关闭奥创后重试。";
        string? reason = _machine.GetCustomControlBlockReason();
        if (reason is not null) return reason;
        if (_controller?.Snapshot.IsConnected == true && !HasWritePermission)
            return "当前连接未允许写入，请在“更多设置”中确认允许写入。";
        return null;
    }

    private void RefreshMachineBlockReason() => _machineBlockReason = _demo ? null : CurrentMachineBlockReason();

    private void ToggleWritePermission()
    {
        if (_demo || _nativeReader is not null || _controller is not { } controller ||
            controller.Snapshot.IsConnected != true || !controller.WritePermissionAvailable) return;

        if (controller.WritePermissionGranted)
        {
            controller.SetWritePermission(false);
            RefreshMachineBlockReason();
            RenderSnapshot();
            return;
        }

        DialogResult result = MessageBox.Show(this,
            "当前机型尚未完成实机验证。允许写入可能导致风扇、性能模式或其他设置异常。\n\n" +
            "这里只开放已经核对协议的功能，原有保护、状态新鲜度和供电检查仍会保留。\n" +
            "本次确认仅对当前连接有效，开启本身不会发送硬件设置。\n\n" +
            "确定要允许写入吗？",
            "允许写入",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (result != DialogResult.OK) return;
        if (!controller.SetWritePermission(true))
        {
            ShowActionStatus("当前连接未开放可核对的写入协议。");
            return;
        }
        RefreshMachineBlockReason();
        RenderSnapshot();
    }
}
