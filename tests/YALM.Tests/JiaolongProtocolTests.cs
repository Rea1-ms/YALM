using YALM.Mechrevo;

internal static partial class Program
{
    private static Task JiaolongRequestsAreReadOnly()
    {
        foreach (bool supportsTemperature in new[] { false, true })
        {
            for (int command = 0; command <= byte.MaxValue; command++)
            {
                bool allowed = command is 8 or 13 || supportsTemperature && command == 22;
                try
                {
                    byte[] request = JiaolongReadProtocol.CreateRequest((JiaolongReadCommand)command, supportsTemperature);
                    Assert.True(allowed, "only the reviewed read commands can be constructed");
                    Assert.Equal(32, request.Length, "Jiaolong requests contain 32 bytes");
                    Assert.Equal((byte)250, request[1], "the operation is always a read");
                    Assert.Equal((byte)command, request[3], "the command has its own request byte");
                    Assert.True(request.Where((_, index) => index != 1 && index != 3).All(value => value == 0),
                        "requests cannot contain setting payloads");
                }
                catch (ArgumentOutOfRangeException)
                {
                    Assert.True(!allowed, "reviewed reads must not be rejected");
                }
            }
        }
        return Task.CompletedTask;
    }

    private static Task JiaolongReadingsMapAndExpireIndependently()
    {
        var observed = DateTimeOffset.UtcNow;
        for (int mode = 0; mode < 3; mode++)
        {
            var replies = new Dictionary<JiaolongReadCommand, byte[]?>
            {
                [JiaolongReadCommand.Mode] = JiaolongReply((byte)mode),
                [JiaolongReadCommand.FanSpeeds] = JiaolongReply(0xE2, 0x12, 0x82, 0x12),
                [JiaolongReadCommand.CpuTemperature] = JiaolongReply(63)
            };
            GcuSnapshot snapshot = JiaolongReadProtocol.Decode(replies, observed, supportsTemperature: true);
            Assert.Equal(new[] { 1, 2, 0 }[mode], snapshot.Mode, "native modes map to the app's semantic modes");
            Assert.Equal(4834, snapshot.CpuFanRpm, "CPU RPM uses little-endian response bytes");
            Assert.Equal(4738, snapshot.GpuFanRpm, "GPU RPM uses its separate little-endian response bytes");
            Assert.Equal(63, snapshot.CpuTemperature, "the reviewed Pro temperature is in degrees Celsius");
            Assert.Equal(observed, snapshot.ModeUpdatedAt, "mode has its current observation time");
            Assert.True(snapshot.IsCpuFanFresh(observed) && snapshot.IsGpuFanFresh(observed), "new fan readings are fresh");
            Assert.True(!snapshot.IsCpuFanFresh(observed.AddSeconds(11)), "fan readings expire normally");
            Assert.Null(snapshot.IsAc, "readings do not invent power status");
            Assert.Null(snapshot.TurboSupported, "observing turbo does not grant turbo control");
            Assert.True(!snapshot.CanSwitchMode(PerformanceMode.Balanced, observed), "partial telemetry cannot enable writes");

            GcuSnapshot legacy = JiaolongReadProtocol.Decode(replies, observed);
            Assert.Null(legacy.CpuTemperature, "legacy images cannot use the Pro-only temperature reply");
            Assert.Null(legacy.CpuTemperatureUpdatedAt, "ignored temperatures have no freshness time");
        }
        return Task.CompletedTask;
    }

    private static Task JiaolongInvalidRepliesStayUnknown()
    {
        var observed = DateTimeOffset.UtcNow;
        var replies = new Dictionary<JiaolongReadCommand, byte[]?>
        {
            [JiaolongReadCommand.Mode] = JiaolongReply(255),
            [JiaolongReadCommand.FanSpeeds] = JiaolongReply(255, 255, 0, 0),
            [JiaolongReadCommand.CpuTemperature] = JiaolongReply(0)
        };
        GcuSnapshot snapshot = JiaolongReadProtocol.Decode(replies, observed, supportsTemperature: true);
        Assert.Null(snapshot.Mode, "unknown mode is not mapped to silent");
        Assert.Null(snapshot.ModeUpdatedAt, "unknown mode does not refresh its timestamp");
        Assert.Null(snapshot.CpuFanRpm, "impossible CPU RPM stays unknown");
        Assert.Null(snapshot.CpuFanUpdatedAt, "invalid CPU RPM does not refresh a timestamp");
        Assert.Equal(0, snapshot.GpuFanRpm, "a valid stopped GPU fan stays zero independently");
        Assert.Null(snapshot.CpuTemperature, "invalid temperature stays unknown");
        Assert.Null(snapshot.CpuTemperatureUpdatedAt, "invalid temperature has no observation timestamp");

        foreach (byte[]? reply in new byte[]?[] { null, Array.Empty<byte>(), new byte[4], new byte[33] })
        {
            replies[JiaolongReadCommand.Mode] = reply;
            replies[JiaolongReadCommand.FanSpeeds] = reply;
            replies[JiaolongReadCommand.CpuTemperature] = reply;
            snapshot = JiaolongReadProtocol.Decode(replies, observed, supportsTemperature: true);
            Assert.True(!snapshot.IsConnected, "missing or structurally invalid replies cannot show a live reading");
            Assert.Null(snapshot.Mode, "invalid frames never retain an earlier mode");
            Assert.Null(snapshot.GpuFanRpm, "invalid frames never retain earlier RPM");
        }
        return Task.CompletedTask;
    }

    private static byte[] JiaolongReply(params byte[] payload)
    {
        byte[] reply = new byte[32];
        payload.CopyTo(reply, 4);
        return reply;
    }
}
