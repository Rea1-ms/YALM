namespace YALM.Mechrevo;

/// <summary>
/// The two direct read requests reviewed in the Yilong 16X 1.3.232 control center.
/// EC mailbox transactions and all setting requests are deliberately absent.
/// </summary>
public static class YilongReadProtocol
{
    public const string Scope = @"root\wmi";
    public const string ClassName = "ACPIMethod";
    public const string InputName = "IData";
    public const string OutputName = "OData";
    public const string ModeMethod = "MemIO";
    public const string GpuTemperatureMethod = "DoMethod";

    public static byte[] ModeRequest()
    {
        byte[] request = Header();
        request[4] = 2;
        request[5] = 3; // Port 0x0302, little endian.
        request[12] = 1; // IO-port access.
        request[13] = 0; // Read.
        request[14] = 8; // Eight bits.
        request[15] = 96;
        return request;
    }

    public static byte[] GpuTemperatureRequest()
    {
        byte[] request = Header();
        request[4] = 4; // GPU temperature command; operation and data stay zero.
        return request;
    }

    public static GcuSnapshot Parse(uint? rawMode, uint? rawGpuTemperature, DateTimeOffset observedAt)
    {
        // 4 and 5 are the battery variants of quiet and balanced, not extra modes.
        int? mode = rawMode switch
        {
            0 or 4 => 0,
            1 or 5 => 1,
            2 => 2,
            3 => 3,
            _ => null
        };
        int? gpuTemperature = rawGpuTemperature is >= 1 and <= 125 ? (int)rawGpuTemperature.Value : null;
        return new GcuSnapshot
        {
            IsConnected = mode.HasValue || gpuTemperature.HasValue,
            Mode = mode,
            ModeUpdatedAt = mode.HasValue ? observedAt : null,
            GpuTemperature = gpuTemperature,
            GpuTemperatureUpdatedAt = gpuTemperature.HasValue ? observedAt : null
        };
    }

    private static byte[] Header() =>
        [66, 89, 68, 76, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
}
