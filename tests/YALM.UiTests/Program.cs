using System.Diagnostics;
using System.Reflection;
using YALM.Mechrevo;

namespace YALM.UiTests;

internal static partial class Program
{
    private static readonly Type MainForm = typeof(GcuController).Assembly.GetType("YALM.Mechrevo.MechrevoForm")!;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--96") || args.Contains("--per-monitor"))
            {
                // Keep alternate scaling modes for isolated layout stress checks.
                Application.SetHighDpiMode(args.Contains("--96") ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
            }
            else
            {
                MainForm.Assembly.GetType("YALM.Mechrevo.MechrevoProgram")!
                    .GetMethod("InitializeUi", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
                Require(Application.HighDpiMode == HighDpiMode.SystemAware, "production UI uses upstream whole-window DPI scaling");
            }
            Logger.appPath = Path.GetFullPath(".cache/service-logging-20261026/logs");
            Logger.logFile = Path.Combine(Logger.appPath, "log.txt");
            if (args.Contains("--theme-only"))
            {
                CheckDisabledControlThemes(args.Contains("--render"));
                Console.WriteLine($"PASS: {_checks} disabled control rendering checks");
                return 0;
            }
            CheckStartup();
            if (args.Contains("--startup-integration")) CheckStartupIntegration();
            if (args.Contains("--startup-only"))
            {
                Console.WriteLine($"PASS: {_checks} startup checks");
                return 0;
            }
            CheckMainLayout(args.Contains("--render"));
            if (args.Contains("--layout-only"))
            {
                Console.WriteLine($"PASS: {_checks} offline window layout checks");
                return 0;
            }
            CheckDisabledControlThemes(args.Contains("--render"));
            CheckDiagnostics();
            CheckControlConflicts();
            CheckBridgePackages(args);
            CheckPlatforms(args);
            CheckRemainingWorkers(args);
            CheckNativeHardware();
            CheckNativeDisplay();
            CheckMenuSafety();
            CheckQuickSwitches(args.Contains("--render"));
            CheckTrayLifecycle();
            CheckSmokeExit();
            CheckSystemExit();
            Console.WriteLine($"PASS: {_checks} offline tray and window checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static Form CreateForm(bool demo = false, bool smoke = false, bool startup = false)
    {
        Type machineType = typeof(GcuController).Assembly.GetType("YALM.Mechrevo.MachineInfo")!;
        object machine = Activator.CreateInstance(machineType, "MECHREVO", "Offline test", "", "GM6PX0X")!;
        var form = (Form)Activator.CreateInstance(MainForm, machine, demo, smoke, startup)!;
        SetControlConflict(form, []);
        Call(form, "RefreshMachineBlockReason");
        return form;
    }

    private static void CheckMenuSafety()
    {
        using var form = CreateForm(); // Never shown: no real service, EC reader or tray registration.
        var transport = new OfflineTransport();
        var controller = new GcuController(transport);
        try
        {
            Set(form, "_controller", controller);
            Set(form, "_connectionOptions", new GcuConnectionOptions("offline", "offline", "offline") { BridgeVersionKnown = false });
            Set(form, "_machineBlockReason", null);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var baseline = new GcuSnapshot
            {
                IsConnected = true, Mode = 1, ModeUpdatedAt = now, IsAc = true, IsAcUpdatedAt = now,
                TurboSupported = true, TurboSupportedUpdatedAt = now,
                CpuFanRpm = 2000, CpuFanUpdatedAt = now, GpuFanRpm = 2100, GpuFanUpdatedAt = now,
                GpuTarget = GpuDisplayRoute.Direct, GpuTargetUpdatedAt = now,
                GpuSwitchSupported = true, GpuSwitchSupportUpdatedAt = now,
                CustomSettings = new GcuCustomSettings { FanBoostEnabled = false }, CustomSettingsUpdatedAt = now,
                FanTable = new FanCurveTable(true, "offline", false, null, null, [], []), FanTableUpdatedAt = now
            };
            Set(controller, "_snapshot", baseline);
            Set(form, "_gpuRoute", new GpuRouteSnapshot { Route = GpuDisplayRoute.Hybrid, UpdatedAt = now });
            Call(form, "RenderSnapshot");
            Require(!Get<NotifyIcon>(form, "_trayIcon").Visible, "constructing a form does not register a tray icon");
            Icon? currentIcon = Get<NotifyIcon>(form, "_trayIcon").Icon;
            Call(form, "RenderSnapshot");
            Require(ReferenceEquals(currentIcon, Get<NotifyIcon>(form, "_trayIcon").Icon), "unchanged state reuses the existing tray icon");
            var modes = Get<Dictionary<PerformanceMode, ToolStripMenuItem>>(form, "_trayModes");
            var gpu = Get<Dictionary<GpuDisplayRoute, ToolStripMenuItem>>(form, "_trayGpuModes");
            Require(modes[PerformanceMode.Balanced].Checked && modes.Values.All(item => item.Enabled), "fresh modes are available and actual mode is checked");
            Require(Get<Control>(form, "_openCustomMode").Enabled,
                "an unknown bridge does not block opening the custom page on supported hardware");
            Require(gpu[GpuDisplayRoute.Hybrid].Checked && !gpu[GpuDisplayRoute.Direct].Checked &&
                gpu[GpuDisplayRoute.Direct].Text?.Contains("待重启") == true, "GPU check follows actual route while saved target is marked pending");
            Require(!Get<ToolStripMenuItem>(form, "_trayFanBoost").Enabled, "preset mode cannot toggle custom fan boost");
            Wait((Task)Call(form, "ToggleTrayFanBoostAsync")!);
            Require(transport.Publishes == 0, "disabled fan boost entry sends no command");
            Set(controller, "_snapshot", baseline with { Mode = 3 });
            Call(form, "RenderSnapshot");
            Require(Get<ToolStripMenuItem>(form, "_trayFanBoost").Enabled, "fresh custom mode allows fan boost");
            foreach (string busy in new[] { "_switching", "_keyboardBusy", "_connecting" })
            {
                Set(form, busy, true);
                Call(form, "RenderSnapshot");
                Require(modes.Values.All(item => !item.Enabled) && gpu.Values.All(item => !item.Enabled) &&
                    !Get<ToolStripMenuItem>(form, "_trayFanBoost").Enabled, busy + " blocks tray commands");
                Set(form, busy, false);
            }
            Set(form, "_ecSnapshot", new EcFanSnapshot { CpuFanRpm = 2000, GpuFanRpm = 2100, UpdatedAt = now, GenericThermalProtect = true });
            Call(form, "RenderSnapshot");
            Require(modes.Values.All(item => !item.Enabled) && !Get<ToolStripMenuItem>(form, "_trayFanBoost").Enabled, "thermal protection blocks performance and fan boost");
            Set(form, "_ecSnapshot", null);
            foreach (GcuSnapshot stale in new[]
            {
                baseline with { Mode = 3, CustomSettingsUpdatedAt = now.AddMinutes(-1) },
                baseline with { Mode = 3, FanTableUpdatedAt = now.AddMinutes(-1) },
                baseline with { Mode = 3, CpuFanUpdatedAt = now.AddMinutes(-1) },
                baseline with { Mode = 3, CustomSettings = new GcuCustomSettings() },
                baseline with { IsConnected = false }
            })
            {
                Set(controller, "_snapshot", stale);
                Call(form, "RenderSnapshot");
                Require(!Get<ToolStripMenuItem>(form, "_trayFanBoost").Enabled, "stale, unknown or disconnected data disables fan boost");
            }
            Set(controller, "_snapshot", baseline with { ModeUpdatedAt = now.AddMinutes(-1), GpuTargetUpdatedAt = now.AddMinutes(-1) });
            Call(form, "RenderSnapshot");
            Require(modes.Values.All(item => !item.Enabled && !item.Checked) && gpu.Values.All(item => !item.Enabled), "expired mode data is neither selected nor writable");
            Set(form, "_gpuRoute", new GpuRouteSnapshot { Route = GpuDisplayRoute.Direct, UpdatedAt = now.AddMinutes(-1) });
            Call(form, "RenderSnapshot");
            Require(gpu.Values.All(item => !item.Checked), "expired actual GPU route clears check marks");
            Wait((Task)Call(form, "SwitchGpuModeAsync", GpuDisplayRoute.Direct)!);
            Require(transport.Publishes == 0, "stale route rejects a direct handler invocation too");
            CheckIcon(form, GpuDisplayRoute.Hybrid, false, false, "standard");
            CheckIcon(form, GpuDisplayRoute.Direct, false, false, "ultimate");
            CheckIcon(form, GpuDisplayRoute.Unknown, false, false, "yalm");
            CheckIcon(form, GpuDisplayRoute.Direct, true, true, "light_standard");
            CheckIcon(form, GpuDisplayRoute.Hybrid, true, false, "dark_standard");
        }
        finally { controller.DisposeAsync().AsTask().GetAwaiter().GetResult(); Set(form, "_controller", null); }
    }

    private static void CheckIcon(Form form, GpuDisplayRoute route, bool bw, bool dark, string resource)
    {
        var resources = typeof(GcuController).Assembly.GetType("GHelper.Properties.Resources")!;
        using var expected = (Icon)resources.GetProperty(resource, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        using var actual = (Icon)MainForm.GetMethod("TrayIconFor", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [route, bw, dark])!;
        using var expectedData = new MemoryStream();
        using var actualData = new MemoryStream();
        expected.Save(expectedData);
        actual.Save(actualData);
        Require(expectedData.ToArray().SequenceEqual(actualData.ToArray()), $"icon selection {route}/{bw}/{dark}");
    }

    private static void CheckTrayLifecycle()
    {
        using var form = CreateForm(demo: true);
        string eventName = @"Local\YALM.UiTests." + Guid.NewGuid().ToString("N");
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        Call(form, "SetActivationSignal", signal);
        form.Show();
        Application.DoEvents();
        NotifyIcon icon = Get<NotifyIcon>(form, "_trayIcon");
        Require(icon.Visible, "showing an interactive form registers its tray icon");
        Call(form, "OpenCustomMode");
        Form custom = Get<Form>(form, "_customModeForm");
        form.Close();
        Require(!form.Visible && !custom.Visible && !form.IsDisposed && icon.Visible, "window X hides the main and owned editor but keeps the tray");
        Require(Get<System.Windows.Forms.Timer>(form, "_refreshTimer").Enabled &&
            !Get<CancellationTokenSource>(form, "_lifetime").IsCancellationRequested, "hidden form keeps refresh and connection lifetime");
        using (var secondInstanceSignal = EventWaitHandle.OpenExisting(eventName)) secondInstanceSignal.Set();
        Call(form, "CheckTraySignals");
        Require(form.Visible, "single-instance activation signal restores the hidden window");
        Call(form, "OpenCustomMode");
        Require(custom.Visible, "previously hidden fan editor can reopen");
        Call(form, "ToggleMainWindow");
        Require(!form.Visible, "tray left click hides the visible window");
        Call(form, "ToggleMainWindow");
        Require(form.Visible, "tray left click restores the hidden window");
        int taskbarMessage = Convert.ToInt32(MainForm.GetField("TaskbarCreated", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        Call(form, "HandleTrayMessage", taskbarMessage);
        Require(icon.Visible, "shell recreation re-registers the tray icon");
        Call(form, "HandleTrayMessage", 0x031A);
        Require(icon.Visible, "theme refresh keeps the tray registered");
        var menu = Get<ContextMenuStrip>(form, "_trayMenu");
        menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "退出").PerformClick();
        PumpUntil(() => form.IsDisposed);
        Require(!icon.Visible && menu.IsDisposed && custom.IsDisposed, "explicit exit removes tray, menu and child windows");
    }

    private static void CheckSmokeExit()
    {
        using var form = CreateForm(demo: true, smoke: true);
        form.Show();
        Require(!Get<NotifyIcon>(form, "_trayIcon").Visible, "automated smoke run does not register a tray icon");
        PumpUntil(() => form.IsDisposed);
        Require(form.IsDisposed, "smoke timer exits instead of hiding");
    }

    private static void CheckSystemExit()
    {
        using var form = CreateForm(demo: true);
        var args = new FormClosingEventArgs(CloseReason.WindowsShutDown, false);
        Call(form, "OnFormClosing", form, args);
        Require(!args.Cancel, "system shutdown is not cancelled or hidden to tray");
    }

    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethods(Private)
        .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(target, args);
    private static void Wait(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> done)
    {
        var elapsed = Stopwatch.StartNew();
        while (!done() && elapsed.Elapsed < TimeSpan.FromSeconds(8)) { Application.DoEvents(); Thread.Sleep(10); }
        if (!done()) throw new TimeoutException("Offline UI action timed out.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }

    private sealed class OfflineTransport : IGcuTransport
    {
        public bool IsConnected => true;
        public int Publishes { get; private set; }
        public event Action<string, string>? MessageReceived { add { } remove { } }
        public event Action? Disconnected { add { } remove { } }
        public Task ConnectAsync(GcuConnectionOptions options, CancellationToken ct) => throw new InvalidOperationException("No real connection allowed.");
        public Task SubscribeAsync(IReadOnlyCollection<string> topics, CancellationToken ct) => Task.CompletedTask;
        public Task PublishAsync(string topic, string payload, CancellationToken ct) { Publishes++; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
