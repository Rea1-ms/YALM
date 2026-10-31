using Microsoft.Win32.TaskScheduler;
using System.Diagnostics;
using System.Security.Principal;

namespace YALM.Mechrevo;

// Adapted from G-Helper's Helpers/Startup.cs. Keep YALM tasks separate from ASUS tasks.
internal sealed class MechrevoStartup
{
    internal const string Argument = "--autostart";
    private readonly string _taskName;
    private readonly string _executablePath;
    private readonly string _userId;

    public MechrevoStartup()
    {
        using var identity = WindowsIdentity.GetCurrent();
        _userId = identity.User!.Value;
        _taskName = "YALM_" + _userId;
        _executablePath = Application.ExecutablePath;
    }

    internal MechrevoStartup(string taskName, string executablePath, string userId)
    {
        _taskName = taskName;
        _executablePath = executablePath;
        _userId = userId;
    }

    public bool IsScheduled()
    {
        using var service = OpenService();
        using var task = service.GetTask(_taskName);
        return task?.Enabled == true && task.Definition.Triggers.Any(trigger => trigger.Enabled && trigger is LogonTrigger);
    }

    public bool SetEnabled(bool enabled)
    {
        using (var service = OpenService())
        {
            if (enabled) Register(service);
            else service.RootFolder.DeleteTask(_taskName, false);
        }
        bool actual = IsScheduled();
        if (actual != enabled) throw new InvalidOperationException("Startup task state was not confirmed.");
        Logger.WriteLine(enabled ? "已开启登录自启动。" : "已关闭登录自启动。");
        return actual;
    }

    private TaskDefinition CreateDefinition(TaskService service)
    {
        var definition = service.NewTask();
        definition.RegistrationInfo.Description = "YALM 登录自启动";
        definition.Triggers.Add(new LogonTrigger { UserId = _userId, Delay = TimeSpan.FromSeconds(1) });
        // Match upstream: reconnecting a switched session, rather than every unlock.
        definition.Triggers.Add(new SessionStateChangeTrigger
        {
            StateChange = TaskSessionStateChangeType.ConsoleConnect, UserId = _userId,
            Delay = TimeSpan.FromSeconds(1)
        });
        definition.Actions.Add(new ExecAction(_executablePath, Argument, Path.GetDirectoryName(_executablePath)));
        definition.Principal.UserId = _userId;
        definition.Principal.LogonType = TaskLogonType.InteractiveToken;
        // The application and its existing hardware access run without elevation.
        definition.Principal.RunLevel = TaskRunLevel.LUA;
        definition.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;
        return definition;
    }

    private void Register(TaskService service)
    {
        using var definition = CreateDefinition(service);
        using var task = service.RootFolder.RegisterTaskDefinition(_taskName, definition,
            TaskCreation.CreateOrUpdate, _userId, null, TaskLogonType.InteractiveToken);
    }

    public void CheckPath()
    {
        using var service = OpenService();
        using var task = service.GetTask(_taskName);
        // A disabled or missing task must stay disabled, including after a program update.
        if (task?.Enabled != true || !task.Definition.Triggers.Any(trigger => trigger.Enabled && trigger is LogonTrigger) ||
            task.Definition.Actions.FirstOrDefault() is not ExecAction action ||
            !NeedsPathUpdate(_executablePath, action.Path)) return;
        Register(service);
        Logger.WriteLine("已更新登录自启动的程序位置。");
    }

    private static TaskService OpenService()
    {
        var service = new TaskService();
        using var root = service.RootFolder;
        if (root is not null) return service;
        service.Dispose();
        throw new InvalidOperationException("The Task Scheduler root folder is unavailable.");
    }

    private static bool NeedsPathUpdate(string currentPath, string scheduledPath)
    {
        if (string.Equals(currentPath, scheduledPath, StringComparison.OrdinalIgnoreCase)) return false;
        if (!File.Exists(scheduledPath)) return true;
        // Do not replace a newer installed copy when an older copy is opened manually.
        static Version VersionOf(string path)
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        }
        return VersionOf(currentPath) > VersionOf(scheduledPath);
    }

    internal static void LogFailure(Exception error) =>
        Logger.WriteLine($"登录自启动操作失败：{error.GetType().Name} (0x{error.HResult:X8})");
}
