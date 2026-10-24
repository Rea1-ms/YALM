namespace YALM.Mechrevo;

public sealed record GcuKeyboardSettings
{
    public string? Type { get; init; }
    public string? Effect { get; init; }
    public int? Brightness { get; init; }
    public int? MonochromeIndex { get; init; }
    public bool? Power { get; init; }
    public bool SingleZone => Type == "SingleZone";
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

    public static Dictionary<string, object> SingleZoneEffect(string effect, int brightness, int colorIndex)
    {
        if (effect is not ("Single" or "Rainbow") || brightness is < 0 or > 4 || colorIndex is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(effect));
        var (r, g, b) = Palette[colorIndex - 1];
        return new()
        {
            ["function"] = "SetEffectALL", ["mode"] = "Lighting", ["effect"] = effect,
            ["light"] = brightness.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["speed"] = "1", ["direction"] = "None", ["nv_save"] = "NOT_SAVE",
            ["MonochromeIndex"] = colorIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ManualIndex1"] = "1", ["ManualIndex2"] = "5", ["ManualIndex3"] = "9",
            ["ManualIndex4"] = "13", ["ManualIndex5"] = "17", ["ManualIndex6"] = "21",
            ["ManualInterval"] = "10", ["BreathingIndex"] = "1",
            ["color"] = new
            {
                isCircular = true, ColorBlocks = 1,
                ColorBuffer = new[] { new { ID = 0, R = r, G = g, B = b } }
            }
        };
    }
}
