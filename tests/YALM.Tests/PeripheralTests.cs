using System.Text.Json;
using YALM.Mechrevo;

internal static partial class Program
{
    private static JsonDocument ReadRealServiceFixture() => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "real-service-status.json")));

    private static Task RealCurveTerminatorRetainsEffectivePoint()
    {
        using var fixture = ReadRealServiceFixture();
        GcuSnapshot snapshot = GcuMessageParser.Apply(new() { IsConnected = true }, "Fan/Table",
            fixture.RootElement.GetProperty("FanTable").GetRawText(), DateTimeOffset.UtcNow, out _);
        foreach (FanCurveTarget target in Enum.GetValues<FanCurveTarget>())
        {
            var points = snapshot.FanTable!.Curve(target);
            Assert.True(FanCurveDraft.CanEdit(points), "real curve must pass the same validation used by the chart");
            Assert.Equal(255, points[10].DownTemperature, "real final falling threshold is a terminator");
            Assert.False(points[10].IsTail, "the last rising point is effective");
            Assert.True(points.Skip(11).All(point => point.IsTail), "only the final five slots are tail slots");
            Assert.True(FanCurveDraft.TryBuild(points.Take(11).Select(point => point.Duty).ToArray(),
                out var duties, out var error), error);
            Assert.True(duties.SequenceEqual(points.Select(point => point.Duty)), "reading and rebuilding must preserve all duties");
        }
        return Task.CompletedTask;
    }

    private static Task RealKeyboardStatusParses()
    {
        using var fixture = ReadRealServiceFixture();
        var now = DateTimeOffset.UtcNow;
        var snapshot = GcuMessageParser.Apply(new() { IsConnected = true }, "Keyboard/Status",
            fixture.RootElement.GetProperty("Keyboard").GetRawText(), now, out _);
        Assert.Equal("SingleZone", snapshot.Keyboard?.Type, "real keyboard type");
        Assert.Null(snapshot.Keyboard?.Effect, "a null effect must remain unknown");
        Assert.Equal(3, snapshot.Keyboard?.Brightness, "single-zone brightness is a level");
        Assert.Equal(17, snapshot.Keyboard?.MonochromeIndex, "real color palette index");
        Assert.Equal(false, snapshot.Keyboard?.Power, "powerStatus Off must not be replaced by lowercase powerstatus");
        var on = GcuMessageParser.Apply(snapshot, "Keyboard/Status",
            "{\"type\":\"SingleZone\",\"powerStatus\":\"On\",\"effect\":null}", now, out _);
        Assert.Equal(true, on.Keyboard?.Power, "power can be on without an effect report");
        Assert.Null(on.Keyboard?.Effect, "missing effect must not be inferred from power");
        Assert.False(snapshot.IsKeyboardFresh(now.AddSeconds(11)), "keyboard state must expire independently");
        snapshot = GcuMessageParser.Apply(snapshot, "Setting/Status", "{\"FnKey\":\"FNKEY_UNLOCK\"}", now, out _);
        Assert.Equal(false, snapshot.FnLocked, "unlocked FN state");
        snapshot = GcuMessageParser.Apply(snapshot, "Setting/Status", "{\"FnKey\":\"unknown\"}", now, out _);
        Assert.False(snapshot.IsFnLockFresh(now), "unknown FN state must disable writes");
        return Task.CompletedTask;
    }

    private static Task CurveDisplayUsesStopThreshold()
    {
        using var fixture = ReadRealServiceFixture();
        var snapshot = GcuMessageParser.Apply(new() { IsConnected = true }, "Fan/Table",
            fixture.RootElement.GetProperty("FanTable").GetRawText(), DateTimeOffset.UtcNow, out _);
        foreach (FanCurveTarget target in Enum.GetValues<FanCurveTarget>())
        {
            var curve = snapshot.FanTable!.Curve(target);
            Assert.Equal(0, curve[0].UpTemperature, "firmware sentinel must remain unchanged");
            Assert.Equal(48, curve[0].PlotTemperature, "fixed stop point is drawn at its real threshold");
            Assert.True(curve.Take(11).All(point => point.PlotTemperature is >= 20 and <= 110),
                "off-plot sentinel must not create a diagonal line through the plot");
            Assert.True(curve.Skip(1).Take(10).All(point => point.PlotTemperature == point.UpTemperature),
                "effective rising thresholds must be preserved, including coincident GPU points");
            Assert.Equal(255, curve[15].PlotTemperature, "tail sentinel is not an editable display point");
        }
        return Task.CompletedTask;
    }

    private static async Task FnLockConfirmsIndependently()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake);
        fake.Emit("Setting/Status", "{\"FnKey\":\"FNKEY_UNLOCK\"}");
        fake.Published.Clear();
        fake.OnPublish = (transport, topic, payload) =>
        {
            if (topic == "Setting/Control" && payload.Contains("GETSTATUS"))
                transport.Emit("Setting/Status", "{\"FnKey\":\"FNKEY_LOCK\"}");
            return Task.CompletedTask;
        };
        Assert.True((await controller.SetFnLockAsync(true)).Confirmed, "FN lock must not depend on fan telemetry");
        Assert.True(fake.Published[0].Payload.Contains("FNKEY_LOCK"), "vendor FN lock action");
        Assert.True(fake.Published.All(item => item.Topic == "Setting/Control"), "FN lock stays on settings channel");
    }

    private static async Task CachedFnLockCannotConfirm()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(2));
        fake.Emit("Setting/Status", "{\"FnKey\":\"FNKEY_LOCK\"}");
        fake.OnPublish = (transport, topic, payload) =>
        {
            if (payload.Contains("GETSTATUS")) transport.Emit("Setting/Status", "{\"OSD\":\"OSD_HIDDEN_OFF\"}");
            return Task.CompletedTask;
        };
        Assert.Equal(GcuWriteStatus.TimedOut, (await controller.SetFnLockAsync(true)).Status,
            "cached matching FN value cannot confirm unrelated new frames");
    }

    private static void SeedKeyboard(FakeTransport fake, string? effect = "Single", int brightness = 3, int color = 17) =>
        fake.Emit("Keyboard/Status", JsonSerializer.Serialize(new
        {
            type = "SingleZone", effect, brightNess = brightness.ToString(), MonochromeIndex = color.ToString(), powerStatus = "On"
        }));

    private static async Task KeyboardEffectSendsRequiredFields()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake);
        SeedKeyboard(fake, effect: null);
        fake.Published.Clear();
        fake.OnPublish = (transport, topic, payload) =>
        {
            if (payload.Contains("GETSTATUS")) SeedKeyboard(transport, brightness: 2, color: 21);
            return Task.CompletedTask;
        };
        Assert.True((await controller.SetKeyboardEffectAsync("Single", 2, 21)).Confirmed, "new full keyboard frame should confirm");
        using var command = JsonDocument.Parse(fake.Published[0].Payload);
        var root = command.RootElement;
        Assert.Equal("SetEffectALL", root.GetProperty("function").GetString(), "vendor keyboard effect function");
        Assert.Equal("2", root.GetProperty("light").GetString(), "brightness travels with the effect");
        Assert.Equal("21", root.GetProperty("MonochromeIndex").GetString(), "single-zone palette index");
        Assert.Equal("NOT_SAVE", root.GetProperty("nv_save").GetString(), "do not persist to firmware without request");
        foreach (var key in new[] { "ManualIndex1", "ManualIndex2", "ManualIndex3", "ManualIndex4", "ManualIndex5", "ManualIndex6", "ManualInterval", "BreathingIndex" })
            Assert.True(root.TryGetProperty(key, out _), "single-zone firmware rejects missing auxiliary fields");
        Assert.Equal(0, root.GetProperty("color").GetProperty("ColorBuffer")[0].GetProperty("ID").GetInt32(), "RGB color block requires its ID");
        Assert.Equal(1, fake.Published.Count(item => item.Payload.Contains("SetEffectALL")), "writes must not be retried");
    }

    private static async Task MissingKeyboardEffectCannotConfirm()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(2));
        SeedKeyboard(fake);
        fake.OnPublish = (transport, topic, payload) =>
        {
            if (payload.Contains("GETSTATUS")) SeedKeyboard(transport, effect: null);
            return Task.CompletedTask;
        };
        Assert.Equal(GcuWriteStatus.TimedOut, (await controller.SetKeyboardEffectAsync("Single", 3, 17)).Status,
            "missing effect cannot confirm even if brightness matches");
    }

    private static async Task UnsupportedKeyboardRejectsWrites()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake);
        fake.Emit("Keyboard/Status", "{\"type\":\"Unknown\",\"powerStatus\":\"On\"}");
        fake.Published.Clear();
        Assert.Equal(GcuWriteStatus.Rejected, (await controller.SetKeyboardEffectAsync("Single", 3, 17)).Status, "unknown keyboard rejected");
        Assert.Equal(GcuWriteStatus.Rejected, (await controller.SetKeyboardPowerAsync(false)).Status, "unknown keyboard power rejected");
        SeedKeyboard(fake);
        Assert.Equal(GcuWriteStatus.Rejected, (await controller.SetKeyboardEffectAsync("Breathing", 3, 17)).Status, "unsupported effect rejected");
        Assert.Equal(GcuWriteStatus.Rejected, (await controller.SetKeyboardEffectAsync("Single", 5, 17)).Status, "invalid brightness rejected");
        Assert.Equal(0, fake.Published.Count, "rejections must not publish commands");
    }

    private static async Task KeyboardPowerConfirms()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake);
        SeedKeyboard(fake);
        fake.Published.Clear();
        fake.OnPublish = (transport, topic, payload) =>
        {
            if (payload.Contains("GETSTATUS"))
                transport.Emit("Keyboard/Status", "{\"type\":\"SingleZone\",\"powerStatus\":\"Off\",\"powerstatus\":1}");
            return Task.CompletedTask;
        };
        Assert.True((await controller.SetKeyboardPowerAsync(false)).Confirmed, "case-sensitive powerStatus confirms");
        using var command = JsonDocument.Parse(fake.Published[0].Payload);
        Assert.Equal(0, command.RootElement.GetProperty("powerstatus").GetInt32(), "SetPower uses lowercase numeric field");
    }
}
