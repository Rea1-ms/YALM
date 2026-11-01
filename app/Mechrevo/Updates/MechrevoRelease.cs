using System.Text.Json;

namespace YALM.Mechrevo;

public sealed record MechrevoRelease(Version Version, Uri DownloadUrl, long Size, string Sha256)
{
    public const string Repository = "Rea1-ms/YALM";
    public const string ReleasesUrl = "https://github.com/" + Repository + "/releases";
    public const long MaximumSize = 128 * 1024 * 1024;

    public static Version Normalize(Version version) => new(version.Major, version.Minor,
        Math.Max(0, version.Build), Math.Max(0, version.Revision));

    public static MechrevoRelease? Parse(string json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        string number = tag.StartsWith('v') ? tag[1..] : tag;
        if (number.Length == 0 || number.Any(c => c != '.' && !char.IsAsciiDigit(c)) ||
            !Version.TryParse(number, out Version? version))
            throw new InvalidDataException("Invalid release version.");
        version = Normalize(version);
        if (version <= Normalize(current)) return null;

        JsonElement[] assets = root.GetProperty("assets").EnumerateArray().ToArray();
        foreach (string name in new[] { "YALM-win-x64.zip", "YALM.zip" })
        {
            JsonElement[] matches = assets.Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
            if (matches.Length == 0) continue;
            if (matches.Length != 1) throw new InvalidDataException("Ambiguous release asset.");
            JsonElement asset = matches[0];
            string expected = $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{name}";
            string? url = asset.GetProperty("browser_download_url").GetString();
            if (!string.Equals(url, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unexpected release URL.");
            long size = asset.GetProperty("size").GetInt64();
            string digest = asset.TryGetProperty("digest", out JsonElement hash) ? hash.GetString() ?? "" : "";
            if (size <= 0 || size > MaximumSize || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                digest.Length != 71 || digest[7..].Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("Missing or invalid release checksum/size.");
            return new(version, new Uri(expected), size, digest[7..]);
        }
        throw new InvalidDataException("No supported update package.");
    }
}
