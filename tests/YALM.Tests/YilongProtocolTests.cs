using YALM.Mechrevo;

internal static partial class Program
{
    private static Task YilongRequestsAreReadOnly()
    {
        byte[] mode = YilongReadProtocol.ModeRequest();
        Assert.True(mode.SequenceEqual(new byte[]
        {
            66, 89, 68, 76, 2, 3, 0, 0, 0, 0, 0, 0, 1, 0, 8, 96, 0, 0, 0, 0, 0, 0, 0, 0
        }), "the reviewed performance query uses the direct read port and no payload");
        byte[] temperature = YilongReadProtocol.GpuTemperatureRequest();
        Assert.True(temperature.SequenceEqual(new byte[]
        {
            66, 89, 68, 76, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
        }), "the GPU temperature query uses DoMethod command 4 without a write operation");
        mode[13] = 1;
        temperature[12] = 1;
        Assert.Equal((byte)0, YilongReadProtocol.ModeRequest()[13], "callers cannot mutate later mode requests");
        Assert.Equal((byte)0, YilongReadProtocol.GpuTemperatureRequest()[12], "callers cannot mutate later temperature requests");
        return Task.CompletedTask;
    }

    private static Task YilongModesAndFieldFreshness()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int[] expectedModes = [0, 1, 2, 3, 0, 1];
        for (uint raw = 0; raw < expectedModes.Length; raw++)
        {
            GcuSnapshot snapshot = YilongReadProtocol.Parse(raw, 61, now);
            Assert.Equal<int?>(expectedModes[raw], snapshot.Mode, "battery variants normalize to their reported preset");
            Assert.Equal<int?>(61, snapshot.GpuTemperature, "valid GPU temperature remains independent of mode");
            Assert.Equal<DateTimeOffset?>(now, snapshot.ModeUpdatedAt, "the mode uses the current observation time");
            Assert.Equal<DateTimeOffset?>(now, snapshot.GpuTemperatureUpdatedAt, "the temperature uses the current observation time");
            Assert.True(snapshot.CpuFanRpm is null && snapshot.GpuFanRpm is null && snapshot.CpuTemperature is null &&
                snapshot.IsAc is null && snapshot.TurboSupported is null && snapshot.FanTable is null,
                "read packets must not invent fan, protection, supply or control capability observations");
        }
        GcuSnapshot unknownMode = YilongReadProtocol.Parse(6, 60, now);
        Assert.True(unknownMode.Mode is null && unknownMode.ModeUpdatedAt is null && unknownMode.GpuTemperature == 60,
            "an unknown mode does not discard an independent temperature reading");
        GcuSnapshot unknownTemperature = YilongReadProtocol.Parse(1, 255, now);
        Assert.True(unknownTemperature.GpuTemperature is null && unknownTemperature.GpuTemperatureUpdatedAt is null &&
            unknownTemperature.Mode == 1, "a rejected temperature never borrows a timestamp or invalidates a valid mode");
        Assert.True(!YilongReadProtocol.Parse(0, 60, now - GcuSnapshot.Freshness - TimeSpan.FromSeconds(1))
            .IsGpuTemperatureFresh(now), "read observations expire under the existing freshness rules");
        return Task.CompletedTask;
    }

    private static Task YilongInvalidValuesStayUnknown()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (uint? value in new uint?[] { null, 126, 255, 0x80000001, uint.MaxValue })
        {
            GcuSnapshot snapshot = YilongReadProtocol.Parse(value, value, now);
            Assert.True(!snapshot.IsConnected && snapshot.Mode is null && snapshot.GpuTemperature is null &&
                snapshot.ModeUpdatedAt is null && snapshot.GpuTemperatureUpdatedAt is null,
                "missing, out-of-range and vendor failure sentinels never become valid observations");
        }
        Assert.True(YilongReadProtocol.Parse(null, 0, now).GpuTemperature is null,
            "zero temperature does not claim a working sensor");
        Assert.Equal<int?>(1, YilongReadProtocol.Parse(null, 1, now).GpuTemperature, "lower valid temperature is retained");
        Assert.Equal<int?>(125, YilongReadProtocol.Parse(null, 125, now).GpuTemperature, "upper valid temperature is retained");
        return Task.CompletedTask;
    }
}
