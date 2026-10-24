namespace YALM.Mechrevo;

public enum FanCurveTarget
{
    Cpu = 0,
    Gpu = 1
}

public enum GcuCustomWriteField
{
    Pl1,
    Pl2,
    Pl4,
    TccTarget,
    TccEnabled,
    FanSwitchSpeed,
    FanSwitchSpeedEnabled
}

public enum GcuWriteStatus
{
    Confirmed,
    Rejected,
    Busy,
    TimedOut,
    Disconnected,
    Cancelled,
    Failed
}

public sealed record GcuWriteResult(GcuWriteStatus Status, string Message)
{
    public bool Confirmed => Status == GcuWriteStatus.Confirmed;
}

public sealed record FanCurvePoint(
    int Id,
    int UpTemperature,
    int DownTemperature,
    int Duty)
{
    // The last effective slot has a valid rising threshold and a 255 falling
    // terminator. Only two terminators identify an unused tail slot.
    public bool IsTail => UpTemperature == 255 && DownTemperature == 255;

    // Slot zero has no rising threshold. Plot its actual fan-stop threshold,
    // keeping the protocol's zero sentinel out of line interpolation.
    public int PlotTemperature => UpTemperature == 0 && Duty == 0 && DownTemperature < 255
        ? DownTemperature : UpTemperature;
}

public static class FanCurveDraft
{
    public static bool CanEdit(IReadOnlyList<FanCurvePoint>? curve) =>
        curve is { Count: 16 } && curve.Take(11).All(point => point.UpTemperature < 255) &&
        curve.Skip(11).All(point => point.IsTail);

    public static bool TryBuild(IReadOnlyList<int> editableDuties, out int[] duties, out string error)
    {
        ArgumentNullException.ThrowIfNull(editableDuties);
        duties = new int[16];
        error = "";
        if (editableDuties.Count != 11)
        {
            error = "风扇曲线必须包含 11 个可编辑温度点。";
            return false;
        }

        if (editableDuties[0] != 0)
        {
            error = "第一个曲线点固定为 0%。";
            return false;
        }

        for (int index = 1; index < editableDuties.Count; index++)
        {
            if (editableDuties[index] is < 0 or > 100)
            {
                error = $"第 {index + 1} 个曲线的占空比无效。";
                return false;
            }
            if (editableDuties[index] < editableDuties[index - 1])
            {
                error = "风扇曲线占空比不能随温度下降。";
                return false;
            }
        }

        for (int index = 0; index < duties.Length; index++)
            duties[index] = editableDuties[Math.Min(index, 10)];
        return true;
    }
}

public sealed record FanCurveTable(
    bool? Activated,
    string? Name,
    bool? ControlRespective,
    int? CpuMaxLevel,
    int? GpuMaxLevel,
    IReadOnlyList<FanCurvePoint> Cpu,
    IReadOnlyList<FanCurvePoint> Gpu)
{
    public IReadOnlyList<FanCurvePoint> Curve(FanCurveTarget target) =>
        target == FanCurveTarget.Cpu ? Cpu : Gpu;
}

public sealed record GcuCustomSettings
{
    public int? ProfileIndex { get; init; }
    public string? ProfileName { get; init; }
    public string? TableName { get; init; }

    public int? Pl1 { get; init; }
    public int? Pl1Minimum { get; init; }
    public int? Pl1Maximum { get; init; }
    public int? Pl2 { get; init; }
    public int? Pl2Minimum { get; init; }
    public int? Pl2Maximum { get; init; }
    public int? Pl4 { get; init; }
    public int? Pl4Minimum { get; init; }
    public int? Pl4Maximum { get; init; }
    public bool? Pl4HalfWatt { get; init; }

    public int? TccRawOffset { get; init; }
    public int? TccTarget => TjMax is { } tjMax && TccRawOffset is { } raw
        ? tjMax - raw
        : null;
    public int? TjMax { get; init; }
    public bool? TccEnabled { get; init; }

    public int? GpuTgp { get; init; }
    public int? GpuTgpMinimum { get; init; }
    public int? GpuTgpMaximum { get; init; }
    public bool? GpuTgpEnabled { get; init; }
    public int? GpuDynamicBoost { get; init; }
    public int? GpuDynamicBoostMinimum { get; init; }
    public int? GpuDynamicBoostMaximum { get; init; }
    public bool? GpuDynamicBoostEnabled { get; init; }

    public int? GpuCoreClockOffset { get; init; }
    public int? GpuCoreClockOffsetMinimum { get; init; }
    public int? GpuCoreClockOffsetMaximum { get; init; }
    public int? GpuMemoryClockOffset { get; init; }
    public int? GpuMemoryClockOffsetMinimum { get; init; }
    public int? GpuMemoryClockOffsetMaximum { get; init; }
    public bool? OverclockingEnabled { get; init; }

    public int? FanSwitchSpeed { get; init; }
    public bool? FanSwitchSpeedEnabled { get; init; }
    public bool? FanBoostEnabled { get; init; }
    public bool? FanBoostButtonSupported { get; init; }
    public bool? MemoryOverclockSupported { get; init; }
    public bool? MemoryOverclockEnabled { get; init; }

    public bool CanWriteCpuPower =>
        Pl1 is not null && Pl1Minimum is not null && Pl1Maximum >= Pl1Minimum &&
        Pl2 is not null && Pl2Minimum is not null && Pl2Maximum >= Pl2Minimum &&
        Pl4 is not null && Pl4Minimum is not null && Pl4Maximum >= Pl4Minimum;
}
