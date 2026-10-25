using System.Text.Json;
using System.Text.Json.Nodes;
using YALM.Mechrevo;

internal static partial class Program
{
    private static string ManualFrame() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "keyboard-manual-status.json"));
    private static GcuKeyboardSettings ParseKeyboard(string json) =>
        GcuMessageParser.Apply(new(), "Keyboard/Status", json, DateTimeOffset.UtcNow, out _).Keyboard!;
    private static string[] SavedKeyboardFrames() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "keyboard-saved-effects.json")))!.AsArray().Select(item => item!.ToJsonString()).ToArray();

    private static Task KeyboardSequenceParsing()
    {
        var live = ParseKeyboard(ManualFrame());
        Assert.True(live.HasEffectParameters, "all real manual parameters are present");
        Assert.Null(live.Effect, "sequence fields alone do not identify the active effect");
        Assert.True(live.ManualIndices!.SequenceEqual(new[] { 1, 5, 9, 13, 21, 25 }), "preserve six ordered palette indices");
        foreach (string field in new[] { "ManualIndex3", "ManualInterval", "BreathingIndex" })
        {
            var bad = JsonNode.Parse(ManualFrame())!;
            bad.AsObject().Remove(field);
            var next = GcuMessageParser.Apply(new() { Keyboard = live }, "Keyboard/Status", bad.ToJsonString(), DateTimeOffset.UtcNow, out _);
            Assert.False(next.Keyboard!.HasEffectParameters, "missing fields cannot inherit old frame parameters");
        }
        foreach (int index in new[] { 0, 31, 50, 52, 255 })
        {
            var bad = JsonNode.Parse(ManualFrame())!;
            bad["ManualIndex1"] = index;
            Assert.Null(ParseKeyboard(bad.ToJsonString()).ManualIndices, "invalid color sequence is not editable");
        }
        var custom = JsonNode.Parse(ManualFrame())!;
        custom["MonochromeIndex"] = "51"; custom["ManualIndex6"] = "51";
        Assert.True(ParseKeyboard(custom.ToJsonString()).HasEffectParameters, "custom RGB index is supported");
        return Task.CompletedTask;
    }

    private static Task SavedKeyboardRequiresUniqueMatch()
    {
        var live = ParseKeyboard(ManualFrame());
        string[] saved = SavedKeyboardFrames();
        var match = GcuKeyboardSavedProfile.Match(live, saved);
        Assert.Equal("Manual", match!.Effect, "choose the exact matching record rather than the first project key");
        Assert.Null(live.Effect, "saved mode never modifies the live effect field");
        Assert.Equal("Manual", GcuKeyboardSavedProfile.Match(live, new[] { saved[1], saved[1] })!.Effect, "equivalent records are harmless");
        var conflicting = JsonNode.Parse(saved[1])!; conflicting["save_effect"] = 5;
        Assert.Null(GcuKeyboardSavedProfile.Match(live, new[] { saved[1], conflicting.ToJsonString() }), "ambiguous effects remain unknown");
        conflicting = JsonNode.Parse(saved[1])!;
        conflicting["UserDefineColor"]!["ColorBuffer"]![0]!["R"] = 123;
        Assert.Null(GcuKeyboardSavedProfile.Match(live, new[] { saved[1], conflicting.ToJsonString() }), "ambiguous RGB also remains unknown");
        conflicting = JsonNode.Parse(saved[1])!;
        conflicting["UserDefineColor"]!["ColorBuffer"] = null;
        Assert.Null(GcuKeyboardSavedProfile.Match(live, new[] { saved[1], conflicting.ToJsonString() }), "a malformed matching record cannot be silently skipped");
        Assert.Null(GcuKeyboardSavedProfile.Match(live with { ManualInterval = 5 }, saved), "stale parameters cannot select a saved mode");
        Assert.Null(GcuKeyboardSavedProfile.Match(live with { Effect = "Rainbow" }, saved), "saved data cannot override an explicitly reported mode");
        Assert.Null(GcuKeyboardSavedProfile.Match(live with { BreathingIndex = null }, saved), "incomplete status is insufficient to match");
        return Task.CompletedTask;
    }

    private static Task SavedKeyboardRejectsIncompleteRgb()
    {
        var live = ParseKeyboard(ManualFrame());
        string saved = SavedKeyboardFrames()[1];
        foreach (Action<JsonNode> change in new Action<JsonNode>[]
        {
            node => node["UserDefineColor"]!["ColorBlocks"] = 6,
            node => node["UserDefineColor"]!["ColorBuffer"]![3]!["ID"] = 2,
            node => node["UserDefineColor"]!["ColorBuffer"]![5]!["R"] = 256,
            node => node["UserDefineColor"]!["ColorBuffer"] = null,
            node => node["UserDefineColor"]!["ColorBuffer"]![0]!.AsObject().Remove("G")
        })
        {
            var malformed = JsonNode.Parse(saved)!; change(malformed);
            Assert.Null(GcuKeyboardSavedProfile.Match(live, new[] { malformed.ToJsonString(), "[]", "invalid" }),
                "malformed color buffers must not be padded with guessed colors");
        }
        return Task.CompletedTask;
    }

    private static Task KeyboardDraftPreservesColors()
    {
        var live = ParseKeyboard(ManualFrame());
        var saved = GcuKeyboardSavedProfile.Match(live, SavedKeyboardFrames())!;
        var draft = GcuKeyboardDraft.Create(live, saved, "Manual")!.WithColor(3, new(12, 34, 56));
        Assert.Equal(51, draft.ManualIndices[2], "arbitrary RGB selects the vendor custom color index");
        Assert.Equal(new GcuRgb(12, 34, 56), draft.ColorAt(3), "third step uses buffer three, not zero");
        Assert.Equal(9, live.ManualIndices![2], "editing a draft does not mutate the service snapshot");
        foreach (string effect in new[] { "Single", "Rainbow", "Manual" })
        {
            var changed = draft with { Effect = effect, IsCircular = false };
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(GcuKeyboardProtocol.SingleZoneEffect(changed, 4)));
            var root = json.RootElement;
            Assert.Equal("4", root.GetProperty("ManualInterval").GetString(), "effect changes preserve the interval");
            Assert.Equal("25", root.GetProperty("ManualIndex6").GetString(), "effect changes preserve step six");
            Assert.Equal("51", root.GetProperty("ManualIndex3").GetString(), "effect changes preserve custom step indices");
            Assert.Equal(7, root.GetProperty("color").GetProperty("ColorBlocks").GetInt32(), "all seven RGB slots must travel together");
            Assert.False(root.GetProperty("color").GetProperty("isCircular").GetBoolean(), "preserve the saved color buffer metadata");
            Assert.Equal(56, root.GetProperty("color").GetProperty("ColorBuffer")[3].GetProperty("B").GetInt32(), "custom RGB survives effect switches");
        }
        Assert.Null(GcuKeyboardDraft.Create(live, null, "Manual"), "missing saved RGB cannot invent a complete draft");
        return Task.CompletedTask;
    }

    private static async Task ManualWriteRequiresCompleteReadback()
    {
        foreach (string? omitted in new[] { null, "ManualIndex6", "ManualInterval", "effect" })
        {
            await using var fake = new FakeTransport();
            await using var controller = await Connected(fake, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(2));
            SeedKeyboard(fake);
            var draft = KeyboardDraft(controller, "Manual") with { ManualInterval = 7 };
            var reply = JsonNode.Parse(ManualFrame())!;
            reply["effect"] = "Manual"; reply["brightNess"] = "3"; reply["ManualInterval"] = "7";
            if (omitted is not null) reply.AsObject().Remove(omitted);
            fake.Published.Clear();
            fake.OnPublish = (transport, topic, payload) =>
            {
                if (payload.Contains("GETSTATUS")) transport.Emit("Keyboard/Status", reply.ToJsonString());
                return Task.CompletedTask;
            };
            Assert.Equal(omitted is null ? GcuWriteStatus.Confirmed : GcuWriteStatus.Sent,
                (await controller.SetKeyboardEffectAsync(draft)).Status, "only a complete fresh effect frame can confirm");
            Assert.Equal(1, fake.Published.Count(item => item.Payload.Contains("SetEffectALL")), "one write even when readback is incomplete");
        }
    }

    private static async Task CustomRgbCannotClaimLiveConfirmation()
    {
        foreach (string effect in new[] { "Single", "Manual" })
        {
            await using var fake = new FakeTransport();
            await using var controller = await Connected(fake, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(2));
            SeedKeyboard(fake);
            var draft = KeyboardDraft(controller, effect).WithColor(effect == "Single" ? 0 : 6, new(15, 25, 35));
            var reply = JsonNode.Parse(ManualFrame())!;
            reply["effect"] = effect; reply["brightNess"] = "3";
            reply[effect == "Single" ? "MonochromeIndex" : "ManualIndex6"] = "51";
            fake.OnPublish = (transport, topic, payload) =>
            {
                if (payload.Contains("GETSTATUS")) transport.Emit("Keyboard/Status", reply.ToJsonString());
                return Task.CompletedTask;
            };
            Assert.Equal(GcuWriteStatus.Sent, (await controller.SetKeyboardEffectAsync(draft)).Status,
                "an index alone does not verify the custom RGB, even when effect is supplied");
        }
    }

    private static async Task KeyboardRejectsChangedDraft()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake);
        SeedKeyboard(fake);
        var draft = KeyboardDraft(controller, "Rainbow");
        SeedKeyboard(fake, color: 21);
        fake.Published.Clear();
        Assert.Equal(GcuWriteStatus.Rejected, (await controller.SetKeyboardEffectAsync(draft)).Status,
            "another client changed the parameters while the editor was open");
        Assert.Equal(0, fake.Published.Count, "stale drafts cannot overwrite another client's colors");
        SeedKeyboard(fake, brightness: 4);
        fake.OnPublish = (transport, topic, payload) =>
        {
            if (payload.Contains("GETSTATUS")) SeedKeyboard(transport, effect: "Rainbow", brightness: 4);
            return Task.CompletedTask;
        };
        Assert.True((await controller.SetKeyboardEffectAsync(draft)).Confirmed, "an independent brightness update can be preserved");
        using var sent = JsonDocument.Parse(fake.Published[0].Payload);
        Assert.Equal("4", sent.RootElement.GetProperty("light").GetString(), "use latest brightness, not the editor's earlier value");
    }

    private static async Task KeyboardRefreshWaitsForNewFrame()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(2));
        SeedKeyboard(fake);
        fake.Published.Clear();
        Assert.Equal(GcuWriteStatus.TimedOut, (await controller.RefreshKeyboardAsync()).Status,
            "an old complete snapshot cannot count as a new read");
        fake.OnPublish = (transport, topic, payload) =>
        {
            if (topic == "Keyboard/Ctrl" && payload.Contains("GETSTATUS")) transport.Emit("Keyboard/Status", ManualFrame());
            return Task.CompletedTask;
        };
        Assert.True((await controller.RefreshKeyboardAsync()).Confirmed, "new complete frame completes the read even with an unknown effect");
        Assert.True(fake.Published.All(item => item.Topic == "Keyboard/Ctrl" && item.Payload == "{\"Action\":\"GETSTATUS\"}"),
            "reload only publishes keyboard queries");
    }

    private static async Task KeyboardEffectsDoNotWaitForMissingReadback()
    {
        await using var fake = new FakeTransport();
        await using var controller = await Connected(fake);
        SeedKeyboard(fake, effect: null);
        GcuKeyboardDraft draft = KeyboardDraft(controller, "Manual") with { ManualInterval = 7 };
        fake.Published.Clear();
        Task<GcuWriteResult> pending = controller.SetKeyboardEffectAsync(draft);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        GcuWriteResult result = await pending.WaitAsync(timeout.Token);
        Assert.Equal(GcuWriteStatus.Sent, result.Status, "missing readback returns sent without the eight-second confirmation loop");
        Assert.False(result.Confirmed, "publication is not service or hardware confirmation");
        Assert.Null(controller.Snapshot.Keyboard!.Effect, "do not fill in an effect from the sent command");
        Assert.Equal(4, controller.Snapshot.Keyboard.ManualInterval, "do not overwrite observed parameters with the draft");
        Assert.Equal(2, fake.Published.Count, "one effect publication and one read query, no polling");
        Assert.True(fake.Published[0].Payload.Contains("SetEffectALL") && fake.Published[1].Payload.Contains("GETSTATUS"),
            "query follows the effect command");
        fake.OnPublish = (transport, topic, payload) =>
        {
            if (payload.Contains("GETSTATUS")) SeedKeyboard(transport, effect: null, brightness: 2);
            return Task.CompletedTask;
        };
        Assert.True((await controller.SetKeyboardBrightnessAsync(2)).Confirmed,
            "an unconfirmed effect immediately releases the operation lock for the next setting");
    }

    private static async Task KeyboardSendSeparatesPublicationFromQueryFailure()
    {
        foreach (bool failQuery in new[] { false, true })
        {
            await using var fake = new FakeTransport();
            await using var controller = await Connected(fake);
            SeedKeyboard(fake);
            fake.Published.Clear();
            fake.OnPublish = (_, _, payload) =>
            {
                if (payload.Contains(failQuery ? "GETSTATUS" : "SetEffectALL")) throw new InvalidOperationException("offline failure");
                return Task.CompletedTask;
            };
            GcuWriteResult result = await controller.SetKeyboardEffectAsync(KeyboardDraft(controller, "Rainbow"));
            Assert.Equal(failQuery ? GcuWriteStatus.Sent : GcuWriteStatus.Failed, result.Status,
                "a failed status query cannot undo a completed command publication");
            Assert.False(result.Confirmed, "neither failure is a confirmation");
            Assert.Equal(1, fake.Published.Count(item => item.Payload.Contains("SetEffectALL")), "never retry an uncertain write");
        }
    }
}
