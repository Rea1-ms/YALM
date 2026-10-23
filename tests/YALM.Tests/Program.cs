using System.Collections.Concurrent;
using System.Text.Json;
using YALM.Mechrevo;

internal static class Program
{
    private static readonly GcuConnectionOptions Options = new("test-client", "test-user", "test-password")
    {
        TurboSupported = true
    };

    public static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("invalid readings never become zero", InvalidReadingsNeverBecomeZero),
            ("fields expire independently", FieldsExpireIndependently),
            ("EC fan readings decode RPM and protection", EcFanReadingsDecode),
            ("incomplete or impossible EC readings are rejected", InvalidEcReadingsRejected),
            ("connect subscribes before publishing queries", ConnectSubscribesBeforePublishing),
            ("telemetry initializes once per connection", TelemetryInitializesOncePerConnection),
            ("disconnect clears all readings", DisconnectClearsReadings),
            ("mode command order and numeric profile index", ModeCommandOrder),
            ("old status cannot confirm a switch", OldStatusCannotConfirm),
            ("publish failure cannot report success", PublishFailureCannotReportSuccess),
            ("busy clicks do not duplicate commands", BusyClicksDoNotDuplicateCommands),
            ("turbo requires support and AC", TurboRequiresSupportAndAc),
            ("fan duty never invents actual RPM", FanDutyNeverInventsRpm),
            ("fan duty accepts zero and rejects invalid values", FanDutyRejectsInvalidValues),
            ("fan duty and RPM expire independently", FanDutyExpiresIndependently),
            ("explicit invalid telemetry clears earlier readings", ExplicitInvalidTelemetryClearsReadings),
            ("unplugging AC blocks turbo before publication", () => PlatformPowerBlocksTurbo(() => false, false)),
            ("unknown platform power blocks turbo before publication", () => PlatformPowerBlocksTurbo(() => null, null)),
            ("failed platform power read blocks turbo before publication", () => PlatformPowerBlocksTurbo(() => throw new InvalidOperationException("power unavailable"), null)),
            ("service AC cannot override platform battery power", () => ServiceCannotOverridePlatformPower(false)),
            ("service AC cannot override unknown platform power", () => ServiceCannotOverridePlatformPower(null)),
            ("legacy string mode and fan duty use platform power", LegacyTelemetryUsesPlatformPower),
            ("subscription failure cleans up connection", SubscriptionFailureCleansUp),
            ("missing credentials are rejected", MissingCredentialsRejected),
            ("reconnect does not duplicate callbacks", ReconnectDoesNotDuplicateCallbacks)
        };
        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                await run();
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL  {name}: {ex.Message}");
            }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
        return failures == 0 ? 0 : 1;
    }

    private static Task InvalidReadingsNeverBecomeZero()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new GcuSnapshot { IsConnected = true };
        snapshot = GcuMessageParser.Apply(snapshot, "System/CpuInfo", "{\"CpuTemperature\":0}", now, out _);
        Assert.Null(snapshot.CpuTemperature, "zero CPU temperature must be rejected");
        snapshot = GcuMessageParser.Apply(snapshot, "System/CpuInfo", "{\"CpuTemperature\":130}", now, out _);
        Assert.Null(snapshot.CpuTemperature, "out-of-range CPU temperature must be rejected");
        snapshot = GcuMessageParser.Apply(snapshot, "System/CpuInfo", "{\"CpuTemperature\":55}", now, out _);
        Assert.Equal(55, snapshot.CpuTemperature, "valid CPU temperature should be retained");
        snapshot = GcuMessageParser.Apply(snapshot, "System/CpuInfo", "{}", now, out _);
        Assert.Equal(55, snapshot.CpuTemperature, "missing temperature must not become zero");
        snapshot = GcuMessageParser.Apply(snapshot, "System/FanInfo", "{\"CpuFanRpm\":-1,\"GpuFanRpm\":25001}", now, out _);
        Assert.Null(snapshot.CpuFanRpm, "negative fan RPM must be rejected");
        Assert.Null(snapshot.GpuFanRpm, "out-of-range fan RPM must be rejected");
        snapshot = GcuMessageParser.Apply(snapshot, "System/FanInfo", "{\"CpuFanRpm\":1200}", now, out _);
        Assert.Equal(1200, snapshot.CpuFanRpm, "valid fan RPM should be retained");
        snapshot = GcuMessageParser.Apply(snapshot, "System/FanInfo", "{}", now, out _);
        Assert.Equal(1200, snapshot.CpuFanRpm, "missing fan RPM must not become zero");
        return Task.CompletedTask;
    }

    private static Task FieldsExpireIndependently()
    {
        var observed = DateTimeOffset.UtcNow;
        var snapshot = new GcuSnapshot
        {
            IsConnected = true,
            CpuFanRpm = 1000, CpuFanUpdatedAt = observed,
            GpuFanRpm = 1100, GpuFanUpdatedAt = observed,
            CpuTemperature = 60, CpuTemperatureUpdatedAt = observed,
            GpuTemperature = 65, GpuTemperatureUpdatedAt = observed
        };
        var later = observed.AddSeconds(11);
        Assert.False(snapshot.IsCpuFanFresh(later), "CPU fan should expire");
        Assert.False(snapshot.IsGpuFanFresh(later), "GPU fan should expire");
        var mixed = snapshot with { CpuFanUpdatedAt = later.AddSeconds(-1), GpuFanUpdatedAt = later.AddSeconds(-11) };
        Assert.True(mixed.IsCpuFanFresh(later), "fresh CPU fan must remain usable");
        Assert.False(mixed.IsGpuFanFresh(later), "stale GPU fan must be rejected independently");
        Assert.False(mixed.IsFanFresh(later), "combined fan state requires both fields");
        return Task.CompletedTask;
    }

    private static Task EcFanReadingsDecode()
    {
        var observed = new DateTimeOffset(2026, 10, 23, 5, 19, 4, TimeSpan.Zero);
        var registers = new Dictionary<int, byte>
        {
            [EcFanRegisters.CpuFanRpmHigh] = 18,
            [EcFanRegisters.CpuFanRpmLow] = 226,
            [EcFanRegisters.GpuFanRpmHigh] = 18,
            [EcFanRegisters.GpuFanRpmLow] = 130,
            [EcFanRegisters.FanSafetyFlags] = 8,
            [EcFanRegisters.GenericThermalFlags] = 16
        };

        var snapshot = EcFanDecoder.Decode(registers, observed);

        Assert.Equal(4834, snapshot.CpuFanRpm, "CPU fan bytes should combine as high times 256 plus low");
        Assert.Equal(4738, snapshot.GpuFanRpm, "GPU fan bytes should combine as high times 256 plus low");
        Assert.Equal(true, snapshot.FanSafetyProtect, "safety bit should be decoded");
        Assert.Equal(true, snapshot.GenericThermalProtect, "thermal protection bit should be decoded");
        Assert.True(snapshot.IsFresh(observed.AddSeconds(3)), "three-second-old direct reading should be fresh");
        Assert.True(snapshot.HasActiveProtection(observed), "active protection should be exposed");
        Assert.False(snapshot.IsFresh(observed.Add(EcFanSnapshot.Freshness).AddMilliseconds(1)),
            "direct EC reading should expire");
        return Task.CompletedTask;
    }

    private static Task InvalidEcReadingsRejected()
    {
        var observed = new DateTimeOffset(2026, 10, 23, 5, 19, 4, TimeSpan.Zero);
        var incomplete = new Dictionary<int, byte>
        {
            [EcFanRegisters.CpuFanRpmHigh] = 18,
            [EcFanRegisters.CpuFanRpmLow] = 226
        };
        var missing = EcFanDecoder.Decode(incomplete, observed);
        Assert.NotNull(missing.LastError, "missing registers should produce an explicit error");
        Assert.Null(missing.CpuFanRpm, "partial bytes must not become an RPM reading");
        Assert.False(missing.IsFresh(observed), "error snapshots must not be displayed as fresh");

        var impossible = new Dictionary<int, byte>
        {
            [EcFanRegisters.CpuFanRpmHigh] = 255,
            [EcFanRegisters.CpuFanRpmLow] = 255,
            [EcFanRegisters.GpuFanRpmHigh] = 18,
            [EcFanRegisters.GpuFanRpmLow] = 130,
            [EcFanRegisters.FanSafetyFlags] = 0,
            [EcFanRegisters.GenericThermalFlags] = 0
        };
        var outOfRange = EcFanDecoder.Decode(impossible, observed);
        Assert.NotNull(outOfRange.LastError, "impossible RPM should produce an explicit error");
        Assert.Null(outOfRange.CpuFanRpm, "out-of-range RPM must not be displayed");
        Assert.False(outOfRange.HasActiveProtection(observed), "invalid readings cannot claim active protection");
        return Task.CompletedTask;
    }

    private static async Task ConnectSubscribesBeforePublishing()
    {
        await using var fake = new FakeTransport { RejectPublishBeforeSubscribe = true };
        await using var controller = new GcuController(fake);
        await controller.ConnectAsync(Options);
        Assert.True(fake.Subscribed, "subscription should complete");
        Assert.True(fake.Published.Count >= 3, "connect should query initial status");
        Assert.True(fake.Published.All(x => x.AfterSubscribe), "no publish may precede subscription");
    }

    private static async Task DisconnectClearsReadings()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake);
        fake.Emit("Fan/Status", "{\"OperatingMode\":1,\"IsAC\":true}");
        fake.Emit("System/FanInfo", "{\"CpuFanRpm\":1000,\"GpuFanRpm\":1200}");
        Assert.NotNull(controller.Snapshot.CpuFanRpm, "fixture should contain a reading");
        fake.DisconnectNow();
        Assert.False(controller.Snapshot.IsConnected, "disconnect should clear connection");
        Assert.Null(controller.Snapshot.CpuFanRpm, "disconnect should clear CPU fan reading");
        Assert.Null(controller.Snapshot.Mode, "disconnect should clear mode");
    }

    private static async Task TelemetryInitializesOncePerConnection()
    {
        await using var fake = new FakeTransport { RejectPublishBeforeSubscribe = true };
        await using var controller = await Connected(fake);
        Assert.Equal(1, fake.Published.Count(x => x.Topic == "System/Control" && x.Payload.Contains("System_ON", StringComparison.Ordinal)),
            "new connection must initialize system telemetry exactly once");

        var initialPublicationCount = fake.Published.Count;
        await controller.RefreshAsync();
        await controller.RefreshAsync();
        var refreshPublications = fake.Published.Skip(initialPublicationCount).ToArray();
        Assert.Equal(4, refreshPublications.Length, "each refresh should query only fan and settings status");
        Assert.Equal(2, refreshPublications.Count(x => x.Topic == "Fan/Control"), "each refresh should query fan status once");
        Assert.Equal(2, refreshPublications.Count(x => x.Topic == "Setting/Control"), "each refresh should query settings once");
        foreach (var publication in refreshPublications)
        {
            using var payload = JsonDocument.Parse(publication.Payload);
            Assert.Equal("GETSTATUS", payload.RootElement.GetProperty("Action").GetString(), "refresh must only request status");
        }
        Assert.Equal(1, fake.Published.Count(x => x.Topic == "System/Control"), "refresh must not reinitialize hardware telemetry");

        fake.DisconnectNow();
        var beforeReconnect = fake.Published.Count;
        await controller.ConnectAsync(Options);
        var reconnectPublications = fake.Published.Skip(beforeReconnect).ToArray();
        Assert.Equal(1, reconnectPublications.Count(x => x.Topic == "System/Control" && x.Payload.Contains("System_ON", StringComparison.Ordinal)),
            "a replacement connection must reinitialize system telemetry exactly once");
        Assert.True(reconnectPublications.All(x => x.AfterSubscribe), "reconnect must complete subscriptions before initialization");
        await controller.RefreshAsync();
        Assert.Equal(2, fake.Published.Count(x => x.Topic == "System/Control"), "refresh after reconnect must not add another initialization");
    }

    private static async Task ModeCommandOrder()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake);
        SeedReadyState(fake, mode: 0, ac: true);
        fake.Published.Clear();
        fake.OnPublish = (transport, topic, payload) =>
        {
            if (topic == "Fan/Control" && payload.Contains("GETSTATUS", StringComparison.Ordinal))
                transport.Emit("Fan/Status", "{\"OperatingMode\":1,\"IsAC\":true}");
            return Task.CompletedTask;
        };
        var result = await controller.SetModeAsync(PerformanceMode.Balanced);
        Assert.Equal(ModeSwitchStatus.ServiceConfirmed, result.Status, "fresh target readback should confirm");
        Assert.True(fake.Published.Count >= 3, "switch should publish two commands and a query");
        Assert.Equal("Fan/Control", fake.Published[0].Topic, "mode command must be first");
        Assert.Equal("LCHWOC/Control", fake.Published[1].Topic, "secondary mode command must be second");
        using var command = JsonDocument.Parse(fake.Published[0].Payload);
        Assert.Equal("OPERATING_GAMING_MODE", command.RootElement.GetProperty("Action").GetString(), "balanced action");
        Assert.True(command.RootElement.GetProperty("ProfileIndex").ValueKind == JsonValueKind.Number, "ProfileIndex must be numeric JSON");
        Assert.Equal(0, command.RootElement.GetProperty("ProfileIndex").GetInt32(), "ProfileIndex must be zero");
    }

    private static async Task OldStatusCannotConfirm()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(5));
        SeedReadyState(fake, mode: 0, ac: true);
        fake.Published.Clear();
        fake.OnPublish = (transport, topic, payload) =>
        {
            if (topic == "Fan/Control" && payload.Contains("GETSTATUS", StringComparison.Ordinal))
                transport.Emit("Fan/Status", "{\"OperatingMode\":0,\"IsAC\":true}");
            return Task.CompletedTask;
        };
        var result = await controller.SetModeAsync(PerformanceMode.Balanced);
        Assert.Equal(ModeSwitchStatus.TimedOut, result.Status, "old mode readback must not confirm");
        Assert.False(result.ServiceConfirmed, "timeout cannot report success");
    }

    private static async Task PublishFailureCannotReportSuccess()
    {
        await using var fake = new FakeTransport { ThrowOnPublishTopic = "LCHWOC/Control" };
        await using var controller = await Connected(fake);
        SeedReadyState(fake, mode: 0, ac: true);
        fake.Published.Clear();
        var result = await controller.SetModeAsync(PerformanceMode.Balanced);
        Assert.Equal(ModeSwitchStatus.Failed, result.Status, "publish failure should fail the switch");
        Assert.False(result.ServiceConfirmed, "publish failure cannot report success");
    }

    private static async Task BusyClicksDoNotDuplicateCommands()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(5));
        SeedReadyState(fake, mode: 0, ac: true);
        fake.Published.Clear();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.OnPublish = async (transport, topic, payload) =>
        {
            if (topic == "Fan/Control" && payload.Contains("GETSTATUS", StringComparison.Ordinal))
                await gate.Task.ConfigureAwait(false);
        };
        Task<ModeSwitchResult> first = controller.SetModeAsync(PerformanceMode.Balanced);
        await WaitUntil(() => fake.Published.Count >= 3);
        var second = await controller.SetModeAsync(PerformanceMode.Balanced);
        Assert.Equal(ModeSwitchStatus.Busy, second.Status, "second click should be busy");
        Assert.Equal(1, fake.Published.Count(x => x.Topic == "LCHWOC/Control"), "busy click must not duplicate secondary command");
        gate.TrySetResult();
        var firstResult = await first;
        Assert.False(firstResult.ServiceConfirmed, "no readback cannot confirm");
    }

    private static async Task TurboRequiresSupportAndAc()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake);
        SeedReadyState(fake, mode: 0, ac: false);
        fake.Published.Clear();
        var result = await controller.SetModeAsync(PerformanceMode.Turbo);
        Assert.Equal(ModeSwitchStatus.Rejected, result.Status, "Turbo without AC must be rejected");
        Assert.Equal(0, fake.Published.Count, "rejected Turbo must not publish");
        fake.Emit("Fan/Status", "{\"OperatingMode\":0,\"IsAC\":true,\"TurboModeSupport\":false}");
        result = await controller.SetModeAsync(PerformanceMode.Turbo);
        Assert.Equal(ModeSwitchStatus.Rejected, result.Status, "unsupported Turbo must be rejected");
        Assert.Equal(0, fake.Published.Count, "unsupported Turbo must not publish");
    }

    private static Task FanDutyNeverInventsRpm()
    {
        var observed = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        var snapshot = GcuMessageParser.Apply(new GcuSnapshot { IsConnected = true },
            "System/FanInfo", "{\"CpuFanDuty\":49,\"GpuFanDuty\":49}", observed, out _);

        Assert.Equal(49, snapshot.CpuFanDuty, "CPU duty should preserve the service control percentage");
        Assert.Equal(49, snapshot.GpuFanDuty, "GPU duty should preserve the service control percentage");
        Assert.True(snapshot.IsFanFresh(observed), "two fresh duty reports are usable control telemetry");
        Assert.Null(snapshot.CpuFanRpm, "CPU control percentage cannot establish actual RPM");
        Assert.Null(snapshot.GpuFanRpm, "GPU control percentage cannot establish actual RPM");
        Assert.Null(snapshot.CpuFanUpdatedAt, "duty cannot create an RPM observation timestamp");
        Assert.Null(snapshot.GpuFanUpdatedAt, "duty cannot create an RPM observation timestamp");
        Assert.False(snapshot.IsCpuFanFresh(observed), "CPU actual speed must remain unavailable");
        Assert.False(snapshot.IsGpuFanFresh(observed), "GPU actual speed must remain unavailable");
        return Task.CompletedTask;
    }

    private static Task FanDutyRejectsInvalidValues()
    {
        var observed = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        var snapshot = GcuMessageParser.Apply(new GcuSnapshot { IsConnected = true },
            "System/FanInfo", "{\"CpuFanDuty\":0,\"GpuFanDuty\":\"100\"}", observed, out _);
        Assert.Equal(0, snapshot.CpuFanDuty, "zero duty is a valid control value");
        Assert.Equal(100, snapshot.GpuFanDuty, "100 percent string duty is valid");
        Assert.True(snapshot.IsFanFresh(observed), "both inclusive duty boundaries should be fresh");

        var invalid = GcuMessageParser.Apply(snapshot, "System/FanInfo",
            "{\"CpuFanDuty\":-1,\"GpuFanDuty\":101}", observed.AddSeconds(1), out _);
        Assert.Null(invalid.CpuFanDuty, "negative duty must clear the earlier valid reading");
        Assert.Null(invalid.GpuFanDuty, "duty above 100 must clear the earlier valid reading");
        Assert.Null(invalid.CpuFanDutyUpdatedAt, "invalid CPU duty cannot keep a fresh timestamp");
        Assert.Null(invalid.GpuFanDutyUpdatedAt, "invalid GPU duty cannot keep a fresh timestamp");
        Assert.False(invalid.IsFanFresh(observed.AddSeconds(1)), "invalid duty must prevent ready fan state");

        invalid = GcuMessageParser.Apply(snapshot, "System/FanInfo",
            "{\"CpuFanDuty\":0.5,\"GpuFanDuty\":\"unavailable\"}", observed.AddSeconds(2), out _);
        Assert.Null(invalid.CpuFanDuty, "fractional duty must not be rounded to zero");
        Assert.Null(invalid.GpuFanDuty, "text duty must not become zero");
        var missing = GcuMessageParser.Apply(snapshot, "System/FanInfo", "{}", observed.AddSeconds(3), out _);
        Assert.Equal(snapshot, missing, "missing duty fields must preserve values without refreshing timestamps");
        return Task.CompletedTask;
    }

    private static Task FanDutyExpiresIndependently()
    {
        var observed = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        var snapshot = GcuMessageParser.Apply(new GcuSnapshot { IsConnected = true },
            "System/FanInfo", "{\"CpuFanDuty\":49,\"GpuFanDuty\":50,\"CpuFanRpm\":1000,\"GpuFanRpm\":1100}", observed, out _);
        var later = observed.Add(GcuSnapshot.Freshness).AddMilliseconds(1);
        snapshot = GcuMessageParser.Apply(snapshot, "System/FanInfo", "{\"CpuFanDuty\":51}", later, out _);
        Assert.True(snapshot.IsCpuFanDutyFresh(later), "CPU duty should refresh independently");
        Assert.False(snapshot.IsGpuFanDutyFresh(later), "CPU duty must not refresh missing GPU duty");
        Assert.False(snapshot.IsCpuFanFresh(later), "CPU duty must not refresh CPU actual speed");
        Assert.False(snapshot.IsGpuFanFresh(later), "CPU duty must not refresh GPU actual speed");
        Assert.False(snapshot.IsFanFresh(later), "stale GPU data must block combined fan readiness");
        Assert.Equal(observed, snapshot.GpuFanDutyUpdatedAt, "missing GPU duty retains its original observation time");

        snapshot = GcuMessageParser.Apply(snapshot, "System/FanInfo", "{\"GpuFanDuty\":52}", later, out _);
        Assert.True(snapshot.IsFanFresh(later), "fresh CPU and GPU duty restore readiness without RPM");
        Assert.False(snapshot.IsCpuFanFresh(later), "restoring readiness must not imply measured CPU speed");
        Assert.False(snapshot.IsGpuFanFresh(later), "restoring readiness must not imply measured GPU speed");
        return Task.CompletedTask;
    }

    private static async Task PlatformPowerBlocksTurbo(Func<bool?> unavailablePower, bool? expectedPower)
    {
        Func<bool?> readPower = () => true;
        await using var fake = new FakeTransport();
        await using var controller = new GcuController(fake, readAcPower: () => readPower());
        await controller.ConnectAsync(Options);
        SeedReadyState(fake, mode: 0, ac: true);
        Assert.True(controller.Snapshot.CanSwitchMode(PerformanceMode.Turbo), "fixture starts with supported turbo on AC");
        fake.Published.Clear();
        readPower = unavailablePower;

        var result = await controller.SetModeAsync(PerformanceMode.Turbo);

        Assert.Equal(ModeSwitchStatus.Rejected, result.Status, "latest platform power must gate turbo at click time");
        Assert.Equal(expectedPower, controller.Snapshot.IsAc, "earlier service AC report cannot override latest platform result");
        Assert.False(controller.Snapshot.CanSwitchMode(PerformanceMode.Turbo), "unavailable AC must leave turbo disabled");
        Assert.Equal(0, fake.Published.Count, "blocked turbo must send neither mode commands nor status queries");
        if (!expectedPower.HasValue)
            Assert.Null(controller.Snapshot.IsAcUpdatedAt, "failed or unknown power cannot retain a fresh AC timestamp");
    }

    private static Task ExplicitInvalidTelemetryClearsReadings()
    {
        var observed = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        var snapshot = new GcuSnapshot { IsConnected = true };
        snapshot = GcuMessageParser.Apply(snapshot, "Fan/Status", "{\"OperatingMode\":1,\"IsAC\":true,\"TurboModeSupport\":true}", observed, out _);
        snapshot = GcuMessageParser.Apply(snapshot, "System/FanInfo", "{\"CpuFanRpm\":1000,\"GpuFanRpm\":1100}", observed, out _);
        snapshot = GcuMessageParser.Apply(snapshot, "System/CpuInfo", "{\"CpuTemperature\":55}", observed, out _);
        snapshot = GcuMessageParser.Apply(snapshot, "System/GpuInfo", "{\"GpuTemperature\":60}", observed, out _);
        Assert.True(snapshot.CanSwitchMode(PerformanceMode.Turbo, observed), "fixture should initially allow turbo");

        var later = observed.AddSeconds(1);
        snapshot = GcuMessageParser.Apply(snapshot, "Fan/Status", "{\"OperatingMode\":-1,\"IsAC\":\"unavailable\",\"TurboModeSupport\":null}", later, out var modeReported);
        snapshot = GcuMessageParser.Apply(snapshot, "System/FanInfo", "{\"CpuFanRpm\":-1,\"GpuFanRpm\":20001}", later, out _);
        snapshot = GcuMessageParser.Apply(snapshot, "System/CpuInfo", "{\"CpuTemperature\":130}", later, out _);
        snapshot = GcuMessageParser.Apply(snapshot, "System/GpuInfo", "{\"GpuTemperature\":\"unavailable\"}", later, out _);

        Assert.False(modeReported, "invalid mode cannot count as confirming service readback");
        Assert.Null(snapshot.Mode, "invalid mode must clear earlier mode");
        Assert.Null(snapshot.ModeUpdatedAt, "invalid mode must clear earlier timestamp");
        Assert.Null(snapshot.IsAc, "invalid AC must clear earlier power state");
        Assert.Null(snapshot.IsAcUpdatedAt, "invalid AC must clear earlier timestamp");
        Assert.Null(snapshot.TurboSupported, "invalid capability must clear earlier support claim");
        Assert.Null(snapshot.TurboSupportedUpdatedAt, "invalid capability must clear earlier timestamp");
        Assert.Null(snapshot.CpuFanRpm, "invalid CPU RPM must clear earlier speed");
        Assert.Null(snapshot.CpuFanUpdatedAt, "invalid CPU RPM must clear earlier timestamp");
        Assert.Null(snapshot.GpuFanRpm, "invalid GPU RPM must clear earlier speed");
        Assert.Null(snapshot.GpuFanUpdatedAt, "invalid GPU RPM must clear earlier timestamp");
        Assert.Null(snapshot.CpuTemperature, "invalid CPU temperature must clear earlier reading");
        Assert.Null(snapshot.CpuTemperatureUpdatedAt, "invalid CPU temperature must clear earlier timestamp");
        Assert.Null(snapshot.GpuTemperature, "invalid GPU temperature must clear earlier reading");
        Assert.Null(snapshot.GpuTemperatureUpdatedAt, "invalid GPU temperature must clear earlier timestamp");
        Assert.False(snapshot.CanSwitchMode(PerformanceMode.Turbo, later), "invalid readings cannot keep a mode request enabled");
        return Task.CompletedTask;
    }

    private static async Task ServiceCannotOverridePlatformPower(bool? platformPower)
    {
        await using var fake = new FakeTransport();
        await using var controller = new GcuController(fake, readAcPower: () => platformPower);
        await controller.ConnectAsync(Options);
        var platformUpdatedAt = controller.Snapshot.IsAcUpdatedAt;
        fake.Emit("Fan/Status", "{\"OperatingMode\":\"0\",\"IsAC\":true}");
        fake.Emit("System/FanInfo", "{\"CpuFanDuty\":49,\"GpuFanDuty\":49}");

        Assert.Equal(platformPower, controller.Snapshot.IsAc, "incoming service AC cannot replace platform power");
        Assert.Equal(platformUpdatedAt, controller.Snapshot.IsAcUpdatedAt, "service AC cannot refresh the platform observation timestamp");
        Assert.False(controller.Snapshot.CanSwitchMode(PerformanceMode.Turbo), "service AC cannot enable the turbo button");
        fake.Published.Clear();
        var result = await controller.SetModeAsync(PerformanceMode.Turbo);
        Assert.Equal(ModeSwitchStatus.Rejected, result.Status, "turbo must remain rejected without known platform AC");
        Assert.Equal(0, fake.Published.Count, "blocked turbo must not publish any command");
    }

    private static async Task LegacyTelemetryUsesPlatformPower()
    {
        await using var fake = new FakeTransport();
        await using var controller = new GcuController(fake, readAcPower: () => true);
        await controller.ConnectAsync(Options);
        fake.Emit("Fan/Status", "{\"OperatingMode\":\"0\"}");
        fake.Emit("System/FanInfo", "{\"CpuFanDuty\":49,\"GpuFanDuty\":49}");
        Assert.Equal(0, controller.Snapshot.Mode, "legacy string mode should be parsed");
        Assert.Equal(true, controller.Snapshot.IsAc, "platform AC must fill the field absent from legacy service status");
        Assert.True(controller.Snapshot.CanSwitchMode(PerformanceMode.Turbo), "legacy control telemetry with platform AC should allow an explicit request");
        fake.Published.Clear();
        fake.OnPublish = (transport, topic, payload) =>
        {
            if (topic == "Fan/Control" && payload.Contains("GETSTATUS", StringComparison.Ordinal))
                transport.Emit("Fan/Status", "{\"OperatingMode\":\"2\"}");
            return Task.CompletedTask;
        };

        var result = await controller.SetModeAsync(PerformanceMode.Turbo);

        Assert.Equal(ModeSwitchStatus.ServiceConfirmed, result.Status, "legacy mode response should confirm the service state");
        Assert.Equal("Fan/Control", fake.Published[0].Topic, "legacy protocol still sends the primary command first");
        Assert.Equal("LCHWOC/Control", fake.Published[1].Topic, "legacy protocol still sends the secondary command second");
        using var primary = JsonDocument.Parse(fake.Published[0].Payload);
        Assert.Equal("OPERATING_TURBO_MODE", primary.RootElement.GetProperty("Action").GetString(), "turbo uses the official action");
        using var secondary = JsonDocument.Parse(fake.Published[1].Payload);
        Assert.Equal(2, secondary.RootElement.GetProperty("IsNormalRun").GetInt32(), "turbo uses the official numeric mode");
        Assert.Null(controller.Snapshot.CpuFanRpm, "service mode confirmation cannot invent CPU actual speed");
        Assert.Null(controller.Snapshot.GpuFanRpm, "service mode confirmation cannot invent GPU actual speed");
    }

    private static async Task SubscriptionFailureCleansUp()
    {
        await using var fake = new FakeTransport { ThrowOnSubscribe = true };
        await using var controller = new GcuController(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ConnectAsync(Options));
        Assert.False(fake.IsConnected, "failed subscription must disconnect transport");
        Assert.False(controller.Snapshot.IsConnected, "failed subscription must clear snapshot");
        Assert.True(fake.DisconnectCalls > 0, "failed subscription should clean up transport");
    }

    private static async Task MissingCredentialsRejected()
    {
        await using var fake = new FakeTransport();
        await using var controller = new GcuController(fake);
        await Assert.ThrowsAsync<ArgumentException>(() => controller.ConnectAsync(new GcuConnectionOptions("", "user", "password")));
        Assert.Equal(0, fake.ConnectCalls, "invalid credentials must not reach transport");
        await Assert.ThrowsAsync<ArgumentException>(() => controller.ConnectAsync(new GcuConnectionOptions("client", "", "password")));
        await Assert.ThrowsAsync<ArgumentException>(() => controller.ConnectAsync(new GcuConnectionOptions("client", "user", "")));
    }

    private static async Task ReconnectDoesNotDuplicateCallbacks()
    {
        await using var fake = new FakeTransport();
        await using var controller = new GcuController(fake);
        var changed = 0;
        controller.Changed += () => changed++;
        await controller.ConnectAsync(Options);
        var afterFirstConnect = changed;
        await controller.ConnectAsync(Options);
        Assert.Equal(afterFirstConnect, changed, "connecting an existing session must not duplicate callback");
        await controller.DisconnectAsync();
        await controller.ConnectAsync(Options);
        Assert.True(changed >= afterFirstConnect + 2, "a real reconnect should raise one disconnect and one connect notification");
        var beforeMessage = changed;
        fake.Emit("System/FanInfo", "{\"CpuFanRpm\":900}");
        Assert.Equal(beforeMessage + 1, changed, "one message should raise one callback");
    }

    private static async Task<GcuController> Connected(FakeTransport fake, TimeSpan? timeout = null, TimeSpan? poll = null)
    {
        var controller = new GcuController(fake, confirmationTimeout: timeout, confirmationPollInterval: poll);
        await controller.ConnectAsync(Options);
        return controller;
    }

    private static void SeedReadyState(FakeTransport fake, int mode, bool ac)
    {
        fake.Emit("Fan/Status", $"{{\"OperatingMode\":{mode},\"IsAC\":{ac.ToString().ToLowerInvariant()},\"TurboModeSupport\":true}}");
        fake.Emit("System/FanInfo", "{\"CpuFanRpm\":1000,\"GpuFanRpm\":1100}");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition()) return;
            await Task.Delay(1);
        }
        throw new Exception("condition was not reached in time");
    }
}

internal sealed class FakeTransport : IGcuTransport
{
    public sealed record Publication(string Topic, string Payload, bool AfterSubscribe);
    public bool IsConnected { get; private set; }
    public bool Subscribed { get; private set; }
    public bool RejectPublishBeforeSubscribe { get; init; }
    public bool ThrowOnSubscribe { get; init; }
    public int ThrowOnPublishNumber { get; init; }
    public string? ThrowOnPublishTopic { get; init; }
    public int ConnectCalls { get; private set; }
    public int DisconnectCalls { get; private set; }
    public List<Publication> Published { get; } = [];
    public Func<FakeTransport, string, string, Task>? OnPublish { get; set; }
    private int _publishNumber;
    public event Action<string, string>? MessageReceived;
    public event Action? Disconnected;

    public Task ConnectAsync(GcuConnectionOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ConnectCalls++;
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task SubscribeAsync(IReadOnlyCollection<string> topics, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ThrowOnSubscribe) throw new InvalidOperationException("subscribe failed");
        if (!IsConnected) throw new InvalidOperationException("not connected");
        Subscribed = true;
        return Task.CompletedTask;
    }

    public async Task PublishAsync(string topic, string payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConnected) throw new InvalidOperationException("not connected");
        if (RejectPublishBeforeSubscribe && !Subscribed) throw new InvalidOperationException("published before subscribe");
        Published.Add(new Publication(topic, payload, Subscribed));
        _publishNumber++;
        if ((ThrowOnPublishNumber > 0 && _publishNumber == ThrowOnPublishNumber) ||
            (ThrowOnPublishTopic is not null && string.Equals(topic, ThrowOnPublishTopic, StringComparison.Ordinal)))
            throw new InvalidOperationException("publish failed");
        if (OnPublish is not null) await OnPublish(this, topic, payload).ConfigureAwait(false);
    }
    public Task DisconnectAsync(CancellationToken ct)
    {
        DisconnectCalls++;
        IsConnected = false;
        Subscribed = false;
        return Task.CompletedTask;
    }

    public void Emit(string topic, string payload) => MessageReceived?.Invoke(topic, payload);
    public void DisconnectNow()
    {
        IsConnected = false;
        Subscribed = false;
        Disconnected?.Invoke();
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal static class Assert
{
    public static void True(bool value, string message) { if (!value) throw new Exception(message); }
    public static void False(bool value, string message) => True(!value, message);
    public static void Null(object? value, string message) { if (value is not null) throw new Exception($"{message}; actual {value}"); }
    public static void NotNull(object? value, string message) { if (value is null) throw new Exception(message); }
    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{message}; expected {expected}, actual {actual}");
    }
    public static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception($"expected {typeof(T).Name}");
    }
}
