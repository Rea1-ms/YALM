using System;
using System.Globalization;
using System.Text.Json;

namespace YALM.Mechrevo;

internal static class GcuMessageParser
{
    public static GcuSnapshot Apply(GcuSnapshot snapshot, string topic, string payload,
        DateTimeOffset now, out bool modeReported)
    {
        modeReported = false;
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
                    break;
                case "Setting/Status":
                    snapshot = ApplyTurboSupport(snapshot, root, now);
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
