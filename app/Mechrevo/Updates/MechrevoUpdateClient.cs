using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace YALM.Mechrevo;

public sealed record PreparedMechrevoUpdate(string Executable, string Sha256);

public sealed class MechrevoUpdateClient(HttpClient http)
{
    public async Task<MechrevoRelease?> CheckAsync(Version current, CancellationToken ct)
    {
        using var request = Request("https://api.github.com/repos/" + MechrevoRelease.Repository + "/releases/latest");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using var content = new MemoryStream();
        await CopyLimitedAsync(stream, content, 1024 * 1024, null, ct);
        return MechrevoRelease.Parse(System.Text.Encoding.UTF8.GetString(content.ToArray()), current);
    }

    public async Task<PreparedMechrevoUpdate> PrepareAsync(MechrevoRelease release, string directory,
        IProgress<int>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        string archive = Path.Combine(directory, "package.zip");
        using (var request = Request(release.DownloadUrl.AbsoluteUri))
        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != release.Size)
                throw new InvalidDataException("Unexpected download length.");
            using Stream source = await response.Content.ReadAsStreamAsync(ct);
            using (var target = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await CopyLimitedAsync(source, target, release.Size, progress, ct);
                if (target.Length != release.Size) throw new InvalidDataException("Incomplete update download.");
            }
        }
        string hash = await HashAsync(archive, ct);
        if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update checksum mismatch.");

        string executable = Path.Combine(directory, "YALM.exe");
        using (ZipArchive zip = ZipFile.OpenRead(archive))
        {
            // Read only this exact root entry; never extract arbitrary archive paths.
            ZipArchiveEntry[] entries = zip.Entries.Where(entry => entry.FullName == "YALM.exe").ToArray();
            if (entries.Length != 1 || entries[0].Length is <= 0 or > MechrevoRelease.MaximumSize)
                throw new InvalidDataException("Invalid executable in update package.");
            using Stream source = entries[0].Open();
            using var target = new FileStream(executable, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await CopyLimitedAsync(source, target, entries[0].Length, null, ct);
            if (target.Length != entries[0].Length) throw new InvalidDataException("Incomplete executable.");
        }
        ValidateExecutable(executable, release.Version);
        return new(executable, await HashAsync(executable, ct));
    }

    public static void ValidateExecutable(string path, Version expected)
    {
        using (var reader = new PEReader(File.OpenRead(path)))
            if (reader.PEHeaders.CoffHeader.Machine != Machine.Amd64 || reader.PEHeaders.IsDll)
                throw new InvalidDataException("Update is not a Windows x64 executable.");
        var info = FileVersionInfo.GetVersionInfo(path);
        var actual = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        if (MechrevoRelease.Normalize(actual) != MechrevoRelease.Normalize(expected) || info.ProductName != "YALM")
            throw new InvalidDataException("Unexpected executable product/version.");
    }

    private static HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("YALM-Updater/1.0");
        return request;
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }

    private static async Task CopyLimitedAsync(Stream source, Stream target, long maximum,
        IProgress<int>? progress, CancellationToken ct)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) != 0)
        {
            total += read;
            if (total > maximum) throw new InvalidDataException("Download exceeds size limit.");
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            progress?.Report((int)(total * 100 / maximum));
        }
    }
}
