using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using YALM.Mechrevo;

namespace YALM.UiTests;

internal static partial class Program
{
    private static void CheckUpdates(string[] args)
    {
        using (Form demo = CreateForm(demo: true))
        {
            Require(!Get<Label>(demo, "_versionLabel").Enabled, "demo disables program updates");
            Wait((Task)Call(demo, "CheckUpdatesAsync", true)!);
            Require(!Get<System.Windows.Forms.Timer>(demo, "_updateTimer").Enabled, "offline construction never starts update polling");
        }
        using (Form normal = CreateForm(demo: false))
        {
            Require(Get<Label>(normal, "_versionLabel").Enabled, "manual updates do not require vendor connection");
            Require(!Get<System.Windows.Forms.Timer>(normal, "_updateTimer").Enabled, "unshown forms never access the update server");
        }
        int index = Array.IndexOf(args, "--update-exe");
        if (index < 0) return;
        string executable = Path.GetFullPath(args[index + 1]);
        Version current = MainForm.Assembly.GetName().Version!;
        MechrevoUpdateClient.ValidateExecutable(executable, current);
        Require(true, "published executable has the expected x64 product and file version");
        try
        {
            MechrevoUpdateClient.ValidateExecutable(executable, new Version(99, 0));
            throw new InvalidOperationException("Mismatched executable was accepted.");
        }
        catch (InvalidDataException) { Require(true, "mismatched executable version rejected"); }
        string directory = Path.GetFullPath(Path.Combine(".cache", "update-package-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        string archive = Path.Combine(directory, "release.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(executable, "YALM.exe");
            using var writer = new StreamWriter(zip.CreateEntry("../outside.txt").Open());
            writer.Write("must never be extracted");
        }
        byte[] package = File.ReadAllBytes(archive);
        using var http = new HttpClient(new PackageHandler(package));
        var release = new MechrevoRelease(current, new Uri("https://github.com/Rea1-ms/YALM/releases/download/v0.1.0/YALM.zip"),
            package.Length, Convert.ToHexString(SHA256.HashData(package)));
        Task<PreparedMechrevoUpdate> preparation = new MechrevoUpdateClient(http).PrepareAsync(release,
            Path.Combine(directory, "staged"), null, default);
        Wait(preparation);
        Require(File.ReadAllBytes(preparation.Result.Executable).SequenceEqual(File.ReadAllBytes(executable)),
            "verified package produces the complete published executable");
        Require(!File.Exists(Path.Combine(directory, "outside.txt")), "unselected archive entries are never extracted");
    }

    private sealed class PackageHandler(byte[] package) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
    }
}
