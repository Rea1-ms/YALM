using System.Linq.Expressions;
using System.Reflection;
using YALM.Mechrevo;

namespace YALM.UiTests;

internal static partial class Program
{
    private static void CheckControlConflicts()
    {
        using var form = CreateForm(); // Never shown or connected to real hardware.
        var transport = new OfflineTransport();
        var controller = new GcuController(transport);
        string previousLog = Logger.logFile;
        string directory = Path.GetFullPath(".cache/console-conflict-checks");
        Directory.CreateDirectory(directory);
        Logger.logFile = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".log");
        try
        {
            Set(form, "_controller", controller);
            Set(form, "_diagnosticsStarted", true);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Set(controller, "_snapshot", PlatformControlSnapshot(now) with { Mode = 3 });
            Set(form, "_ecSnapshot", new EcFanSnapshot
            {
                CpuFanRpm = 2000, GpuFanRpm = 2100, UpdatedAt = now,
                GenericThermalProtect = false, FanSafetyProtect = false
            });
            Set(form, "_gpuRoute", new GpuRouteSnapshot { Route = GpuDisplayRoute.Hybrid, UpdatedAt = now });
            SetControlConflict(form, []);
            Call(form, "RefreshMachineBlockReason");
            Call(form, "RenderSnapshot");
            Require(Get<System.Collections.IDictionary>(form, "_modeButtons").Values.Cast<Control>().All(button => button.Enabled),
                "fresh control state starts available without competing consoles");

            SetControlConflict(form, ["CCUWinUI", "SystrayComponent"]);
            // A console starting between refresh and a click is rechecked by the command handler.
            Wait((Task)Call(form, "SwitchModeAsync", PerformanceMode.Balanced)!);
            Require(transport.Publishes == 0, "a newly started console blocks mode commands before sending");
            bool keyboardRan = false;
            Func<Task<GcuWriteResult>> keyboard = () =>
            {
                keyboardRan = true;
                return Task.FromResult(new GcuWriteResult(GcuWriteStatus.Confirmed, "offline"));
            };
            Wait((Task)Call(form, "RunKeyboardAsync", keyboard)!);
            Wait((Task)Call(form, "ToggleTrayFanBoostAsync")!);
            Wait((Task)Call(form, "SwitchGpuModeAsync", GpuDisplayRoute.Direct)!);
            Require(!keyboardRan && transport.Publishes == 0, "console conflicts block keyboard, GPU and tray writes too");
            Require(Call(form, "CurrentCustomControlBlockReason") is string reason && reason.Contains("机械革命控制中心"),
                "custom controls use the same current console guard");
            Call(form, "ShowActionStatus", "已连接本机服务。");
            Set(form, "_actionMessageUntil", DateTimeOffset.UtcNow.AddMinutes(-1));
            Call(form, "RenderSnapshot");
            Require(Get<Label>(form, "_connectionStatus").Text.Contains("退出控制台及托盘"),
                "the console restriction remains visible after connection messages and transient status expiry");
            Require(Get<System.Collections.IDictionary>(form, "_modeButtons").Values.Cast<Control>().All(button => !button.Enabled) &&
                Get<Dictionary<PerformanceMode, ToolStripMenuItem>>(form, "_trayModes").Values.All(item => !item.Enabled),
                "main and tray modes visibly reflect the conflict");
            string conflictLog = File.ReadAllText(Logger.logFile);
            Require(conflictLog.Contains("命中=CCUWinUI、SystrayComponent") && conflictLog.Contains("保留厂商后台服务"),
                "diagnostics identify the actual console and explain what to exit");
            Call(form, "RefreshMachineBlockReason");
            Require(File.ReadAllText(Logger.logFile) == conflictLog, "unchanged scans do not flood the log");

            SetControlConflict(form, []);
            Call(form, "RefreshMachineBlockReason");
            Call(form, "RenderSnapshot");
            Require(Get<System.Collections.IDictionary>(form, "_modeButtons").Values.Cast<Control>().All(button => button.Enabled) &&
                Call(form, "CurrentCustomControlBlockReason") is null,
                "exiting the console releases the guard without restarting YALM");
            Require(Get<Label>(form, "_connectionStatus").Text == "已连接本机服务" &&
                File.ReadAllText(Logger.logFile).Contains("命中=无"), "console exit clears the notice and records the transition");

            SetControlConflict(form, [], failed: true);
            Call(form, "RefreshMachineBlockReason");
            Call(form, "RenderSnapshot");
            Require(Get<Label>(form, "_connectionStatus").Text.Contains("检测未完成") &&
                Get<System.Collections.IDictionary>(form, "_modeButtons").Values.Cast<Control>().All(button => !button.Enabled),
                "unknown process inspection is visibly distinct from both conflict and successful detection");
        }
        finally
        {
            Logger.logFile = previousLog;
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Set(form, "_controller", null);
        }
    }

    private static void SetControlConflict(Form form, string[] names, bool failed = false)
    {
        Type statusType = MainForm.Assembly.GetType("YALM.Mechrevo.MechrevoControlConflictDetector+Status")!;
        object status = Activator.CreateInstance(statusType, names, failed)!;
        FieldInfo source = MainForm.GetField("_readControlConflict", Private)!;
        source.SetValue(form, Expression.Lambda(source.FieldType, Expression.Constant(status, statusType)).Compile());
    }
}
