namespace YALM.Mechrevo;

internal static class MechrevoProgram
{
    // Keep the original coordination name so an older executable cannot connect
    // alongside YALM using the same vendor account slot.
    private const string InstanceMutex = @"Local\MHelper.Mechrevo";
    private const string ActivationEvent = @"Local\MHelper.Mechrevo.ShowWindow";
    [STAThread]
    public static int Main(string[] args)
    {
        bool automated = args.Any(arg => new[] { "--startup-test", "--smoke-test", "--probe" }.Contains(arg, StringComparer.OrdinalIgnoreCase));
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        try { return Run(args); }
        catch (Exception exception)
        {
            // Record types and call sites, never exception messages or connection values.
            string details = string.Join(Environment.NewLine, ExceptionDetails(exception));
            Console.Error.WriteLine(details);
            string? reportPath = null;
            foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath() })
            {
                try
                {
                    string directory = Path.Combine(root, "YALM");
                    Directory.CreateDirectory(directory);
                    reportPath = Path.Combine(directory, $"startup-error-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.txt");
                    File.WriteAllText(reportPath, details);
                    break;
                }
                catch { reportPath = null; }
            }
            if (!automated)
                MessageBox.Show("程序启动失败。" + (reportPath is null ? "无法保存错误记录。" : $"错误记录已保存至：\n{reportPath}"),
                    "YALM", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static IEnumerable<string> ExceptionDetails(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            yield return $"{current.GetType().FullName} (0x{current.HResult:X8})\n{current.StackTrace}";
    }

    private static int Run(string[] args)
    {
        if (args.Length is 2 or 3 && args[0] == "--probe")
        {
            int captureSeconds = 15;
            if (args.Length == 3 &&
                (!int.TryParse(args[2], out captureSeconds) || captureSeconds is < 5 or > 120))
                return 2;
            using var probeMutex = new Mutex(true, InstanceMutex, out bool ownsProbeMutex);
            if (!ownsProbeMutex) return 2;
            try { return MechrevoProbe.RunAsync(args[1], captureSeconds).GetAwaiter().GetResult(); }
            finally { probeMutex.ReleaseMutex(); }
        }
        bool smokeTest = args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
        bool startupTest = args.Contains("--startup-test", StringComparer.OrdinalIgnoreCase);
        bool demo = smokeTest || args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var singleInstance = new Mutex(true,
            demo ? InstanceMutex + ".Demo" : InstanceMutex, out bool ownsMutex);
        if (!ownsMutex)
        {
            if (!smokeTest && !startupTest)
            {
                try
                {
                    using var signal = EventWaitHandle.OpenExisting(demo ? ActivationEvent + ".Demo" : ActivationEvent);
                    signal.Set();
                    return 0;
                }
                catch (WaitHandleCannotBeOpenedException) { }
                catch (UnauthorizedAccessException) { }
                MessageBox.Show("YALM 或旧版助手已经运行。", "YALM", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return smokeTest || startupTest ? 2 : 0;
        }

        try
        {
            using var activationSignal = new EventWaitHandle(false, EventResetMode.AutoReset,
                demo ? ActivationEvent + ".Demo" : ActivationEvent);
            MachineInfo machine = MachineInfo.Read();
            using var form = new MechrevoForm(machine, demo, smokeTest, startupTest);
            form.SetActivationSignal(activationSignal);
            Application.Run(form);
            return 0;
        }
        finally
        {
            singleInstance.ReleaseMutex();
        }
    }
}
