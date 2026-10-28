using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace YALM.Mechrevo;

/// <summary>
/// Read-only protocol recovered statically from the iminiPro V1.4 control center.
/// Fan numbers are kept generic: the reviewed UI does not identify their cooling targets.
/// </summary>
internal static class IminiReadProtocol
{
    public const string Namespace = @"root\WMI";
    public const string ClassName = "PowerSwitchInterface";
    public const string InstanceName = @"ACPI\PNP0C14\IP3POWERSWITCH_0";
    public const string PowerModeMethod = "GetPowerMode";
    public const string FeatureMethod = "GetFeatureValue";
    public const string FanMethod = "GetFanControl";
    public const string TemperatureMethod = "GetHwTemp";
    public const string PowerModeProperty = "CurrentPowerMode";
    public const string FeatureProperty = "ResultStatus";
    public const string FanProperty = "FanDuty";
    public const string TemperatureProperty = "Temp";

    public static IReadOnlyDictionary<string, object> PowerModeParameters { get; } =
        ReadOnly(new Dictionary<string, object>());

    public static IReadOnlyDictionary<string, object> FanParameters { get; } =
        ReadOnly(new Dictionary<string, object> { ["FanNumber"] = 1u });

    public static IReadOnlyDictionary<string, object> CpuTemperatureParameters { get; } =
        ReadOnly(new Dictionary<string, object> { ["HwTempType"] = 1u });

    public static IReadOnlyDictionary<string, object> FanCountParameters { get; } = FeatureParameters(3u);
    public static IReadOnlyDictionary<string, object> GpuSupportParameters { get; } = FeatureParameters(4u);

    public static GcuSnapshot Decode(uint? powerMode, uint? packedFanRpm, uint? fanCount,
        uint? gpuSupport, DateTimeOffset observedAt, uint? packedCpuTemperature = null)
    {
        int? mode = powerMode switch
        {
            2u => (int)PerformanceMode.Silent,
            0u => (int)PerformanceMode.Balanced,
            1u => (int)PerformanceMode.Turbo,
            _ => null
        };
        int? first = null;
        int? second = null;
        int? numberedFanCount = fanCount switch
        {
            1u => 1,
            2u when gpuSupport == 1u => 2,
            2u when gpuSupport == 0u => 1,
            _ => null
        };
        if (packedFanRpm.HasValue && fanCount is 1u or 2u)
        {
            // Despite its name, FanDuty carries two unsigned RPM words in this reader.
            first = Rpm(packedFanRpm.Value & 0xffffu);
            if (fanCount == 2u && gpuSupport == 1u) second = Rpm(packedFanRpm.Value >> 16);
        }
        int? temperature = packedCpuTemperature is uint rawTemperature && (rawTemperature & 0xffu) is >= 1u and <= 125u
            ? (int)(rawTemperature & 0xffu) : null;
        return new GcuSnapshot
        {
            IsConnected = mode.HasValue || first.HasValue || second.HasValue || temperature.HasValue,
            Mode = mode,
            ModeUpdatedAt = mode.HasValue ? observedAt : null,
            Fan1Rpm = first,
            Fan2Rpm = second,
            NumberedFanCount = numberedFanCount,
            Fan1UpdatedAt = first.HasValue ? observedAt : null,
            Fan2UpdatedAt = second.HasValue ? observedAt : null,
            CpuTemperature = temperature,
            CpuTemperatureUpdatedAt = temperature.HasValue ? observedAt : null
        };
    }

    private static int? Rpm(uint value) => value <= 20000u && value != 8000u ? (int)value : null;

    private static IReadOnlyDictionary<string, object> FeatureParameters(uint feature) =>
        ReadOnly(new Dictionary<string, object>
        {
            ["Reserved1"] = feature,
            ["Reserved2"] = 0u,
            ["Reserved3"] = 0u,
            ["Reserved4"] = 0u
        });

    private static IReadOnlyDictionary<string, object> ReadOnly(Dictionary<string, object> values) =>
        new ReadOnlyDictionary<string, object>(values);
}
