using System.Diagnostics;

namespace YALM.Mechrevo;

internal static class MechrevoUpdateInstaller
{
    internal static async Task StartAsync(PreparedMechrevoUpdate update, string target, CancellationToken ct)
    {
        string directory = Path.GetDirectoryName(update.Executable)!;
        string script = Path.Combine(directory, "apply-update.ps1");
        using (Stream resource = typeof(MechrevoUpdateInstaller).Assembly.GetManifestResourceStream("YALM.ApplyUpdate.ps1")!)
        using (var output = new FileStream(script, FileMode.CreateNew))
            await resource.CopyToAsync(output, ct);
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
            "-ParentId", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ParentStarted", current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-Source", update.Executable, "-Target", target, "-Sha256", update.Sha256 })
            start.ArgumentList.Add(argument);
        using Process helper = Process.Start(start) ?? throw new IOException("Updater did not start.");
        string ready = Path.Combine(directory, "ready");
        try
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (helper.HasExited) throw new IOException("Updater exited before handoff.");
                if (File.Exists(ready)) return;
                await Task.Delay(100, ct);
            }
            throw new TimeoutException("Updater did not become ready.");
        }
        catch
        {
            // Stop only this helper; it must not install after a later ordinary exit.
            if (!helper.HasExited) helper.Kill();
            throw;
        }
    }
}
