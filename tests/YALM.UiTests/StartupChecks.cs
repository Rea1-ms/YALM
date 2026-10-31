using Microsoft.Win32.TaskScheduler;
using System.Reflection;
using System.Security.Principal;

namespace YALM.UiTests;

internal static partial class Program
{
    private static readonly Type StartupType = MainForm.Assembly.GetType("YALM.Mechrevo.MechrevoStartup")!;

    private static object NewStartup(string name, string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return Activator.CreateInstance(StartupType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [name, path, identity.User!.Value], null)!;
    }

    private static object? StartupCall(object startup, string method, params object?[] args) =>
        StartupType.GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(startup, args);

    private static void CheckStartup()
    {
        using var identity = WindowsIdentity.GetCurrent();
        object current = Activator.CreateInstance(StartupType)!;
        Require(Get<string>(current, "_taskName") == "YALM_" + identity.User!.Value,
            "startup is scoped to YALM and the current user, independently of G-Helper");
        const string executable = @"C:\YALM with spaces\YALM.exe";
        object startup = NewStartup("YALM_Offline", executable);
        using var service = new TaskService();
        using var definition = (TaskDefinition)StartupCall(startup, "CreateDefinition", service)!;
        var logon = definition.Triggers.OfType<LogonTrigger>().Single();
        var reconnect = definition.Triggers.OfType<SessionStateChangeTrigger>().Single();
        Require(definition.Triggers.Count == 2 && SameStartupUser(logon.UserId, identity.User.Value) &&
            logon.Delay == TimeSpan.FromSeconds(1), "startup follows the current user's delayed logon");
        Require(reconnect.StateChange == TaskSessionStateChangeType.ConsoleConnect &&
            SameStartupUser(reconnect.UserId, identity.User.Value), "startup handles session reconnect rather than each unlock");
        var action = definition.Actions.OfType<ExecAction>().Single();
        Require(definition.Actions.Count == 1 && action.Path == executable && action.Arguments == "--autostart" &&
            action.WorkingDirectory == Path.GetDirectoryName(executable),
            "startup keeps an executable path with spaces separate from tray arguments and working directory");
        Require(SameStartupUser(definition.Principal.UserId, identity.User.Value) &&
            definition.Principal.LogonType == TaskLogonType.InteractiveToken && definition.Principal.RunLevel == TaskRunLevel.LUA,
            "startup uses the interactive user without passwords or elevation");
        Require(!definition.Settings.DisallowStartIfOnBatteries && !definition.Settings.StopIfGoingOnBatteries &&
            definition.Settings.ExecutionTimeLimit == TimeSpan.Zero &&
            definition.Settings.MultipleInstances == TaskInstancesPolicy.IgnoreNew,
            "startup stays running on battery without time limits or parallel scheduler launches");
        MethodInfo updatePath = StartupType.GetMethod("NeedsPathUpdate", BindingFlags.Static | BindingFlags.NonPublic)!;
        Require(!(bool)updatePath.Invoke(null, [executable, executable.ToUpperInvariant()])! &&
            (bool)updatePath.Invoke(null, [executable, @"C:\missing-yalm-autostart\YALM.exe"])!,
            "path repair ignores case-only changes and repairs a missing executable");
        string newer = Path.Combine(Environment.SystemDirectory, "where.exe");
        Require(!(bool)updatePath.Invoke(null, [MainForm.Assembly.Location, newer])! &&
            (bool)updatePath.Invoke(null, [newer, MainForm.Assembly.Location])!,
            "path repair updates older versions and preserves a newer scheduled copy");

        CheckStartupSettings();
        CheckStartupSingleInstance();
        using var tray = CreateForm(demo: true);
        Call(tray, "StartInTray");
        tray.Show();
        Application.DoEvents();
        Require(!tray.Visible && Get<NotifyIcon>(tray, "_trayIcon").Visible && tray.Opacity == 1,
            "login startup hides the initial window while registering a usable tray icon");
        Require(Get<System.Windows.Forms.Timer>(tray, "_refreshTimer").Enabled,
            "login startup keeps background refresh active");
        Call(tray, "ShowMainWindow");
        Require(tray.Visible && tray.ShowInTaskbar, "a login-started window can be opened normally from the tray");
        Call(tray, "ExitApplication");
        PumpUntil(() => tray.IsDisposed);
        foreach (bool demo in new[] { false, true })
        {
            using var main = CreateForm(demo: demo, startup: !demo);
            using var more = CreateMore(main);
            int calls = 0;
            Set(more, "_readStartup", (Func<bool>)(() => { calls++; return true; }));
            Wait((System.Threading.Tasks.Task)Call(more, "UpdateStartupAsync", false)!);
            Require(!Get<CheckBox>(more, "_startup").Enabled && calls == 0,
                "demo and startup self-tests never access the startup scheduler");
        }
    }

    private static bool SameStartupUser(string userId, string expectedSid) => userId == expectedSid ||
        ((SecurityIdentifier)new NTAccount(userId).Translate(typeof(SecurityIdentifier))).Value == expectedSid;

    private static Form CreateMore(Form main) =>
        (Form)Activator.CreateInstance(MainForm.GetNestedType("MoreSettingsForm", BindingFlags.NonPublic)!, main)!;

    private static void CheckStartupSettings()
    {
        using var main = CreateForm();
        using var more = CreateMore(main);
        CheckBox check = Get<CheckBox>(more, "_startup");
        bool scheduled = false;
        int writes = 0;
        Set(more, "_readStartup", (Func<bool>)(() => scheduled));
        Set(more, "_setStartup", (Func<bool, bool>)(enabled => { writes++; return scheduled = enabled; }));
        Require(check.Enabled && check.CheckState == CheckState.Indeterminate,
            "startup remains available without hardware connectivity and unknown state is not shown as off");
        Wait((System.Threading.Tasks.Task)Call(more, "UpdateStartupAsync", false)!);
        Require(!check.Checked && writes == 0, "opening settings reads the scheduler without enabling startup");
        Wait((System.Threading.Tasks.Task)Call(more, "UpdateStartupAsync", true)!);
        Require(check.Checked && scheduled && writes == 1, "enabling startup displays the confirmed scheduler state");
        Set(more, "_setStartup", (Func<bool, bool>)(_ => throw new UnauthorizedAccessException("private-marker")));
        Wait((System.Threading.Tasks.Task)Call(more, "UpdateStartupAsync", true)!);
        Require(check.Checked && check.Enabled && Get<string>(main, "_actionMessage").Contains("未能修改"),
            "a denied change restores actual state and makes the failure visible");
        Set(more, "_setStartup", (Func<bool, bool>)(enabled => { writes++; return scheduled = enabled; }));
        Wait((System.Threading.Tasks.Task)Call(more, "UpdateStartupAsync", true)!);
        Require(!check.Checked && !scheduled && writes == 2, "disabling startup is confirmed independently of local preferences");
        Set(more, "_readStartup", (Func<bool>)(() => throw new InvalidOperationException("private-marker")));
        Wait((System.Threading.Tasks.Task)Call(more, "UpdateStartupAsync", false)!);
        Require(check.CheckState == CheckState.Indeterminate && check.Enabled,
            "scheduler read failures remain unknown and allow retry");
        Set(more, "_readStartup", (Func<bool>)(() => false));
        Wait((System.Threading.Tasks.Task)Call(more, "UpdateStartupAsync", true)!);
        Require(check.CheckState == CheckState.Unchecked && writes == 2,
            "retrying an unknown state reads first rather than implicitly enabling startup");
        string logs = File.ReadAllText(Logger.logFile);
        Require(!logs.Contains("private-marker"), "startup errors do not write raw exception text to logs");
    }

    private static void CheckStartupIntegration()
    {
        // Opt-in round trip; use only a unique task and a harmless executable, then remove that task.
        string name = "YALM_StartupTest_" + Guid.NewGuid().ToString("N");
        string path = Path.Combine(Environment.SystemDirectory, "where.exe");
        object startup = NewStartup(name, path);
        try
        {
            Require(!(bool)StartupCall(startup, "IsScheduled")!, "a missing task is initially off");
            StartupCall(startup, "CheckPath");
            Require(!(bool)StartupCall(startup, "IsScheduled")!, "startup repair never creates a missing task");
            Require((bool)StartupCall(startup, "SetEnabled", true)! && (bool)StartupCall(startup, "IsScheduled")!,
                "real scheduler registration and status read succeed");
            using (var service = new TaskService())
            using (var task = service.GetTask(name)) task.Enabled = false;
            StartupCall(startup, "CheckPath");
            Require(!(bool)StartupCall(startup, "IsScheduled")!, "startup repair preserves an externally disabled task");
            StartupCall(startup, "SetEnabled", true);
            using (var service = new TaskService())
            using (var definition = service.GetTask(name).Definition)
            {
                ((ExecAction)definition.Actions[0]).Path = @"C:\missing-yalm-autostart\YALM.exe";
                using var task = service.RootFolder.RegisterTaskDefinition(name, definition);
            }
            StartupCall(startup, "CheckPath");
            using (var service = new TaskService())
            using (var task = service.GetTask(name))
                Require(((ExecAction)task.Definition.Actions[0]).Path == path, "real scheduler path repair restores a moved executable");
            Require(!(bool)StartupCall(startup, "SetEnabled", false)!, "real scheduler disable removes the isolated task");
            StartupCall(startup, "SetEnabled", false);
        }
        finally
        {
            using var service = new TaskService();
            service.RootFolder?.DeleteTask(name, false);
        }
    }

    private static void CheckStartupSingleInstance()
    {
        // Use only the demo coordinator; leave an existing interactive demo untouched.
        using var mutex = new Mutex(true, @"Local\MHelper.Mechrevo.Demo", out bool ownsMutex);
        if (!ownsMutex) { Console.WriteLine("SKIP: an interactive demo already owns the demo coordinator"); return; }
        try
        {
            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MHelper.Mechrevo.ShowWindow.Demo");
            int RunDuplicate(params string[] arguments)
            {
                var info = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add(MainForm.Assembly.Location);
                foreach (string argument in arguments) info.ArgumentList.Add(argument);
                using var process = System.Diagnostics.Process.Start(info)!;
                if (!process.WaitForExit(5000)) throw new TimeoutException("Duplicate launch did not exit.");
                return process.ExitCode;
            }
            Require(RunDuplicate("--demo", "--autostart") == 0 && !signal.WaitOne(0),
                "a duplicate login launch exits quietly without showing the running window");
            Require(RunDuplicate("--demo") == 0 && signal.WaitOne(0),
                "a duplicate manual launch still activates the running window");
        }
        finally { mutex.ReleaseMutex(); }
    }
}
