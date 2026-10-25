using System.Globalization;
using System.Text.Json;

namespace YALM.Mechrevo;

public sealed record GcuRgb(int R, int G, int B)
{
    public bool IsValid => R is >= 0 and <= 255 && G is >= 0 and <= 255 && B is >= 0 and <= 255;
}

public sealed record GcuKeyboardSettings
{
    public string? Type { get; init; }
    public string? Effect { get; init; }
    public int? Brightness { get; init; }
    public int? MonochromeIndex { get; init; }
    public IReadOnlyList<int>? ManualIndices { get; init; }
    public int? ManualInterval { get; init; }
    public int? BreathingIndex { get; init; }
    public bool? Power { get; init; }
    public bool SingleZone => Type == "SingleZone";

    public bool HasEffectParameters => SingleZone && Brightness is >= 0 and <= 4 &&
        GcuKeyboardProtocol.IsColorIndex(MonochromeIndex) && ManualIndices?.Count == 6 &&
        ManualIndices.All(index => GcuKeyboardProtocol.IsColorIndex(index)) &&
        ManualInterval is >= 1 and <= 10 && BreathingIndex is >= 1 and <= 10;

    public bool SameEffectParameters(GcuKeyboardSettings? other) => HasEffectParameters && other?.HasEffectParameters == true &&
        MonochromeIndex == other.MonochromeIndex && ManualInterval == other.ManualInterval &&
        BreathingIndex == other.BreathingIndex && ManualIndices!.SequenceEqual(other.ManualIndices!);
}

// Saved state supplements presentation only. It is never a live acknowledgement.
public sealed record GcuKeyboardSavedProfile(string Effect, GcuKeyboardSettings Parameters, IReadOnlyList<GcuRgb> Colors, bool IsCircular = true)
{
    public static GcuKeyboardSavedProfile? Match(GcuKeyboardSettings live, IEnumerable<string> candidates)
    {
        if (!live.HasEffectParameters) return null;
        GcuKeyboardSavedProfile? match = null;
        foreach (string json in candidates)
        {
            if (json.Length > 16384) continue;
            try
            {
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                string? effect = Number(root, "save_effect") switch { 1 => "Single", 5 => "Rainbow", 64 => "Manual", 2 => "Breathing", _ => null };
                var parameters = new GcuKeyboardSettings
                {
                    Type = "SingleZone", Brightness = live.Brightness,
                    MonochromeIndex = Number(root, "MonochromeIndex"),
                    ManualIndices = ReadIndices(root), ManualInterval = Number(root, "ManualInterval"),
                    BreathingIndex = Number(root, "BreathingIndex")
                };
                if (!live.SameEffectParameters(parameters) || live.Effect is not null && live.Effect != effect) continue;
                if (effect is null || !root.TryGetProperty("UserDefineColor", out var color) || ReadColors(color) is not { } colors ||
                    !color.TryGetProperty("isCircular", out var circular) || circular.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
                var candidate = new GcuKeyboardSavedProfile(effect, parameters, colors, circular.GetBoolean());
                if (match is not null && (match.Effect != candidate.Effect || match.IsCircular != candidate.IsCircular ||
                    !match.Colors.SequenceEqual(colors))) return null;
                match = candidate;
            }
            catch (JsonException) { }
        }
        return match;
    }

    internal static int? Number(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out number) ? number : null;
    }

    internal static IReadOnlyList<int>? ReadIndices(JsonElement root)
    {
        var result = new int[6];
        for (int i = 0; i < result.Length; i++)
        {
            int? index = Number(root, $"ManualIndex{i + 1}");
            if (!GcuKeyboardProtocol.IsColorIndex(index)) return null;
            result[i] = index!.Value;
        }
        return Array.AsReadOnly(result);
    }

    internal static IReadOnlyList<GcuRgb>? ReadColors(JsonElement color)
    {
        if (color.ValueKind != JsonValueKind.Object || Number(color, "ColorBlocks") != 7 ||
            !color.TryGetProperty("ColorBuffer", out var buffer) || buffer.ValueKind != JsonValueKind.Array ||
            buffer.GetArrayLength() != 7) return null;
        var result = new GcuRgb[7];
        for (int i = 0; i < result.Length; i++)
        {
            JsonElement item = buffer[i];
            if (Number(item, "ID") != i || Number(item, "R") is not int r ||
                Number(item, "G") is not int g || Number(item, "B") is not int b) return null;
            result[i] = new(r, g, b);
            if (!result[i].IsValid) return null;
        }
        return Array.AsReadOnly(result);
    }
}

public sealed record GcuKeyboardDraft(GcuKeyboardSettings BasedOn, string Effect, int MonochromeIndex,
    IReadOnlyList<int> ManualIndices, int ManualInterval, IReadOnlyList<GcuRgb> Colors, bool IsCircular = true)
{
    public bool IsValid => BasedOn.HasEffectParameters && Effect is "Single" or "Rainbow" or "Manual" &&
        GcuKeyboardProtocol.IsColorIndex(MonochromeIndex) && ManualIndices.Count == 6 &&
        ManualIndices.All(index => GcuKeyboardProtocol.IsColorIndex(index)) && ManualInterval is >= 1 and <= 10 &&
        Colors.Count == 7 && Colors.All(color => color.IsValid);

    public static GcuKeyboardDraft? Create(GcuKeyboardSettings? live, GcuKeyboardSavedProfile? saved, string effect) =>
        live?.HasEffectParameters == true && saved is not null && live.SameEffectParameters(saved.Parameters)
            ? new(live, effect, live.MonochromeIndex!.Value, live.ManualIndices!.ToArray(),
                live.ManualInterval!.Value, saved.Colors.ToArray(), saved.IsCircular) : null;

    public GcuRgb ColorAt(int slot) => GcuKeyboardProtocol.ResolveColor(
        slot == 0 ? MonochromeIndex : ManualIndices[slot - 1], Colors[slot]);

    public GcuKeyboardDraft WithColor(int slot, GcuRgb color)
    {
        if (slot is < 0 or > 6 || !color.IsValid) throw new ArgumentOutOfRangeException(nameof(slot));
        int[] indices = ManualIndices.ToArray();
        GcuRgb[] colors = Colors.ToArray();
        colors[slot] = color;
        // Keep exact palette indices where possible; 51 is the vendor's custom RGB index.
        int palette = Array.FindIndex(GcuKeyboardProtocol.Palette, item => item == (color.R, color.G, color.B));
        int index = palette < 0 ? 51 : palette + 1;
        if (slot > 0) indices[slot - 1] = index;
        return this with { MonochromeIndex = slot == 0 ? index : MonochromeIndex, ManualIndices = indices, Colors = colors };
    }
}

public static class GcuKeyboardProtocol
{
    public static readonly (int R, int G, int B)[] Palette =
    [
        (255,0,0), (255,50,0), (255,80,0), (145,60,0), (255,102,0), (255,128,0),
        (255,180,0), (150,128,2), (255,204,0), (204,225,0), (120,255,0), (60,115,18),
        (0,255,0), (0,255,80), (0,255,180), (60,125,135), (0,255,255), (0,180,255),
        (0,80,255), (0,35,102), (0,0,255), (80,0,255), (180,0,255), (110,45,100),
        (255,0,255), (255,0,180), (255,0,80), (180,5,0), (0,0,0), (255,255,255)
    ];

    public static bool IsColorIndex(int? index) => index is >= 1 and <= 30 or 51;

    public static GcuRgb ResolveColor(int index, GcuRgb custom)
    {
        if (index is < 1 or > 30) return custom;
        var (r, g, b) = Palette[index - 1];
        return new(r, g, b);
    }

    public static Dictionary<string, object> SingleZoneEffect(GcuKeyboardDraft draft, int brightness)
    {
        if (!draft.IsValid || brightness is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(draft));
        string Wire(int value) => value.ToString(CultureInfo.InvariantCulture);
        var result = new Dictionary<string, object>
        {
            ["function"] = "SetEffectALL", ["mode"] = "Lighting", ["effect"] = draft.Effect,
            ["light"] = Wire(brightness),
            ["speed"] = "1", ["direction"] = "None", ["nv_save"] = "NOT_SAVE",
            ["MonochromeIndex"] = Wire(draft.MonochromeIndex),
            ["ManualInterval"] = Wire(draft.ManualInterval), ["BreathingIndex"] = Wire(draft.BasedOn.BreathingIndex!.Value),
            ["color"] = new
            {
                isCircular = draft.IsCircular, ColorBlocks = 7,
                ColorBuffer = draft.Colors.Select((color, id) => new { ID = id, color.R, color.G, color.B }).ToArray()
            }
        };
        for (int i = 0; i < 6; i++) result[$"ManualIndex{i + 1}"] = Wire(draft.ManualIndices[i]);
        return result;
    }
}
