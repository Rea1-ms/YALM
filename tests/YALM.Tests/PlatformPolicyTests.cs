using YALM.Mechrevo;

internal static partial class Program
{
    private static GcuConnectionOptions ReadOnlyPlatformOptions => Options with
    {
        ModeControlSupported = false,
        CustomControlSupported = false,
        PeripheralControlSupported = false
    };

    private static async Task PlatformPolicyRejectsWritesDespiteFreshStatus()
    {
        await using var fake = new FakeTransport();
        await using var controller = new GcuController(fake, confirmationTimeout: TimeSpan.FromMilliseconds(100),
            confirmationPollInterval: TimeSpan.FromMilliseconds(2));
        await controller.ConnectAsync(ReadOnlyPlatformOptions);
        SeedCustomState(fake, profile: 0);
        fake.Emit("Fan/Table", CurveTableJson(true, false, CpuDuty()));
        SeedKeyboard(fake);
        fake.Emit("Setting/Status", "{\"FnKey\":\"FNKEY_UNLOCK\",\"WinKey\":\"WINKEY_UNLOCK\"}");
        fake.Emit("Setting/Status", GpuStatus(GpuDisplayRoute.Direct));
        fake.Published.Clear();

        foreach (PerformanceMode mode in Enum.GetValues<PerformanceMode>())
            Assert.Equal(ModeSwitchStatus.Rejected, (await controller.SetModeAsync(mode)).Status,
                "fresh status cannot grant an unreviewed performance command");
        var writes = new Func<Task<GcuWriteResult>>[]
        {
            () => controller.SetCustomProfileAsync(1),
            () => controller.SetFanBoostAsync(true),
            () => controller.SetCustomFieldAsync(GcuCustomWriteField.Pl1, 80),
            () => controller.SetCustomFieldAsync(GcuCustomWriteField.TccEnabled, 1),
            () => controller.SetCustomFieldAsync(GcuCustomWriteField.FanSwitchSpeedEnabled, 1),
            () => controller.SetFanCurveAsync(FanCurveTarget.Cpu, CpuDuty()),
            () => controller.SetFnLockAsync(true),
            () => controller.SetQuickSettingAsync(GcuQuickSetting.WinLock, true),
            () => controller.SetKeyboardPowerAsync(false),
            () => controller.SetKeyboardBrightnessAsync(2),
            () => controller.SetKeyboardEffectAsync(KeyboardDraft(controller, "Rainbow")),
            () => controller.SetGpuTargetAsync(GpuDisplayRoute.Hybrid)
        };
        foreach (Func<Task<GcuWriteResult>> write in writes)
        {
            Assert.Equal(GcuWriteStatus.Rejected, (await write()).Status,
                "valid current values do not override the platform write policy");
            Assert.Equal(0, fake.Published.Count, "blocked operations publish neither writes nor follow-up queries");
        }
    }

    private static async Task PlatformPolicyKeepsReadOnlyRefreshes()
    {
        await using var fake = new FakeTransport();
        await using var controller = new GcuController(fake, confirmationTimeout: TimeSpan.FromMilliseconds(100),
            confirmationPollInterval: TimeSpan.FromMilliseconds(2));
        await controller.ConnectAsync(ReadOnlyPlatformOptions);
        fake.Published.Clear();
        fake.OnPublish = (transport, topic, payload) =>
        {
            if (topic == "Fan/Control" && payload.Contains("GETSTATUS")) SeedCustomState(transport, profile: 0);
            if (topic == "Fan/Control" && payload.Contains("GET_FAN_SPEED_CURVE_SETTING"))
                transport.Emit("Fan/Table", CurveTableJson(true, false, CpuDuty()));
            if (topic == "Keyboard/Ctrl" && payload.Contains("GETSTATUS")) SeedKeyboard(transport);
            return Task.CompletedTask;
        };

        Assert.True((await controller.RefreshCustomAsync()).Confirmed, "custom status stays readable without write permission");
        Assert.True((await controller.RefreshKeyboardAsync()).Confirmed, "keyboard status stays readable without write permission");
        await controller.RefreshAsync();
        Assert.True(fake.Published.Count > 0, "read-only refreshes must actually query the service");
        Assert.True(fake.Published.All(publication => publication.Payload.Contains("GETSTATUS") ||
            publication.Payload.Contains("GET_FAN_SPEED_CURVE_SETTING")), "read-only refreshes cannot send hardware changes");
    }

    private static async Task PlatformPolicyCapabilitiesRemainIndependent()
    {
        foreach (string allowed in new[] { "mode", "custom", "peripheral" })
        {
            await using var fake = new FakeTransport();
            await using var controller = new GcuController(fake, confirmationTimeout: TimeSpan.FromMilliseconds(100),
                confirmationPollInterval: TimeSpan.FromMilliseconds(2));
            await controller.ConnectAsync(ReadOnlyPlatformOptions with
            {
                ModeControlSupported = allowed == "mode",
                CustomControlSupported = allowed == "custom",
                PeripheralControlSupported = allowed == "peripheral"
            });
            SeedCustomState(fake, profile: 0);
            fake.Emit("Setting/Status", "{\"FnKey\":\"FNKEY_UNLOCK\"}");
            fake.OnPublish = (transport, topic, payload) =>
            {
                if (topic == "Fan/Control" && payload.Contains("GETSTATUS"))
                {
                    if (allowed == "mode") SeedReadyState(transport, mode: 0, ac: true);
                    else SeedCustomState(transport, profile: 1);
                }
                if (topic == "Setting/Control" && payload.Contains("GETSTATUS"))
                    transport.Emit("Setting/Status", "{\"FnKey\":\"FNKEY_LOCK\"}");
                return Task.CompletedTask;
            };

            Assert.Equal(allowed == "mode" ? ModeSwitchStatus.ServiceConfirmed : ModeSwitchStatus.Rejected,
                (await controller.SetModeAsync(PerformanceMode.Silent)).Status, "mode permission is independent");
            Assert.Equal(allowed == "custom" ? GcuWriteStatus.Confirmed : GcuWriteStatus.Rejected,
                (await controller.SetCustomProfileAsync(1)).Status, "custom permission is independent");
            Assert.Equal(allowed == "peripheral" ? GcuWriteStatus.Confirmed : GcuWriteStatus.Rejected,
                (await controller.SetFnLockAsync(true)).Status, "peripheral permission is independent");
        }
    }

    private static async Task PlatformPolicyReplacesPermissionsOnReconnect()
    {
        await using var fake = new FakeTransport();
        await using var controller = new GcuController(fake, confirmationTimeout: TimeSpan.FromMilliseconds(100),
            confirmationPollInterval: TimeSpan.FromMilliseconds(2));
        foreach (var (allowed, bridgeKnown) in new[] { (false, true), (true, false), (false, false), (true, true) })
        {
            fake.OnPublish = null;
            await controller.ConnectAsync((allowed ? Options : ReadOnlyPlatformOptions) with { BridgeVersionKnown = bridgeKnown });
            SeedCustomState(fake, profile: 0);
            fake.Emit("Setting/Status", "{\"FnKey\":\"FNKEY_UNLOCK\"}");
            fake.Published.Clear();
            fake.OnPublish = (transport, topic, payload) =>
            {
                if (topic == "Fan/Control" && payload.Contains("GETSTATUS"))
                {
                    if (fake.Published.Any(item => item.Payload.Contains("OPERATING_CUSTOM_MODE")))
                        SeedCustomState(transport, profile: 1);
                    else SeedReadyState(transport, mode: 0, ac: true);
                }
                if (topic == "Setting/Control" && payload.Contains("GETSTATUS"))
                    transport.Emit("Setting/Status", "{\"FnKey\":\"FNKEY_LOCK\"}");
                return Task.CompletedTask;
            };
            Assert.Equal(allowed ? ModeSwitchStatus.ServiceConfirmed : ModeSwitchStatus.Rejected,
                (await controller.SetModeAsync(PerformanceMode.Silent)).Status, "reconnect replaces mode permission");
            Assert.Equal(allowed ? GcuWriteStatus.Confirmed : GcuWriteStatus.Rejected,
                (await controller.SetCustomProfileAsync(1)).Status, "reconnect replaces custom permission");
            Assert.Equal(allowed ? GcuWriteStatus.Confirmed : GcuWriteStatus.Rejected,
                (await controller.SetFnLockAsync(true)).Status, "reconnect replaces peripheral permission");
            if (!allowed) Assert.Equal(0, fake.Published.Count, "earlier permission cannot survive a restricted reconnect");
            await controller.DisconnectAsync();
        }
    }
}
