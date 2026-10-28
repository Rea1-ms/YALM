namespace YALM.Mechrevo;

public enum NativeHardwareFamily { Jiaolong, Yilong, Imini }
public enum NativeHardwareReading { Mode, FanSpeeds, CpuTemperature, GpuTemperature, FanCount, GpuSupport }
public enum NativeHardwareSchemaStatus { Matched, MissingClass, MissingMethod, InvalidParameters, MissingInstance, AmbiguousInstances }

/// <summary>Metadata only. Creating this observation never invokes a firmware method.</summary>
public sealed record NativeHardwareEndpoint(
    NativeHardwareFamily Family,
    string InstancePath,
    string? ImageHash,
    NativeHardwareSchemaStatus SchemaStatus)
{
    public bool GpuTemperatureSupported { get; init; }
    public bool CpuTemperatureSupported { get; init; }
}

/// <summary>The backend accepts only the bounded read operations above, never arbitrary commands.</summary>
public interface INativeHardwareReadBackend
{
    IReadOnlyList<NativeHardwareEndpoint> Inspect(CancellationToken ct);
    object? Read(NativeHardwareEndpoint endpoint, NativeHardwareReading reading, CancellationToken ct);
}
