using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YALM.Mechrevo;

internal static partial class Program
{
    private static string UpdateJson(string tag = "v0.2.0", string asset = "YALM.zip", string? url = null,
        string? digest = null, long size = 12, bool prerelease = false, bool draft = false) => JsonSerializer.Serialize(new
        {
            tag_name = tag, draft, prerelease,
            assets = new[] { new { name = asset, size, digest = digest ?? "sha256:" + new string('a', 64),
                browser_download_url = url ?? $"https://github.com/Rea1-ms/YALM/releases/download/{tag}/{asset}" } }
        });

    private static Task UpdateVersionsAndAssets()
    {
        Assert.Null(MechrevoRelease.Parse(UpdateJson(tag: "v0.1.0"), new Version(0, 1, 0, 0)), "three-part tag equals assembly version");
        Assert.Null(MechrevoRelease.Parse(UpdateJson(tag: "v0.0.9.9"), new Version(0, 1, 0)), "older release cannot downgrade");
        Assert.Null(MechrevoRelease.Parse(UpdateJson(prerelease: true), new Version(0, 1)), "prerelease excluded");
        Assert.Null(MechrevoRelease.Parse(UpdateJson(draft: true), new Version(0, 1)), "draft excluded");
        foreach (string name in new[] { "YALM.zip", "YALM-win-x64.zip" })
            Assert.NotNull(MechrevoRelease.Parse(UpdateJson(asset: name), new Version(0, 1)), "supported release asset");
        Assert.Equal(new Version(0, 2, 0, 1), MechrevoRelease.Parse(UpdateJson(tag: "v0.2.0.1"), new Version(0, 2))!.Version,
            "four-part versions retain revision");
        return Task.CompletedTask;
    }

    private static async Task UpdateRejectsUntrustedMetadata()
    {
        foreach (string json in new[]
        {
            UpdateJson(url: "https://example.org/YALM.zip"),
            UpdateJson(url: "https://github.com/seerge/g-helper/releases/download/v0.2.0/YALM.zip"),
            UpdateJson(url: "http://github.com/Rea1-ms/YALM/releases/download/v0.2.0/YALM.zip"),
            UpdateJson(digest: ""), UpdateJson(digest: "sha256:" + new string('z', 64)),
            UpdateJson(size: MechrevoRelease.MaximumSize + 1), UpdateJson(size: 0),
            UpdateJson(asset: "other.zip"), UpdateJson(tag: "v0.2.0-beta")
        })
            await Assert.ThrowsAsync<InvalidDataException>(() => Task.FromResult(MechrevoRelease.Parse(json, new Version(0, 1))));
    }

    private static async Task UpdateHttpFailuresAndCancellation()
    {
        using var missing = new HttpClient(new UpdateHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(await new MechrevoUpdateClient(missing).CheckAsync(new Version(0, 1), default), "no first release is normal");
        using var limited = new HttpClient(new UpdateHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        await Assert.ThrowsAsync<HttpRequestException>(() => new MechrevoUpdateClient(limited).CheckAsync(new Version(0, 1), default));
        using var valid = new HttpClient(new UpdateHttpHandler(request =>
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host, "query uses fixed repository API");
            Assert.True(request.Headers.UserAgent.Count > 0, "GitHub user agent supplied");
            return new(HttpStatusCode.OK) { Content = new StringContent(UpdateJson()) };
        }));
        Assert.NotNull(await new MechrevoUpdateClient(valid).CheckAsync(new Version(0, 1), default), "valid release found");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new MechrevoUpdateClient(valid).CheckAsync(new Version(0, 1), cancelled.Token));
        using var huge = new HttpClient(new UpdateHttpHandler(_ => new(HttpStatusCode.OK)
            { Content = new StringContent(new string('x', 1024 * 1024 + 1)) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new MechrevoUpdateClient(huge).CheckAsync(new Version(0, 1), default));
    }

    private static async Task UpdatePackagesAreVerifiedBeforeExtraction()
    {
        string root = Path.Combine(Path.GetTempPath(), "YALM-update-tests", Guid.NewGuid().ToString("N"));
        byte[] zip = UpdateZip("../outside.exe", "payload");
        foreach (var (body, size, hash) in new[]
        {
            (zip, (long)zip.Length, new string('0', 64)),
            (zip, (long)zip.Length + 1, Convert.ToHexString(SHA256.HashData(zip))),
            (zip, (long)zip.Length - 1, Convert.ToHexString(SHA256.HashData(zip))),
            (zip, (long)zip.Length, Convert.ToHexString(SHA256.HashData(zip))),
            (UpdateZip("YALM.exe", "invalid executable"), 0L, ""),
            (UpdateZip("YALM.exe", "one", "YALM.exe"), 0L, "")
        })
        {
            string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            using var http = new HttpClient(new UpdateHttpHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }));
            var release = new MechrevoRelease(new Version(0, 2), new Uri("https://github.com/Rea1-ms/YALM/releases/download/v0.2.0/YALM.zip"),
                size == 0 ? body.Length : size, hash.Length == 0 ? Convert.ToHexString(SHA256.HashData(body)) : hash);
            try
            {
                await new MechrevoUpdateClient(http).PrepareAsync(release, directory, null, default);
                throw new Exception("Unsafe package was accepted.");
            }
            catch (Exception error) when (error is InvalidDataException or BadImageFormatException) { }
            Assert.False(File.Exists(Path.Combine(root, "outside.exe")), "archive path cannot escape staging directory");
        }
    }

    private static byte[] UpdateZip(string entry, string content, string? duplicate = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry(entry).Open(), Encoding.UTF8)) writer.Write(content);
            if (duplicate is not null)
                using (var writer = new StreamWriter(zip.CreateEntry(duplicate).Open())) writer.Write(content);
        }
        return stream.ToArray();
    }

    private sealed class UpdateHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
