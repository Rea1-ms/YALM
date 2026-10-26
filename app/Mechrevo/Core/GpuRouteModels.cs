namespace YALM.Mechrevo;

public enum InternalPanelAdapter
{
    Unknown,
    Integrated,
    Discrete
}

public enum DgpuPresence
{
    Unknown,
    Present,
    Disabled,
    Absent
}

public enum GpuDisplayRoute
{
    Unknown,
    IgpuOnly,
    Hybrid,
    Direct
}

public sealed record GpuRouteSnapshot
{
    public InternalPanelAdapter InternalPanelAdapter { get; init; } = InternalPanelAdapter.Unknown;
    public DgpuPresence DgpuPresence { get; init; } = DgpuPresence.Unknown;
    public GpuDisplayRoute Route { get; init; } = GpuDisplayRoute.Unknown;
    public string? AdapterDevicePath { get; init; }
    public string? SourceDeviceName { get; init; }
    public string? MonitorFriendlyName { get; init; }
    public string? OutputTechnology { get; init; }
    public bool InternalPanelActive { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }

    public bool IsFresh(DateTimeOffset? now = null)
    {
        if (Route == GpuDisplayRoute.Unknown || !UpdatedAt.HasValue) return false;
        TimeSpan age = (now ?? DateTimeOffset.UtcNow) - UpdatedAt.Value;
        return age >= TimeSpan.Zero && age <= GcuSnapshot.Freshness;
    }
}

public static class GpuRouteInference
{
    public static InternalPanelAdapter ClassifyAdapterPath(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return InternalPanelAdapter.Unknown;
        if (devicePath.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase))
            return InternalPanelAdapter.Discrete;
        if (devicePath.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) ||
            devicePath.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase))
            return InternalPanelAdapter.Integrated;
        return InternalPanelAdapter.Unknown;
    }

    public static GpuDisplayRoute Infer(InternalPanelAdapter panel, DgpuPresence dgpu) => panel switch
    {
        InternalPanelAdapter.Discrete when dgpu == DgpuPresence.Present => GpuDisplayRoute.Direct,
        InternalPanelAdapter.Integrated when dgpu == DgpuPresence.Present => GpuDisplayRoute.Hybrid,
        InternalPanelAdapter.Integrated when dgpu is DgpuPresence.Disabled or DgpuPresence.Absent =>
            GpuDisplayRoute.IgpuOnly,
        _ => GpuDisplayRoute.Unknown
    };
}
