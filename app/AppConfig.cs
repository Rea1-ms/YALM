using System.Text.Json;

/// <summary>YALM-only settings storage. The original ASUS model policy is archived outside the build.</summary>
public static class AppConfig
{
    private static readonly object Sync = new();
    private static readonly System.Timers.Timer SaveTimer = new(2000) { AutoReset = false };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string ConfigFile;
    private static readonly Dictionary<string, object?> Values = new(StringComparer.OrdinalIgnoreCase);

    static AppConfig()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YALM");
        ConfigFile = Path.Combine(directory, "yalm.json");
        try
        {
            Directory.CreateDirectory(directory);
            Load(ConfigFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Logger.WriteLine("YALM 设置读取失败，使用默认值；" + exception.GetType().Name);
        }
        SaveTimer.Elapsed += (_, _) => Save();
    }

    public static int Get(string key, int defaultValue = 0)
    {
        object? value = GetValue(key);
        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int number)) return number;
            if (element.ValueKind == JsonValueKind.True) return 1;
            if (element.ValueKind == JsonValueKind.False) return 0;
            if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out number)) return number;
        }
        return value switch
        {
            int number => number,
            long number => (int)Math.Clamp(number, int.MinValue, int.MaxValue),
            double number => (int)Math.Clamp(number, int.MinValue, int.MaxValue),
            bool flag => flag ? 1 : 0,
            string text when int.TryParse(text, out int number) => number,
            _ => defaultValue
        };
    }

    public static bool Is(string key) => Get(key) != 0;
    public static bool IsNotFalse(string key) => !Values.ContainsKey(key) || Is(key);
    public static string GetString(string key, string defaultValue = "") => GetValue(key)?.ToString() ?? defaultValue;

    public static void Set(string key, object? value)
    {
        lock (Sync) Values[key] = value;
        SaveTimer.Stop();
        SaveTimer.Start();
    }

    public static void Remove(string key)
    {
        lock (Sync) Values.Remove(key);
        SaveTimer.Stop();
        SaveTimer.Start();
    }

    public static bool IsOverlay() => Is("overlay");
    public static bool IsOverlayGameOnly() => Is("overlay_game_only");
    public static bool IsBWIcon() => Is("bw_icon");

    private static object? GetValue(string key)
    {
        lock (Sync) return Values.TryGetValue(key, out object? value) ? value : null;
    }

    private static void Load(string path)
    {
        if (!File.Exists(path)) return;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object) return;
        lock (Sync)
        {
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
                Values[property.Name] = property.Value.Clone();
        }
        Logger.WriteLine("YALM 设置已读取。");
    }

    private static void Save()
    {
        Dictionary<string, object?> snapshot;
        lock (Sync) snapshot = new(Values, StringComparer.OrdinalIgnoreCase);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
            string temporary = ConfigFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, JsonOptions));
            if (File.Exists(ConfigFile)) File.Replace(temporary, ConfigFile, ConfigFile + ".bak");
            else File.Move(temporary, ConfigFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Logger.WriteLine("YALM 设置保存失败；" + exception.GetType().Name);
        }
    }
}
