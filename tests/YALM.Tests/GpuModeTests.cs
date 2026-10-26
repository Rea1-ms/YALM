using System.Text.Json;
using YALM.Mechrevo;

internal static partial class Program
{
    private static string GpuStatus(GpuDisplayRoute target, string support = "Support") => JsonSerializer.Serialize(new
    {
        DiscreteGpuDirectConnectionSwitch_Status = target == GpuDisplayRoute.Direct
            ? "DGPU_DIRECT_CONNECT_TOGGLE_ON" : "DGPU_DIRECT_CONNECT_TOGGLE_OFF",
        DiscreteGpuDirectConnectionSwitch_Support = support
    });

    private static Task GpuTargetParsingIsIndependent()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GcuSnapshot snapshot = GcuMessageParser.Apply(new() { IsConnected = true }, "Setting/Status",
            GpuStatus(GpuDisplayRoute.Direct), now, out _);
        Assert.Equal(GpuDisplayRoute.Direct, snapshot.GpuTarget, "ON saves direct as the reboot target");
        Assert.True(snapshot.CanSetGpuTarget(GpuDisplayRoute.Hybrid, now), "GPU switching does not depend on fan telemetry");
        snapshot = GcuMessageParser.Apply(snapshot, "Setting/Status",
            "{\"FnKey\":\"FNKEY_LOCK\",\"DGpu\":\"NV_CTRL_PANEL_AUTOSELECT\",\"IGpuOnlyConnectionSwitch_Status\":\"ON\"}", now.AddSeconds(2), out _);
        Assert.Equal(now, snapshot.GpuTargetUpdatedAt, "unrelated fields do not renew the target");
        Assert.Equal(now, snapshot.GpuSwitchSupportUpdatedAt, "support has its own timestamp");
        snapshot = GcuMessageParser.Apply(snapshot, "Fan/Status", GpuStatus(GpuDisplayRoute.Hybrid), now, out _);
        Assert.Equal(GpuDisplayRoute.Direct, snapshot.GpuTarget, "wrong topic cannot overwrite the target");
        snapshot = GcuMessageParser.Apply(snapshot, "Setting/Status", GpuStatus(GpuDisplayRoute.Hybrid), now, out _);
        Assert.Equal(GpuDisplayRoute.Hybrid, snapshot.GpuTarget, "OFF saves hybrid");
        foreach (string invalid in new[] { "null", "1", "true", "\"ON\"", "\"dgpu_direct_connect_toggle_on\"" })
        {
            var bad = GcuMessageParser.Apply(snapshot, "Setting/Status",
                "{\"DiscreteGpuDirectConnectionSwitch_Status\":" + invalid + "}", now, out _);
            Assert.Null(bad.GpuTarget, "invalid target must clear the previous value");
            Assert.Null(bad.GpuTargetUpdatedAt, "invalid target must clear the timestamp");
        }
        foreach (string support in new[] { "NotSupport", "unknown", "support", "" })
        {
            var bad = GcuMessageParser.Apply(snapshot, "Setting/Status", GpuStatus(GpuDisplayRoute.Hybrid, support), now, out _);
            Assert.False(bad.CanSetGpuTarget(GpuDisplayRoute.Direct, now), "only exact confirmed support permits switching");
        }
        return Task.CompletedTask;
    }

    private static Task GpuStateExpiresIndependently()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var snapshot = new GcuSnapshot
        {
            IsConnected = true, GpuTarget = GpuDisplayRoute.Direct, GpuSwitchSupported = true,
            GpuTargetUpdatedAt = now, GpuSwitchSupportUpdatedAt = now
        };
        Assert.True(snapshot.CanSetGpuTarget(GpuDisplayRoute.Hybrid, now), "complete state enables both valid targets");
        foreach (GpuDisplayRoute target in new[] { GpuDisplayRoute.Unknown, GpuDisplayRoute.IgpuOnly, (GpuDisplayRoute)99 })
            Assert.False(snapshot.CanSetGpuTarget(target, now), "there are only two supported targets");
        Assert.False((snapshot with { GpuTargetUpdatedAt = now.AddSeconds(-11) }).CanSetGpuTarget(GpuDisplayRoute.Hybrid, now), "stale target blocks writes");
        Assert.False((snapshot with { GpuSwitchSupportUpdatedAt = now.AddSeconds(-11) }).CanSetGpuTarget(GpuDisplayRoute.Hybrid, now), "stale support blocks writes");
        Assert.False((snapshot with { GpuTargetUpdatedAt = now.AddSeconds(1) }).IsGpuTargetFresh(now), "future timestamps are invalid");
        Assert.False((snapshot with { IsConnected = false }).CanSetGpuTarget(GpuDisplayRoute.Hybrid, now), "disconnect blocks writes");
        foreach (DgpuPresence presence in new[] { DgpuPresence.Unknown, DgpuPresence.Disabled, DgpuPresence.Absent })
            Assert.Equal(GpuDisplayRoute.Unknown, GpuRouteInference.Infer(InternalPanelAdapter.Discrete, presence), "panel adapter alone cannot prove direct mode");
        var route = new GpuRouteSnapshot { Route = GpuDisplayRoute.Direct, UpdatedAt = now };
        Assert.True(route.IsFresh(now), "new actual route is usable");
        Assert.False(route.IsFresh(now.AddSeconds(11)), "actual route also expires");
        Assert.False(route.IsFresh(now.AddSeconds(-1)), "future actual route is rejected");
        Assert.False((route with { Route = GpuDisplayRoute.Unknown }).IsFresh(now), "unknown stays unknown");
        return Task.CompletedTask;
    }

    private static async Task GpuSwitchConfirmsBothDirections()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake);
        fake.Emit("Setting/Status", GpuStatus(GpuDisplayRoute.Direct));
        foreach (GpuDisplayRoute target in new[] { GpuDisplayRoute.Hybrid, GpuDisplayRoute.Direct })
        {
            fake.Published.Clear();
            fake.OnPublish = (transport, topic, payload) =>
            {
                if (topic == "Setting/Control" && payload.Contains("GETSTATUS"))
                    transport.Emit("Setting/Status", GpuStatus(target));
                return Task.CompletedTask;
            };
            var result = await controller.SetGpuTargetAsync(target);
            Assert.True(result.Confirmed, "new complete target frame confirms the saved target");
            Assert.True(result.Message.Contains("重启后"), "confirmation describes a reboot target");
            Assert.Equal(target, controller.Snapshot.GpuTarget, "saved target follows the readback");
            var writes = fake.Published.Where(p => !p.Payload.Contains("GETSTATUS")).ToArray();
            Assert.Equal(1, writes.Length, "only one state-changing command is allowed");
            Assert.Equal("Setting/Control", writes[0].Topic, "vendor setting control topic");
            using var doc = JsonDocument.Parse(writes[0].Payload);
            Assert.Equal(1, doc.RootElement.EnumerateObject().Count(), "no extra fields or restart command");
            Assert.Equal(target == GpuDisplayRoute.Direct ? "DGPU_DIRECT_CONNECT_TOGGLE_ON" : "DGPU_DIRECT_CONNECT_TOGGLE_OFF",
                doc.RootElement.GetProperty("Action").GetString(), "direction maps to the documented action");
            Assert.False(fake.Published.Any(p => p.Payload.Contains("RESTART")), "never ask the service to restart");
        }
    }

    private static async Task GpuSwitchRejectsUnavailableState()
    {
        var time = new GpuTestTime();
        await using var fake = new FakeTransport();
        await using var controller = new GcuController(fake, time);
        await controller.ConnectAsync(Options);
        async Task Rejected(GpuDisplayRoute target)
        {
            fake.Published.Clear();
            Assert.Equal(GcuWriteStatus.Rejected, (await controller.SetGpuTargetAsync(target)).Status, "invalid state must reject before publishing");
            Assert.Equal(0, fake.Published.Count, "rejected write publishes nothing");
        }
        await Rejected(GpuDisplayRoute.Hybrid);
        fake.Emit("Setting/Status", GpuStatus(GpuDisplayRoute.Direct, "NotSupport"));
        await Rejected(GpuDisplayRoute.Hybrid);
        fake.Emit("Setting/Status", GpuStatus(GpuDisplayRoute.Direct));
        await Rejected(GpuDisplayRoute.Direct);
        await Rejected(GpuDisplayRoute.IgpuOnly);
        await Rejected(GpuDisplayRoute.Unknown);
        await Rejected((GpuDisplayRoute)99);
        time.Now = time.Now.AddSeconds(11);
        await Rejected(GpuDisplayRoute.Hybrid);
        fake.DisconnectNow();
        await Rejected(GpuDisplayRoute.Hybrid);
    }

    private static async Task GpuSwitchNeedsNewCompleteFrame()
    {
        foreach (string response in new[]
        {
            "{}", "{\"FnKey\":\"FNKEY_LOCK\"}",
            "{\"DiscreteGpuDirectConnectionSwitch_Status\":\"DGPU_DIRECT_CONNECT_TOGGLE_OFF\"}",
            "{\"DiscreteGpuDirectConnectionSwitch_Support\":\"Support\"}",
            GpuStatus(GpuDisplayRoute.Direct), GpuStatus(GpuDisplayRoute.Hybrid, "NotSupport")
        })
        {
            await using var fake = new FakeTransport();
            await using var controller = await Connected(fake, TimeSpan.FromMilliseconds(35), TimeSpan.FromMilliseconds(3));
            fake.Emit("Setting/Status", GpuStatus(GpuDisplayRoute.Direct));
            fake.Published.Clear();
            fake.OnPublish = (transport, topic, payload) =>
            {
                if (payload.Contains("GETSTATUS"))
                {
                    transport.Emit("Keyboard/Status", GpuStatus(GpuDisplayRoute.Hybrid));
                    transport.Emit("Setting/Status", response);
                }
                return Task.CompletedTask;
            };
            Assert.Equal(GcuWriteStatus.TimedOut, (await controller.SetGpuTargetAsync(GpuDisplayRoute.Hybrid)).Status,
                "cached values, partial frames and wrong topics must not confirm a target");
            Assert.Equal(1, fake.Published.Count(p => p.Payload.Contains("TOGGLE_OFF")), "timeout cannot resend the switch");
        }
    }

    private static async Task GpuSwitchBusyAndCancellation()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(2));
        fake.Emit("Setting/Status", GpuStatus(GpuDisplayRoute.Direct));
        fake.Published.Clear();
        using var cancel = new CancellationTokenSource();
        Task<GcuWriteResult> pending = controller.SetGpuTargetAsync(GpuDisplayRoute.Hybrid, cancel.Token);
        await WaitUntil(() => fake.Published.Any(p => p.Payload.Contains("GETSTATUS")));
        Assert.Equal(GcuWriteStatus.Busy, (await controller.SetGpuTargetAsync(GpuDisplayRoute.Hybrid)).Status, "second click does not enqueue another switch");
        cancel.Cancel();
        Assert.Equal(GcuWriteStatus.Cancelled, (await pending).Status, "cancelled operation cannot claim success");
        Assert.Equal(1, fake.Published.Count(p => p.Payload.Contains("TOGGLE_OFF")), "only one switch was sent");
        pending = controller.SetGpuTargetAsync(GpuDisplayRoute.Hybrid);
        fake.DisconnectNow();
        Assert.Equal(GcuWriteStatus.Disconnected, (await pending).Status, "disconnect interrupts confirmation");
        Assert.Null(controller.Snapshot.GpuTarget, "disconnect clears the saved target observation");
    }

    private static async Task GpuSwitchFailureDoesNotRetry()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(3));
        fake.Emit("Setting/Status", GpuStatus(GpuDisplayRoute.Direct));
        fake.Published.Clear();
        fake.OnPublish = (_, _, _) => throw new InvalidOperationException("simulated publication failure");
        Assert.Equal(GcuWriteStatus.Failed, (await controller.SetGpuTargetAsync(GpuDisplayRoute.Hybrid)).Status, "publication failure is unconfirmed");
        Assert.Equal(1, fake.Published.Count, "failed publication is not repeated");
        fake.OnPublish = null;
        fake.Published.Clear();
        Assert.Equal(GcuWriteStatus.TimedOut, (await controller.SetGpuTargetAsync(GpuDisplayRoute.Hybrid)).Status, "old cached direct target cannot confirm hybrid");
        Assert.Equal(1, fake.Published.Count(p => p.Payload.Contains("TOGGLE_OFF")), "polling retries reads only");
    }

    private sealed class GpuTestTime : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
