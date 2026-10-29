using System.Collections.Concurrent;
using System.Text.Json;
using YALM.Mechrevo;

internal static partial class Program
{
    private static readonly GcuConnectionOptions Options = new("test-client", "test-user", "test-password")
    {
        TurboSupported = true
    };

    public static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Jiaolong requests contain only reviewed reads", JiaolongRequestsAreReadOnly),
            ("Jiaolong readings map and expire independently", JiaolongReadingsMapAndExpireIndependently),
            ("Jiaolong invalid replies stay unknown", JiaolongInvalidRepliesStayUnknown),
            ("Imini requests contain only reviewed reads", IminiRequestsAreReadOnly),
            ("Imini modes and numbered fans decode", IminiModesAndNumberedFansDecode),
            ("Imini invalid fields remain independent", IminiInvalidAndMissingFieldsStayIndependent),
            ("Imini temperature uses the reviewed low byte", IminiTemperatureUsesTheReviewedLowByte),
            ("Yilong requests contain only reviewed reads", YilongRequestsAreReadOnly),
            ("Yilong modes and field freshness", YilongModesAndFieldFreshness),
            ("Yilong invalid values stay unknown", YilongInvalidValuesStayUnknown),
            ("unreviewed platforms reject all writes", PlatformPolicyRejectsWritesDespiteFreshStatus),
            ("Mechrevo control center conflict detection", MechrevoControlConflictDetection),
            ("read-only platforms retain status refresh", PlatformPolicyKeepsReadOnlyRefreshes),
            ("platform permissions are independent", PlatformPolicyCapabilitiesRemainIndependent),
            ("reconnection replaces platform permissions", PlatformPolicyReplacesPermissionsOnReconnect),
            ("write permission is opt-in and session-scoped", WritePermissionIsOptInAndSessionScoped),
            ("revoking write permission blocks a secondary command", RevokingWritePermissionStopsFollowUpCommand),
            ("invalid readings never become zero", InvalidReadingsNeverBecomeZero),
            ("fields expire independently", FieldsExpireIndependently),
            ("EC fan readings decode RPM and protection", EcFanReadingsDecode),
            ("incomplete or impossible EC readings are rejected", InvalidEcReadingsRejected),
            ("EC register snapshots report completeness explicitly", EcRegisterSnapshotCompleteness),
            ("display route uses panel adapter and dGPU presence", GpuRouteInferenceUsesBothSignals),
            ("GPU targets parse independently of unrelated status", GpuTargetParsingIsIndependent),
            ("GPU target, support and actual route expire independently", GpuStateExpiresIndependently),
            ("GPU commands confirm both reboot target directions", GpuSwitchConfirmsBothDirections),
            ("GPU switching rejects unavailable and duplicate targets", GpuSwitchRejectsUnavailableState),
            ("GPU confirmation requires a new complete matching frame", GpuSwitchNeedsNewCompleteFrame),
            ("GPU busy clicks, cancellation and disconnect stay unconfirmed", GpuSwitchBusyAndCancellation),
            ("GPU publication failures and timeouts never resend the switch", GpuSwitchFailureDoesNotRetry),
            ("GPU driver power range is read without a fixed cap", GpuDriverPowerRangeParses),
            ("custom settings and fan table parse", CustomSettingsAndFanTableParse),
            ("real firmware end marker retains the last curve point", RealCurveTerminatorRetainsEffectivePoint),
            ("fan curve separates cooldown stop from coincident rising nodes", CurveDisplayUsesStopThreshold),
            ("profile 2 curve preserves its ten effective slots", Profile2CurveUsesActualSlots),
            ("curve slot validation rejects incomplete tails", CurveSlotValidationRejectsIncompleteTails),
            ("profile 2 curve validates all six tail values before writing", Profile2CurveValidatesAndConfirms),
            ("real keyboard status keeps null effect and case-sensitive power", RealKeyboardStatusParses),
            ("Fn lock is confirmed without fan telemetry", FnLockConfirmsIndependently),
            ("quick settings parse exact states and expire independently", QuickSettingsParseIndependently),
            ("quick settings confirm both directions with exact commands", QuickSettingsConfirmBothDirections),
            ("quick settings reject missing stale and duplicate targets", QuickSettingsRejectUnavailable),
            ("quick settings need their own matching status frame", QuickSettingsRequireMatchingField),
            ("quick settings serialize clicks and preserve unknown outcomes", QuickSettingsBusyFailureAndDisconnect),
            ("cached Fn lock cannot confirm an unrelated setting frame", CachedFnLockCannotConfirm),
            ("single-zone keyboard sends required fields and confirms", KeyboardEffectSendsRequiredFields),
            ("missing keyboard effect readback never claims success", MissingKeyboardEffectCannotConfirm),
            ("unsupported keyboard and invalid settings publish nothing", UnsupportedKeyboardRejectsWrites),
            ("keyboard power uses fresh case-sensitive readback", KeyboardPowerConfirms),
            ("keyboard power needs fresh matching single-zone state", KeyboardPowerNeedsFreshReadback),
            ("brightness-only writes preserve unknown six-color effect", KeyboardBrightnessPreservesManualEffect),
            ("brightness requires its own fresh matching readback", KeyboardBrightnessNeedsFreshReadback),
            ("brightness rejects unsupported or incomplete state", KeyboardBrightnessRejectsUnavailableState),
            ("six-color status preserves ordered values and unknown effect", KeyboardSequenceParsing),
            ("saved keyboard profiles need one exact unambiguous match", SavedKeyboardRequiresUniqueMatch),
            ("saved keyboard rejects malformed RGB buffers", SavedKeyboardRejectsIncompleteRgb),
            ("keyboard drafts preserve six colors across effect changes", KeyboardDraftPreservesColors),
            ("manual effects require complete fresh readback", ManualWriteRequiresCompleteReadback),
            ("custom RGB cannot be verified from an index alone", CustomRgbCannotClaimLiveConfirmation),
            ("stale keyboard drafts reject and brightness stays independent", KeyboardRejectsChangedDraft),
            ("keyboard reload waits for a fresh frame and only queries", KeyboardRefreshWaitsForNewFrame),
            ("lighting releases the operation lock without waiting for readback", KeyboardEffectsDoNotWaitForMissingReadback),
            ("lighting publication and status query failures stay distinct", KeyboardSendSeparatesPublicationFromQueryFailure),
            ("connect subscribes before publishing queries", ConnectSubscribesBeforePublishing),
            ("telemetry initializes once per connection", TelemetryInitializesOncePerConnection),
            ("disconnect clears all readings", DisconnectClearsReadings),
            ("mode command order and numeric profile index", ModeCommandOrder),
            ("old status cannot confirm a switch", OldStatusCannotConfirm),
            ("publish failure cannot report success", PublishFailureCannotReportSuccess),
            ("busy clicks do not duplicate commands", BusyClicksDoNotDuplicateCommands),
            ("custom profile uses numeric index and confirms", CustomProfileUsesNumericIndex),
            ("fan boost publishes one action and confirms", FanBoostPublishesAndConfirms),
            ("fan boost requires custom mode before publishing", FanBoostRequiresCustomMode),
            ("custom field validates range before publishing", CustomFieldRejectsOutOfRange),
            ("custom field issues one wire field and confirms", CustomFieldPublishesOneFieldAndConfirms),
            ("fan curve draft fills all five tail values", FanCurveDraftFillsTailValues),
            ("fan curve validates and confirms exact readback", FanCurveValidatesAndConfirms),
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

    private static Task EcRegisterSnapshotCompleteness()
    {
        var complete = new EcRegisterSnapshot
        {
            Registers = new Dictionary<int, byte> { [1] = 10, [2] = 20 },
            UpdatedAt = DateTimeOffset.UtcNow
        };
        Assert.True(complete.IsComplete([1, 2]), "all requested registers should be complete");
        Assert.False(complete.IsComplete([1, 3]), "a missing register must be reported as incomplete");

        var failed = complete with { LastError = "read failed" };
        Assert.False(failed.IsComplete([1, 2]), "an explicit read failure must be incomplete");
        return Task.CompletedTask;
    }

    private static Task GpuRouteInferenceUsesBothSignals()
    {
        Assert.Equal(
            InternalPanelAdapter.Integrated,
            GpuRouteInference.ClassifyAdapterPath(@"\\?\PCI#VEN_8086&DEV_A78B"),
            "Intel adapter path should be integrated");
        Assert.Equal(
            InternalPanelAdapter.Discrete,
            GpuRouteInference.ClassifyAdapterPath(@"\\?\PCI#VEN_10DE&DEV_28E0"),
            "NVIDIA adapter path should be discrete");
        Assert.Equal(
            GpuDisplayRoute.Direct,
            GpuRouteInference.Infer(InternalPanelAdapter.Discrete, DgpuPresence.Present),
            "discrete internal panel should mean direct route");
        Assert.Equal(
            GpuDisplayRoute.Hybrid,
            GpuRouteInference.Infer(InternalPanelAdapter.Integrated, DgpuPresence.Present),
            "integrated panel with an active dGPU should mean hybrid route");
        Assert.Equal(
            GpuDisplayRoute.Unknown,
            GpuRouteInference.Infer(InternalPanelAdapter.Integrated, DgpuPresence.Unknown),
            "unknown dGPU presence cannot confirm hybrid route");
        return Task.CompletedTask;
    }

    private static Task GpuDriverPowerRangeParses()
    {
        Assert.True(GpuDriverPower.TryParse(
            "NVIDIA GeForce RTX 4060 Laptop GPU, 14.14, N/A, 5, 140, 115",
            out GpuDriverPower? power, out string error), error);
        Assert.Equal(5d, power!.MinimumLimit, "minimum driver limit should be read");
        Assert.Equal(140d, power.MaximumLimit, "maximum driver limit should be read");
        Assert.Equal(115d, power.DefaultLimit, "default driver limit should be read");
        Assert.Null(power.CurrentLimit, "N/A must remain unknown instead of becoming zero");
        return Task.CompletedTask;
    }

    private static Task CustomSettingsAndFanTableParse()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = GcuMessageParser.Apply(new GcuSnapshot { IsConnected = true },
            "Fan/Status",
            "{\"OperatingMode\":3,\"CustomProfileIndex\":1,\"ProfileName\":\"Mode4_Profile2\"," +
            "\"CPU_PL1\":75,\"CPU_PL1Minimum\":10,\"CPU_PL1Maximum\":130," +
            "\"CPU_PL2\":75,\"CPU_PL2Minimum\":10,\"CPU_PL2Maximum\":130," +
            "\"CPU_PL4\":200,\"CPU_PL4Minimum\":10,\"CPU_PL4Maximum\":200," +
            "\"CPU_TccOffset\":5,\"CPU_TccOffsetSwitch\":0,\"FAN_FanSwitchSpeed\":300," +
            "\"FAN_FanSwitchSpeedEnabled\":0,\"FAN_TableName\":\"M4T2\",\"FanBoostEnable\":0}",
            now, out _, out bool customReported, out _);

        Assert.True(customReported, "known custom fields should be reported");
        Assert.Equal(1, snapshot.CustomSettings?.ProfileIndex, "custom profile index should parse");
        Assert.Equal("Mode4_Profile2", snapshot.CustomSettings?.ProfileName, "profile name should parse");
        Assert.Equal(95, snapshot.CustomSettings?.TccTarget, "TCC target should be derived from TjMax");
        Assert.True(snapshot.IsCustomSettingsFresh(now), "custom settings should be fresh");

        snapshot = GcuMessageParser.Apply(snapshot, "Fan/Table", CurveTableJson(true, false, CpuDuty()), now,
            out _, out _, out bool tableReported);

        Assert.True(tableReported, "fan table should be reported");
        Assert.Equal("M4T2", snapshot.FanTable?.Name, "fan table name should parse");
        Assert.Equal(16, snapshot.FanTable?.Cpu.Count, "CPU curve should retain all slots");
        Assert.Equal(16, snapshot.FanTable?.Gpu.Count, "GPU curve should retain all slots");
        Assert.True(snapshot.IsFanTableFresh(now), "fan table should be fresh");
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
        Assert.Equal(6, refreshPublications.Length, "each refresh should query fan, settings and keyboard status");
        Assert.Equal(2, refreshPublications.Count(x => x.Topic == "Fan/Control"), "each refresh should query fan status once");
        Assert.Equal(2, refreshPublications.Count(x => x.Topic == "Setting/Control"), "each refresh should query settings once");
        Assert.Equal(2, refreshPublications.Count(x => x.Topic == "Keyboard/Ctrl"), "each refresh should query keyboard once");
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

    private static async Task CustomProfileUsesNumericIndex()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake);
        SeedCustomState(fake, profile: 0);
        fake.Published.Clear();
        fake.OnPublish = (transport, topic, payload) =>
        {
            if (topic == "Fan/Control" && payload.Contains("GETSTATUS", StringComparison.Ordinal))
                SeedCustomState(transport, profile: 1);
            return Task.CompletedTask;
        };

        var result = await controller.SetCustomProfileAsync(1);

        Assert.Equal(GcuWriteStatus.Confirmed, result.Status, "fresh custom profile readback should confirm");
        using var command = JsonDocument.Parse(fake.Published[0].Payload);
        Assert.Equal("OPERATING_CUSTOM_MODE", command.RootElement.GetProperty("Action").GetString(), "custom action");
        Assert.Equal(1, command.RootElement.GetProperty("ProfileIndex").GetInt32(), "profile index must be numeric");
        Assert.Equal("LCHWOC/Control", fake.Published[1].Topic, "custom run flag follows mode request");
    }

    private static async Task FanBoostPublishesAndConfirms()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake);
        SeedCustomState(fake, profile: 1, fanBoost: false);
        fake.Published.Clear();
        fake.OnPublish = (transport, topic, payload) =>
        {
            if (topic == "Fan/Control" && payload.Contains("GETSTATUS", StringComparison.Ordinal))
                SeedCustomState(transport, profile: 1, fanBoost: true);
            return Task.CompletedTask;
        };

        var result = await controller.SetFanBoostAsync(true);

        Assert.Equal(GcuWriteStatus.Confirmed, result.Status, "fan boost readback should confirm");
        Assert.Equal(1, fake.Published.Count(x => x.Payload.Contains("FAN_BOOST_ON", StringComparison.Ordinal)),
            "only one fan boost command should be published");
    }

    private static async Task FanBoostRequiresCustomMode()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake);
        SeedReadyState(fake, mode: 1, ac: true);
        fake.Published.Clear();

        var result = await controller.SetFanBoostAsync(true);

        Assert.Equal(GcuWriteStatus.Rejected, result.Status, "fan boost outside custom mode must be rejected");
        Assert.Equal(0, fake.Published.Count, "rejected fan boost must not publish");
    }

    private static async Task CustomFieldRejectsOutOfRange()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake);
        SeedCustomState(fake, profile: 1);
        fake.Published.Clear();

        var result = await controller.SetCustomFieldAsync(GcuCustomWriteField.Pl1, 500);

        Assert.Equal(GcuWriteStatus.Rejected, result.Status, "out-of-range PL1 must be rejected");
        Assert.Equal(0, fake.Published.Count, "rejected field must not publish");
    }

    private static async Task CustomFieldPublishesOneFieldAndConfirms()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake);
        SeedCustomState(fake, profile: 1, pl1: 75);
        fake.Published.Clear();
        fake.OnPublish = (transport, topic, payload) =>
        {
            if (topic == "Fan/Control" && payload.Contains("GETSTATUS", StringComparison.Ordinal))
                SeedCustomState(transport, profile: 1, pl1: 80);
            return Task.CompletedTask;
        };

        var result = await controller.SetCustomFieldAsync(GcuCustomWriteField.Pl1, 80);

        Assert.Equal(GcuWriteStatus.Confirmed, result.Status, "fresh PL1 readback should confirm");
        using var command = JsonDocument.Parse(fake.Published[0].Payload);
        Assert.Equal("SET_OPERATING_MODE_DETAIL", command.RootElement.GetProperty("Action").GetString(), "single field action");
        Assert.Equal("80", command.RootElement.GetProperty("PL1").GetString(), "single PL1 value should be a string");
        Assert.Equal(2, command.RootElement.EnumerateObject().Count(), "payload must contain only action and one field");
    }

    private static async Task FanCurveValidatesAndConfirms()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake);
        SeedCustomState(fake, profile: 1);
        int[] original = CpuDuty();
        int[] changed = original.ToArray();
        changed[10] = changed[11] = changed[12] = changed[13] = changed[14] = changed[15] = 90;
        fake.Emit("Fan/Table", CurveTableJson(true, false, original));
        Assert.True(controller.Snapshot.IsFanTableFresh(), "fixture fan table should be fresh");
        fake.Published.Clear();
        fake.OnPublish = (transport, topic, payload) =>
        {
            if (topic == "Fan/Control" && payload.Contains("GET_FAN_SPEED_CURVE_SETTING", StringComparison.Ordinal))
                transport.Emit("Fan/Table", CurveTableJson(true, false, changed));
            return Task.CompletedTask;
        };

        var rejected = await controller.SetFanCurveAsync(FanCurveTarget.Cpu, new int[15]);
        Assert.Equal(GcuWriteStatus.Rejected, rejected.Status, "short curves must be rejected");
        int[] inconsistentTail = changed.ToArray();
        inconsistentTail[15] = 91;
        rejected = await controller.SetFanCurveAsync(FanCurveTarget.Cpu, inconsistentTail);
        Assert.Equal(GcuWriteStatus.Rejected, rejected.Status, "inconsistent tail must be rejected");
        Assert.Equal(0, fake.Published.Count, "invalid curves must not publish");
        var result = await controller.SetFanCurveAsync(FanCurveTarget.Cpu, changed);

        Assert.Equal(GcuWriteStatus.Confirmed, result.Status, "exact curve readback should confirm");
        using var command = JsonDocument.Parse(fake.Published[0].Payload);
        Assert.Equal("SET_FAN_SPEED_CURVE_SETTING", command.RootElement.GetProperty("Action").GetString(), "curve action");
        Assert.Equal("CPU", command.RootElement.GetProperty("Type").GetString(), "curve target");
        Assert.Equal("90", command.RootElement.GetProperty("T15").GetString(), "all 16 duties must be sent as strings");
    }

    private static Task FanCurveDraftFillsTailValues()
    {
        int[] editable = [0, 30, 30, 35, 45, 55, 60, 70, 80, 90, 91];
        Assert.True(FanCurveDraft.TryBuild(editable, out int[] duties, out string error), error);
        Assert.Equal(16, duties.Length, "draft should contain all sixteen service slots");
        Assert.Equal(91, duties[10], "last editable point should be retained");
        Assert.True(duties.Skip(11).All(value => value == 91),
            "all five tail slots should follow the last editable point");
        return Task.CompletedTask;
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

    private static void SeedCustomState(
        FakeTransport fake,
        int profile,
        int pl1 = 75,
        bool fanBoost = false)
    {
        string message =
            $"{{\"OperatingMode\":3,\"CustomProfileIndex\":{profile}," +
            $"\"ProfileName\":\"Mode4_Profile{profile + 1}\",\"IsAC\":true,\"TurboModeSupport\":true," +
            "\"CPU_PL1\":75,\"CPU_PL1Minimum\":10,\"CPU_PL1Maximum\":130," +
            "\"CPU_PL2\":75,\"CPU_PL2Minimum\":10,\"CPU_PL2Maximum\":130," +
            "\"CPU_PL4\":200,\"CPU_PL4Minimum\":10,\"CPU_PL4Maximum\":200," +
            "\"CPU_TccOffset\":5,\"CPU_TccOffsetSwitch\":0," +
            "\"FAN_FanSwitchSpeed\":300,\"FAN_FanSwitchSpeedEnabled\":0," +
            $"\"FAN_TableName\":\"M4T2\",\"FanBoostEnable\":{(fanBoost ? 1 : 0)}}}";
        fake.Emit("Fan/Status",
            message.Replace("\"CPU_PL1\":75", $"\"CPU_PL1\":{pl1}", StringComparison.Ordinal));
        fake.Emit("System/FanInfo", "{\"CpuFanRpm\":1000,\"GpuFanRpm\":1100}");
    }

    private static int[] CpuDuty() =>
        [0, 30, 30, 35, 45, 55, 60, 70, 80, 90, 95, 95, 95, 95, 95, 95];

    private static string CurveTableJson(bool activated, bool respective, IReadOnlyList<int> cpuDuty)
    {
        string cpu = CurveJson(cpuDuty);
        string gpu = CurveJson(cpuDuty);
        return $"{{\"Activated\":{activated.ToString().ToLowerInvariant()}," +
            "\"Name\":\"M4T2\"," +
            $"\"FanControlRespective\":{respective.ToString().ToLowerInvariant()}," +
            "\"CpuTemp_DefaultMaxLevel\":11,\"GpuTemp_DefaultMaxLevel\":11," +
            $"\"CPU\":[{cpu}],\"GPU\":[{gpu}]}}";
    }

    private static string CurveJson(IReadOnlyList<int> duties)
    {
        return string.Join(",", Enumerable.Range(0, 16).Select(index =>
        {
            int temperature = index < 11 ? 50 + index : 255;
            return $"{{\"ID\":{index},\"UpT\":{temperature},\"DownT\":{temperature},\"Duty\":{duties[index]}}}";
        }));
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
