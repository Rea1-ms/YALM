using System;
using System.Globalization;
using System.Text.Json;

namespace YALM.Mechrevo;

internal static class GcuMessageParser
{
    public static GcuSnapshot Apply(GcuSnapshot snapshot, string topic, string payload,
        DateTimeOffset now, out bool modeReported)
    {
        return Apply(snapshot, topic, payload, now, out modeReported, out _, out _);
    }

    public static GcuSnapshot Apply(GcuSnapshot snapshot, string topic, string payload,
        DateTimeOffset now, out bool modeReported, out bool customSettingsReported, out bool fanTableReported)
    {
        modeReported = false;
        customSettingsReported = false;
        fanTableReported = false;
        if (payload.Length > 131072) return snapshot;
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return snapshot;
            switch (topic)
            {
                case "Fan/Status":
                    if (root.TryGetProperty("OperatingMode", out var modeElement))
                    {
                        int? mode = Integer(modeElement, 0, 3);
                        modeReported = mode.HasValue;
                        snapshot = snapshot with { Mode = mode, ModeUpdatedAt = mode.HasValue ? now : null };
                    }
                    if (root.TryGetProperty("IsAC", out var acElement))
                    {
                        bool? ac = Boolean(acElement);
                        snapshot = snapshot with { IsAc = ac, IsAcUpdatedAt = ac.HasValue ? now : null };
                    }
                    snapshot = ApplyTurboSupport(snapshot, root, now);
                    if (HasAnyCustomSetting(root))
                    {
                        GcuCustomSettings settings = ApplyCustomSettings(
                            snapshot.CustomSettings ?? new GcuCustomSettings(), root);
                        snapshot = snapshot with
                        {
                            CustomSettings = settings,
                            CustomSettingsUpdatedAt = now
                        };
                        customSettingsReported = true;
                    }
                    break;
                case "Setting/Status":
                    snapshot = ApplyTurboSupport(snapshot, root, now);
                    break;
                case "Fan/Table":
                    if (TryParseFanTable(root, out FanCurveTable? table))
                    {
                        snapshot = snapshot with { FanTable = table, FanTableUpdatedAt = now };
                        fanTableReported = true;
                    }
                    break;
                case "System/FanInfo":
                    if (root.TryGetProperty("CpuFanDuty", out var cpuDutyElement))
                    {
                        int? duty = Integer(cpuDutyElement, 0, 100);
                        snapshot = snapshot with { CpuFanDuty = duty, CpuFanDutyUpdatedAt = duty.HasValue ? now : null };
                    }
                    if (root.TryGetProperty("GpuFanDuty", out var gpuDutyElement))
                    {
                        int? duty = Integer(gpuDutyElement, 0, 100);
                        snapshot = snapshot with { GpuFanDuty = duty, GpuFanDutyUpdatedAt = duty.HasValue ? now : null };
                    }
                    if (root.TryGetProperty("CpuFanRpm", out var cpuFanElement))
                    {
                        int? rpm = Integer(cpuFanElement, 0, 20000);
                        snapshot = snapshot with { CpuFanRpm = rpm, CpuFanUpdatedAt = rpm.HasValue ? now : null };
                    }
                    if (root.TryGetProperty("GpuFanRpm", out var gpuFanElement))
                    {
                        int? rpm = Integer(gpuFanElement, 0, 20000);
                        snapshot = snapshot with { GpuFanRpm = rpm, GpuFanUpdatedAt = rpm.HasValue ? now : null };
                    }
                    break;
                case "System/CpuInfo":
                    if (root.TryGetProperty("CpuTemperature", out var cpuElement))
                    {
                        int? temperature = Integer(cpuElement, 1, 125);
                        snapshot = snapshot with { CpuTemperature = temperature, CpuTemperatureUpdatedAt = temperature.HasValue ? now : null };
                    }
                    break;
                case "System/GpuInfo":
                    if (root.TryGetProperty("GpuTemperature", out var gpuElement))
                    {
                        int? temperature = Integer(gpuElement, 1, 125);
                        snapshot = snapshot with { GpuTemperature = temperature, GpuTemperatureUpdatedAt = temperature.HasValue ? now : null };
                    }
                    break;
            }
        }
        catch (JsonException) { }
        return snapshot;
    }

    private static bool HasAnyCustomSetting(JsonElement root)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name is "CustomProfileIndex" or "ProfileName" or "FAN_TableName" or
                "CPU_PL1" or "CPU_PL1Minimum" or "CPU_PL1Maximum" or
                "CPU_PL2" or "CPU_PL2Minimum" or "CPU_PL2Maximum" or
                "CPU_PL4" or "CPU_PL4Minimum" or "CPU_PL4Maximum" or "CPU_PL4_Double_Flag" or
                "CPU_TccOffset" or "CPU_TjMax" or "CPU_TccOffsetSwitch" or
                "GPU_ConfigurableTGPTarget" or "GPU_ConfigurableTGPMinimum" or "GPU_ConfigurableTGPMaximum" or
                "GPU_ConfigurableTGPSwitch" or "GPU_DynamicBoost" or "GPU_DynamicBoostMinimum" or
                "GPU_DynamicBoostMaximum" or "GPU_DynamicBoostSwitch" or
                "GPU_CoreClockOffsetOC" or "GPU_CoreClockOffsetMinimum" or "GPU_CoreClockOffsetMaximum" or
                "GPU_MemoryClockOffsetOC" or "GPU_MemoryClockOffsetMinimum" or "GPU_MemoryClockOffsetMaximum" or
                "FAN_FanSwitchSpeed" or "FAN_FanSwitchSpeedEnabled" or "FanBoostEnable" or
                "FanBoostBtnSupport" or "OverClockingSwitch" or
                "MEM_MemoryOverClockSupport" or "MEM_MemoryOverClockSwitch")
                return true;
        }
        return false;
    }

    private static GcuCustomSettings ApplyCustomSettings(GcuCustomSettings settings, JsonElement root)
    {
        settings = ApplyInt(settings, root, "CustomProfileIndex", 0, 4, (value, item) => value with { ProfileIndex = item });
        settings = ApplyString(settings, root, "ProfileName", (value, item) => value with { ProfileName = item });
        settings = ApplyString(settings, root, "FAN_TableName", (value, item) => value with { TableName = item });

        settings = ApplyInt(settings, root, "CPU_PL1", 0, 1000, (value, item) => value with { Pl1 = item });
        settings = ApplyInt(settings, root, "CPU_PL1Minimum", 0, 1000, (value, item) => value with { Pl1Minimum = item });
        settings = ApplyInt(settings, root, "CPU_PL1Maximum", 0, 1000, (value, item) => value with { Pl1Maximum = item });
        settings = ApplyInt(settings, root, "CPU_PL2", 0, 1000, (value, item) => value with { Pl2 = item });
        settings = ApplyInt(settings, root, "CPU_PL2Minimum", 0, 1000, (value, item) => value with { Pl2Minimum = item });
        settings = ApplyInt(settings, root, "CPU_PL2Maximum", 0, 1000, (value, item) => value with { Pl2Maximum = item });
        settings = ApplyInt(settings, root, "CPU_PL4", 0, 2000, (value, item) => value with { Pl4 = item });
        settings = ApplyInt(settings, root, "CPU_PL4Minimum", 0, 2000, (value, item) => value with { Pl4Minimum = item });
        settings = ApplyInt(settings, root, "CPU_PL4Maximum", 0, 2000, (value, item) => value with { Pl4Maximum = item });
        settings = ApplyBool(settings, root, "CPU_PL4_Double_Flag", (value, item) => value with { Pl4HalfWatt = item });

        settings = ApplyInt(settings, root, "CPU_TccOffset", 0, 30, (value, item) => value with { TccRawOffset = item });
        settings = ApplyInt(settings, root, "CPU_TjMax", 50, 125, (value, item) => value with { TjMax = item });
        settings = ApplyBool(settings, root, "CPU_TccOffsetSwitch", (value, item) => value with { TccEnabled = item });
        if (settings.TjMax is null &&
            (root.TryGetProperty("CPU_TccOffset", out _) || root.TryGetProperty("CPU_TccOffsetSwitch", out _)))
            settings = settings with { TjMax = 100 };

        settings = ApplyInt(settings, root, "GPU_ConfigurableTGPTarget", 0, 1000, (value, item) => value with { GpuTgp = item });
        settings = ApplyInt(settings, root, "GPU_ConfigurableTGPMinimum", 0, 1000, (value, item) => value with { GpuTgpMinimum = item });
        settings = ApplyInt(settings, root, "GPU_ConfigurableTGPMaximum", 0, 1000, (value, item) => value with { GpuTgpMaximum = item });
        settings = ApplyBool(settings, root, "GPU_ConfigurableTGPSwitch", (value, item) => value with { GpuTgpEnabled = item });
        settings = ApplyInt(settings, root, "GPU_DynamicBoost", 0, 1000, (value, item) => value with { GpuDynamicBoost = item });
        settings = ApplyInt(settings, root, "GPU_DynamicBoostMinimum", 0, 1000, (value, item) => value with { GpuDynamicBoostMinimum = item });
        settings = ApplyInt(settings, root, "GPU_DynamicBoostMaximum", 0, 1000, (value, item) => value with { GpuDynamicBoostMaximum = item });
        settings = ApplyBool(settings, root, "GPU_DynamicBoostSwitch", (value, item) => value with { GpuDynamicBoostEnabled = item });

        settings = ApplyInt(settings, root, "GPU_CoreClockOffsetOC", -2000, 2000, (value, item) => value with { GpuCoreClockOffset = item });
        settings = ApplyInt(settings, root, "GPU_CoreClockOffsetMinimum", -2000, 2000, (value, item) => value with { GpuCoreClockOffsetMinimum = item });
        settings = ApplyInt(settings, root, "GPU_CoreClockOffsetMaximum", -2000, 2000, (value, item) => value with { GpuCoreClockOffsetMaximum = item });
        settings = ApplyInt(settings, root, "GPU_MemoryClockOffsetOC", -3000, 3000, (value, item) => value with { GpuMemoryClockOffset = item });
        settings = ApplyInt(settings, root, "GPU_MemoryClockOffsetMinimum", -3000, 3000, (value, item) => value with { GpuMemoryClockOffsetMinimum = item });
        settings = ApplyInt(settings, root, "GPU_MemoryClockOffsetMaximum", -3000, 3000, (value, item) => value with { GpuMemoryClockOffsetMaximum = item });
        settings = ApplyBool(settings, root, "OverClockingSwitch", (value, item) => value with { OverclockingEnabled = item });

        settings = ApplyInt(settings, root, "FAN_FanSwitchSpeed", 0, 25500, (value, item) => value with { FanSwitchSpeed = item });
        settings = ApplyBool(settings, root, "FAN_FanSwitchSpeedEnabled", (value, item) => value with { FanSwitchSpeedEnabled = item });
        settings = ApplyBool(settings, root, "FanBoostEnable", (value, item) => value with { FanBoostEnabled = item });
        settings = ApplyBool(settings, root, "FanBoostBtnSupport", (value, item) => value with { FanBoostButtonSupported = item });
        settings = ApplyBool(settings, root, "MEM_MemoryOverClockSupport", (value, item) => value with { MemoryOverclockSupported = item });
        settings = ApplyBool(settings, root, "MEM_MemoryOverClockSwitch", (value, item) => value with { MemoryOverclockEnabled = item });
        return settings;
    }

    private static GcuCustomSettings ApplyInt(GcuCustomSettings settings, JsonElement root,
        string name, int minimum, int maximum, Func<GcuCustomSettings, int?, GcuCustomSettings> apply)
    {
        if (!root.TryGetProperty(name, out var element)) return settings;
        return apply(settings, Integer(element, minimum, maximum));
    }

    private static GcuCustomSettings ApplyBool(GcuCustomSettings settings, JsonElement root,
        string name, Func<GcuCustomSettings, bool?, GcuCustomSettings> apply)
    {
        if (!root.TryGetProperty(name, out var element)) return settings;
        return apply(settings, Boolean(element));
    }

    private static GcuCustomSettings ApplyString(GcuCustomSettings settings, JsonElement root,
        string name, Func<GcuCustomSettings, string?, GcuCustomSettings> apply)
    {
        if (!root.TryGetProperty(name, out var element)) return settings;
        string? value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return apply(settings, string.IsNullOrWhiteSpace(value) ? null : value);
    }

    private static bool TryParseFanTable(JsonElement root, out FanCurveTable? table)
    {
        table = null;
        if (!root.TryGetProperty("CPU", out var cpuElement) ||
            !root.TryGetProperty("GPU", out var gpuElement) ||
            !TryParseCurve(cpuElement, out var cpu) ||
            !TryParseCurve(gpuElement, out var gpu))
            return false;

        bool? activated = root.TryGetProperty("Activated", out var activatedElement)
            ? Boolean(activatedElement) : null;
        bool? respective = root.TryGetProperty("FanControlRespective", out var respectiveElement)
            ? Boolean(respectiveElement) : null;
        string? name = root.TryGetProperty("Name", out var nameElement) &&
            nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() : null;
        int? cpuMax = root.TryGetProperty("CpuTemp_DefaultMaxLevel", out var cpuMaxElement)
            ? Integer(cpuMaxElement, 0, 32) : null;
        int? gpuMax = root.TryGetProperty("GpuTemp_DefaultMaxLevel", out var gpuMaxElement)
            ? Integer(gpuMaxElement, 0, 32) : null;
        table = new FanCurveTable(activated, name, respective, cpuMax, gpuMax, cpu, gpu);
        return true;
    }

    private static bool TryParseCurve(JsonElement element, out IReadOnlyList<FanCurvePoint> curve)
    {
        curve = Array.Empty<FanCurvePoint>();
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() is < 1 or > 16)
            return false;
        var points = new List<FanCurvePoint>(16);
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("ID", out var idElement) ||
                !item.TryGetProperty("UpT", out var upElement) ||
                !item.TryGetProperty("DownT", out var downElement) ||
                !item.TryGetProperty("Duty", out var dutyElement))
                return false;
            int? id = Integer(idElement, 0, 15);
            int? up = Integer(upElement, 0, 255);
            int? down = Integer(downElement, 0, 255);
            int? duty = Integer(dutyElement, 0, 100);
            if (id is null || up is null || down is null || duty is null || id != points.Count)
                return false;
            points.Add(new FanCurvePoint(id.Value, up.Value, down.Value, duty.Value));
        }
        curve = points;
        return true;
    }

    private static GcuSnapshot ApplyTurboSupport(GcuSnapshot snapshot, JsonElement root, DateTimeOffset now)
    {
        if (!root.TryGetProperty("TurboModeSupport", out var element)) return snapshot;
        bool? supported = Boolean(element);
        return snapshot with { TurboSupported = supported, TurboSupportedUpdatedAt = supported.HasValue ? now : null };
    }

    private static int? Integer(JsonElement element, int minimum, int maximum)
    {
        int value;
        bool valid = element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => Fail(out value)
        };
        return valid && value >= minimum && value <= maximum ? value : null;
    }

    private static bool Fail(out int value) { value = 0; return false; }

    private static bool? Boolean(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when element.TryGetInt32(out int number) => number switch { 0 => false, 1 => true, _ => null },
        JsonValueKind.String => element.GetString()?.Trim().ToLowerInvariant() switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => null
        },
        _ => null
    };
}
