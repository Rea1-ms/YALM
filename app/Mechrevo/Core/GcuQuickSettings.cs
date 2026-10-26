namespace YALM.Mechrevo;

public enum GcuQuickSetting
{
    WinLock,
    OsdVisible,
    NumPadLock,
    TouchpadHotkey,
    UsbCharging,
    AcRecovery
}

public readonly record struct GcuQuickSettingState(bool? Enabled, DateTimeOffset? UpdatedAt)
{
    public bool IsFresh(DateTimeOffset? now = null)
    {
        if (!Enabled.HasValue || !UpdatedAt.HasValue) return false;
        TimeSpan age = (now ?? DateTimeOffset.UtcNow) - UpdatedAt.Value;
        return age >= TimeSpan.Zero && age <= GcuSnapshot.Freshness;
    }
}

internal sealed record GcuQuickSettingDefinition(
    GcuQuickSetting Setting, string Title, string Field, string EnableAction, string DisableAction,
    string[] EnabledReports, string[] DisabledReports)
{
    public bool? Parse(string? value) => value is null ? null
        : EnabledReports.Contains(value, StringComparer.Ordinal) ? true
        : DisabledReports.Contains(value, StringComparer.Ordinal) ? false : null;
}

internal static class GcuQuickSettings
{
    public static readonly GcuQuickSettingDefinition[] Definitions =
    [
        new(GcuQuickSetting.WinLock, "系统键锁定", "WinKey", "WINKEY_LOCK", "WINKEY_UNLOCK",
            ["WINKEY_LOCK", "WINKEY_STATUS_LOCK"], ["WINKEY_UNLOCK", "WINKEY_STATUS_UNLOCK"]),
        new(GcuQuickSetting.OsdVisible, "显示功能键提示", "OSD", "OSD_HIDDEN_OFF", "OSD_HIDDEN_ON",
            ["OSD_HIDDEN_OFF"], ["OSD_HIDDEN_ON"]),
        new(GcuQuickSetting.NumPadLock, "数字键盘锁定", "NumPad", "NUMPAD_LOCK", "NUMPAD_UNLOCK",
            ["NUMPAD_LOCK"], ["NUMPAD_UNLOCK"]),
        new(GcuQuickSetting.TouchpadHotkey, "启用触摸板切换键", "TouchpadToggle", "TOUCHPAD_TOGGLE_ON", "TOUCHPAD_TOGGLE_OFF",
            ["TOUCHPAD_TOGGLE_ON"], ["TOUCHPAD_TOGGLE_OFF"]),
        new(GcuQuickSetting.UsbCharging, "关机时 USB 供电", "UsbCharger", "USB_CHARGER_ON", "USB_CHARGER_OFF",
            ["USB_CHARGER_ON", "USB_CHARGER_STATUS_ON"], ["USB_CHARGER_OFF", "USB_CHARGER_STATUS_OFF"]),
        new(GcuQuickSetting.AcRecovery, "来电自动开机", "AcRecoverySwitch_Status", "ACRECOVERY_TOGGLE_ON", "ACRECOVERY_TOGGLE_OFF",
            ["ACRECOVERY_TOGGLE_ON"], ["ACRECOVERY_TOGGLE_OFF"])
    ];

    public static GcuQuickSettingDefinition? Find(GcuQuickSetting setting) =>
        Definitions.FirstOrDefault(definition => definition.Setting == setting);
}
