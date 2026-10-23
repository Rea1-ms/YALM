using Microsoft.Win32;
using System.Diagnostics;

namespace YALM.Mechrevo;

internal sealed record MachineInfo(string Manufacturer, string Model, string BiosVersion, string BaseboardProduct)
{
    public bool? TurboSupported { get; init; }

    public bool SupportsModeControl =>
        string.Equals(Manufacturer.Trim(), "MECHREVO", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(BaseboardProduct.Trim(), "GM6PX0X", StringComparison.OrdinalIgnoreCase) ||
         Model.Contains("GM6PX0X", StringComparison.OrdinalIgnoreCase));

    public string DisplayName => string.Join(" ", new[] { Manufacturer, Model }
        .Where(value => !string.IsNullOrWhiteSpace(value)));

    public static bool? ReadAcPower() => SystemInformation.PowerStatus.PowerLineStatus switch
    {
        PowerLineStatus.Online => true,
        PowerLineStatus.Offline => false,
        _ => null
    };

    public static MachineInfo Read()
    {
        try
        {
            using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS", writable: false);
            return new MachineInfo(
                bios?.GetValue("SystemManufacturer")?.ToString() ?? string.Empty,
                bios?.GetValue("SystemProductName")?.ToString() ?? string.Empty,
                bios?.GetValue("BIOSVersion")?.ToString() ?? string.Empty,
                bios?.GetValue("BaseBoardProduct")?.ToString() ?? string.Empty)
            {
                TurboSupported = ReadTurboCapability()
            };
        }
        catch
        {
            return new MachineInfo(string.Empty, string.Empty, string.Empty, string.Empty);
        }
    }

    private static bool? ReadTurboCapability()
    {
        string[] paths =
        [
            @"SOFTWARE\OEM\GamingCenter2\ItemSupport",
            @"SOFTWARE\OEM\GamingCenter\ItemSupport",
            @"SOFTWARE\OEM\ControlCenter\ItemSupport"
        ];
        foreach (string path in paths)
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var settings = machine.OpenSubKey(path, writable: false);
                    object? value = settings?.GetValue("TurboModeSupport");
                    if (value is bool flag) return flag;
                    if (value is int number && number is 0 or 1) return number == 1;
                    if (value is long wideNumber && wideNumber is 0 or 1) return wideNumber == 1;
                    if (value is string text)
                    {
                        if (text == "0") return false;
                        if (text == "1") return true;
                        if (bool.TryParse(text, out bool parsed)) return parsed;
                    }
                }
                catch { /* Missing or inaccessible capability data stays unknown. */ }
            }
        }
        return null;
    }

    public string? GetModeControlBlockReason()
    {
        if (!SupportsModeControl)
            return "此机型尚未验证，当前仅查看状态。";

        try
        {
            var running = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var unableToInspect = false;
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        var name = process.ProcessName;
                        if (IsCompetingControlApp(name)) running.Add(name);
                    }
                    catch (InvalidOperationException)
                    {
                        // A process that exited during enumeration cannot compete for control.
                    }
                    catch
                    {
                        unableToInspect = true;
                    }
                }
            }

            if (running.Count > 0)
                return $"检测到其他控制界面：{string.Join("、", running)}。请先退出该界面再切换模式。";
            if (unableToInspect)
                return "暂时无法确认其他控制界面的运行状态，已暂停模式切换。";
            return null;
        }
        catch
        {
            return "无法检查其他控制界面，当前仅查看状态。";
        }
    }

    private static bool IsCompetingControlApp(string name) =>
        name.Equals("L-Mechrevo", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("LMechrevo", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("ControlCenter", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("GamingCenter", StringComparison.OrdinalIgnoreCase) ||
        // The original UWP app leaves this full-trust tray process running.
        name.Equals("SystrayComponent", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("RevoHelper", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("MechrevoControl", StringComparison.OrdinalIgnoreCase);
}
