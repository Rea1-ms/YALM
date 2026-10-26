using System.Text.Json;
using YALM.Mechrevo;

internal static partial class Program
{
    private static readonly (GcuQuickSetting Setting, string Field, string On, string Off, string OnReport, string OffReport)[] QuickCases =
    [
        (GcuQuickSetting.WinLock, "WinKey", "WINKEY_LOCK", "WINKEY_UNLOCK", "WINKEY_STATUS_LOCK", "WINKEY_STATUS_UNLOCK"),
        (GcuQuickSetting.OsdVisible, "OSD", "OSD_HIDDEN_OFF", "OSD_HIDDEN_ON", "OSD_HIDDEN_OFF", "OSD_HIDDEN_ON"),
        (GcuQuickSetting.NumPadLock, "NumPad", "NUMPAD_LOCK", "NUMPAD_UNLOCK", "NUMPAD_LOCK", "NUMPAD_UNLOCK"),
        (GcuQuickSetting.TouchpadHotkey, "TouchpadToggle", "TOUCHPAD_TOGGLE_ON", "TOUCHPAD_TOGGLE_OFF", "TOUCHPAD_TOGGLE_ON", "TOUCHPAD_TOGGLE_OFF"),
        (GcuQuickSetting.UsbCharging, "UsbCharger", "USB_CHARGER_ON", "USB_CHARGER_OFF", "USB_CHARGER_STATUS_ON", "USB_CHARGER_STATUS_OFF"),
        (GcuQuickSetting.AcRecovery, "AcRecoverySwitch_Status", "ACRECOVERY_TOGGLE_ON", "ACRECOVERY_TOGGLE_OFF", "ACRECOVERY_TOGGLE_ON", "ACRECOVERY_TOGGLE_OFF")
    ];

    private static string QuickFrame(string field, string value) => JsonSerializer.Serialize(new Dictionary<string, string> { [field] = value });

    private static Task QuickSettingsParseIndependently()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GcuSnapshot snapshot = new() { IsConnected = true };
        foreach (var item in QuickCases)
        {
            snapshot = GcuMessageParser.Apply(snapshot, "Setting/Status", QuickFrame(item.Field, item.OnReport), now, out _);
            Assert.True(snapshot.QuickSetting(item.Setting).Enabled == true, "recognized enabled status: " + item.Field);
            foreach (string alias in new[] { item.OffReport, item.Off })
            {
                var off = GcuMessageParser.Apply(snapshot, "Setting/Status", QuickFrame(item.Field, alias), now, out _);
                Assert.True(off.QuickSetting(item.Setting).Enabled == false, "recognized disabled status: " + item.Field);
            }
        }
        snapshot = GcuMessageParser.Apply(snapshot, "Setting/Status", "{\"FnKey\":\"FNKEY_LOCK\"}", now.AddSeconds(5), out _);
        foreach (var item in QuickCases)
        {
            Assert.Equal(now, snapshot.QuickSetting(item.Setting).UpdatedAt, "other status fields must not refresh this setting");
            Assert.False(snapshot.QuickSetting(item.Setting).IsFresh(now.AddSeconds(11)), "settings expire independently");
            Assert.False(snapshot.QuickSetting(item.Setting).IsFresh(now.AddSeconds(-1)), "future timestamps are not fresh");
            var wrongTopic = GcuMessageParser.Apply(snapshot, "Keyboard/Status", QuickFrame(item.Field, item.OffReport), now, out _);
            Assert.True(wrongTopic.QuickSetting(item.Setting).Enabled == true, "keyboard frames cannot change quick settings");
            foreach (string invalid in new[] { "null", "1", "true", "\"unknown\"", "\"UNSUPPORTED_ON\"", "\"\"" })
            {
                var unknown = GcuMessageParser.Apply(snapshot, "Setting/Status", "{\"" + item.Field + "\":" + invalid + "}", now, out _);
                Assert.Null(unknown.QuickSetting(item.Setting).Enabled, "invalid status must clear the old value");
                Assert.Null(unknown.QuickSetting(item.Setting).UpdatedAt, "invalid status must clear freshness");
            }
        }
        return Task.CompletedTask;
    }

    private static async Task QuickSettingsConfirmBothDirections()
    {
        foreach (var item in QuickCases)
        {
            await using var fake = new FakeTransport();
            await using var controller = await Connected(fake);
            foreach (bool enabled in new[] { true, false })
            {
                fake.Emit("Setting/Status", QuickFrame(item.Field, enabled ? item.OffReport : item.OnReport));
                fake.Published.Clear();
                fake.OnPublish = (transport, topic, payload) =>
                {
                    if (topic == "Setting/Control" && payload.Contains("GETSTATUS"))
                        transport.Emit("Setting/Status", QuickFrame(item.Field, enabled ? item.OnReport : item.OffReport));
                    return Task.CompletedTask;
                };
                Assert.True((await controller.SetQuickSettingAsync(item.Setting, enabled)).Confirmed, "both directions confirm without fan telemetry");
                var writes = fake.Published.Where(publication => !publication.Payload.Contains("GETSTATUS")).ToArray();
                Assert.Equal(1, writes.Length, "each toggle publishes once");
                Assert.Equal("Setting/Control", writes[0].Topic, "quick settings use the settings channel");
                using var command = JsonDocument.Parse(writes[0].Payload);
                Assert.Equal(enabled ? item.On : item.Off, command.RootElement.GetProperty("Action").GetString(), "exact action and OSD inversion");
                Assert.Equal(1, command.RootElement.EnumerateObject().Count(), "no unrelated settings in the command");
                Assert.True(controller.Snapshot.QuickSetting(item.Setting).Enabled == enabled, "readback updates the observed state");
            }
        }
    }

    private static async Task QuickSettingsRejectUnavailable()
    {
        await using var fake = new FakeTransport();
        var clock = new QuickSettingsClock();
        await using var controller = new GcuController(fake, clock);
        await controller.ConnectAsync(Options);
        fake.Published.Clear();
        foreach (var item in QuickCases)
        {
            Assert.Equal(GcuWriteStatus.Rejected, (await controller.SetQuickSettingAsync(item.Setting, true)).Status, "missing state rejects writes");
            fake.Emit("Setting/Status", QuickFrame(item.Field, item.OffReport));
            Assert.Equal(GcuWriteStatus.Rejected, (await controller.SetQuickSettingAsync(item.Setting, false)).Status, "same target is not resent");
        }
        clock.UtcNow += TimeSpan.FromSeconds(11);
        foreach (var item in QuickCases)
            Assert.Equal(GcuWriteStatus.Rejected, (await controller.SetQuickSettingAsync(item.Setting, true)).Status, "expired state rejects writes");
        Assert.Equal(GcuWriteStatus.Rejected, (await controller.SetQuickSettingAsync((GcuQuickSetting)999, true)).Status, "unknown setting rejects writes");
        Assert.Equal(0, fake.Published.Count, "invalid operations send no command or query");
    }

    private static async Task QuickSettingsRequireMatchingField()
    {
        foreach (var item in QuickCases)
        {
            await using var fake = new FakeTransport();
            await using var controller = await Connected(fake, TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(2));
            fake.Emit("Setting/Status", QuickFrame(item.Field, item.OffReport));
            fake.Published.Clear();
            fake.OnPublish = (transport, topic, payload) =>
            {
                if (payload.Contains("GETSTATUS"))
                {
                    transport.Emit("Setting/Status", "{\"FnKey\":\"FNKEY_LOCK\"}");
                    transport.Emit("Keyboard/Status", QuickFrame(item.Field, item.OnReport));
                }
                return Task.CompletedTask;
            };
            Assert.Equal(GcuWriteStatus.TimedOut, (await controller.SetQuickSettingAsync(item.Setting, true)).Status,
                "other fields and wrong topic do not confirm this setting");
            Assert.Equal(1, fake.Published.Count(publication => !publication.Payload.Contains("GETSTATUS")), "timeout never retries the toggle");
        }
    }

    private static async Task QuickSettingsBusyFailureAndDisconnect()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake, TimeSpan.FromMilliseconds(35), TimeSpan.FromMilliseconds(2));
        fake.Emit("Setting/Status", QuickFrame("WinKey", "WINKEY_STATUS_UNLOCK"));
        fake.Published.Clear();
        Task<GcuWriteResult> pending = controller.SetQuickSettingAsync(GcuQuickSetting.WinLock, true);
        Assert.Equal(GcuWriteStatus.Busy, (await controller.SetQuickSettingAsync(GcuQuickSetting.WinLock, true)).Status, "pending toggles are serialized");
        await pending;
        Assert.Equal(1, fake.Published.Count(publication => !publication.Payload.Contains("GETSTATUS")), "busy click cannot duplicate a command");
        fake.OnPublish = (_, _, payload) => payload.Contains("WINKEY_LOCK") ? throw new InvalidOperationException("offline failure") : Task.CompletedTask;
        Assert.Equal(GcuWriteStatus.Failed, (await controller.SetQuickSettingAsync(GcuQuickSetting.WinLock, true)).Status, "failed publication is not success");
        Assert.True(controller.Snapshot.QuickSetting(GcuQuickSetting.WinLock).Enabled == false, "failure preserves observed state");
        fake.OnPublish = null;
        await controller.DisconnectAsync();
        Assert.False(controller.Snapshot.QuickSetting(GcuQuickSetting.WinLock).IsFresh(), "disconnect clears quick settings");
    }

    private sealed class QuickSettingsClock : TimeProvider
    {
        public DateTimeOffset UtcNow = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
