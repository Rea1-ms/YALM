using System;

namespace YALM.Mechrevo;

public static class EcFanRegisters
{
    public const int CpuFanRpmHigh = 1124;
    public const int CpuFanRpmLow = 1125;
    public const int GpuFanRpmLow = 1131;
    public const int GpuFanRpmHigh = 1132;
    public const int FanSafetyFlags = 1957;
    public const int GenericThermalFlags = 1992;

    public static IReadOnlyList<int> AllReadAddresses { get; } =
    [
        CpuFanRpmHigh,
        CpuFanRpmLow,
        GpuFanRpmHigh,
        GpuFanRpmLow,
        FanSafetyFlags,
        GenericThermalFlags
    ];
}

/// <summary>Read-only bytes returned by the vendor ACPI driver.</summary>
public sealed record EcRegisterSnapshot
{
    public IReadOnlyDictionary<int, byte> Registers { get; init; } = new Dictionary<int, byte>();
    public DateTimeOffset? UpdatedAt { get; init; }
    public int? ErrorCode { get; init; }
    public string? LastError { get; init; }

    public bool IsComplete(IEnumerable<int> expectedAddresses)
    {
        ArgumentNullException.ThrowIfNull(expectedAddresses);
        return LastError is null && expectedAddresses.All(Registers.ContainsKey);
    }
}

/// <summary>Direct EC observations stay separate from vendor-service telemetry.</summary>
public sealed record EcFanSnapshot
{
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(4);

    public int? CpuFanRpm { get; init; }
    public int? GpuFanRpm { get; init; }
    public bool? FanSafetyProtect { get; init; }
    public bool? GenericThermalProtect { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public int? ErrorCode { get; init; }
    public string? LastError { get; init; }

    public bool IsFresh(DateTimeOffset? now = null)
    {
        if (!UpdatedAt.HasValue || !CpuFanRpm.HasValue || !GpuFanRpm.HasValue) return false;
        TimeSpan age = (now ?? DateTimeOffset.UtcNow) - UpdatedAt.Value;
        return age >= TimeSpan.Zero && age <= Freshness;
    }

    public bool HasActiveProtection(DateTimeOffset? now = null) =>
        IsFresh(now) && (FanSafetyProtect == true || GenericThermalProtect == true);
}
