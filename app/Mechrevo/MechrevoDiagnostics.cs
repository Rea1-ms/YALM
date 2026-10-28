using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace YALM.Mechrevo;

/// <summary>Only records selected metadata and numeric errors, never arbitrary exception text.</summary>
internal static class MechrevoDiagnostics
{
    public static void WriteStartup(MachineInfo machine, int dpi)
    {
        Logger.WriteLine("----------------------");
        Logger.WriteLine($"助手启动：版本={typeof(MechrevoDiagnostics).Assembly.GetName().Version}；系统={Environment.OSVersion.Version}；运行时={Environment.Version}；进程架构={RuntimeInformation.ProcessArchitecture}");
        Logger.WriteLine($"机型：{OneLine(machine.DisplayName)}；主板={OneLine(machine.BaseboardProduct)}；固件={OneLine(machine.BiosVersion)}；控制协议已适配={machine.SupportsModeControl}；转速读取已适配={machine.SupportsEcTelemetry}；协议来源={(machine.HasVerifiedBoard ? "已验证主板" : machine.ProtocolProfile ?? "未知")}");
        Logger.WriteLine($"界面：DPI={dpi}；缩放={dpi * 100 / 96}%");
        WriteConsolePackages(Logger.WriteLine);
        WriteHardwareWorker(Logger.WriteLine);
        WriteService("UWACPIDriver", Logger.WriteLine);
    }

    public static void WriteService(string name, Action<string>? log)
    {
        if (log is null) return;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name, false);
            if (key is null) { log($"厂商服务 {name}：未安装。"); return; }
            log($"厂商服务 {name}：已安装；状态={ReadServiceState(name)}");
            string? path = ServiceExecutable(key.GetValue("ImagePath")?.ToString());
            if (path is not null && File.Exists(path)) WriteFileVersion(path, log);
        }
        catch (Exception exception) { log($"厂商服务 {name}：信息读取失败；{ExceptionSummary(exception)}"); }
    }

    internal static string? ServiceExecutable(string? command)
    {
        string expanded = Environment.ExpandEnvironmentVariables(command ?? "").Trim();
        int end = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (end < 0) return null;
        string path = expanded[..(end + 4)].Trim('"');
        return Path.IsPathFullyQualified(path) ? path : null;
    }

    public static void WriteFileVersion(string path, Action<string>? log)
    {
        if (log is null) return;
        try
        {
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
            // Numeric fields exclude arbitrary strings from an unrecognised binary.
            log($"厂商组件：文件版本={version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}.{version.FilePrivatePart}；产品版本={version.ProductMajorPart}.{version.ProductMinorPart}.{version.ProductBuildPart}.{version.ProductPrivatePart}");
        }
        catch (Exception exception) { log("组件版本读取失败；" + ExceptionSummary(exception)); }
    }

    public static string ExceptionSummary(Exception exception)
    {
        var errors = new List<string>();
        for (Exception? current = exception; current is not null && errors.Count < 5; current = current.InnerException)
        {
            string detail = $"{current.GetType().Name}(0x{current.HResult:X8})";
            if (current is SocketException socket) detail += $"；网络错误={socket.SocketErrorCode}({socket.NativeErrorCode})";
            else if (current is Win32Exception native) detail += $"；系统错误={native.NativeErrorCode}";
            errors.Add(detail);
        }
        return string.Join(" → ", errors);
    }

    private static string OneLine(string value) => new(value.Where(character => !char.IsControl(character)).Take(160).ToArray());

    private static void WriteConsolePackages(Action<string> log)
    {
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages", false);
            string[] names = packages?.GetSubKeyNames() ?? [];
            int count = 0;
            foreach (string name in names)
            {
                string[] parts = name.Split('_');
                if (parts.Length < 2 || !Version.TryParse(parts[1], out var version)) continue;
                if (!parts[0].StartsWith("GamingCenter", StringComparison.OrdinalIgnoreCase) &&
                    !parts[0].StartsWith("ControlCenter", StringComparison.OrdinalIgnoreCase) &&
                    !parts[0].Contains("CCU.WinUI", StringComparison.OrdinalIgnoreCase) &&
                    !parts[0].Contains("GCUUI", StringComparison.OrdinalIgnoreCase)) continue;
                log($"当前用户控制中心包：{OneLine(parts[0])}；版本={version}");
                count++;
            }
            if (count == 0) log("当前用户控制中心包：注册表中未发现匹配项，不能据此确认未安装。");
        }
        catch (Exception exception) { log("控制中心包版本读取失败；" + ExceptionSummary(exception)); }
    }

    private static void WriteHardwareWorker(Action<string> log)
    {
        try
        {
            // In the original package this is a worker process, not a registered service.
            Process[] workers = Process.GetProcessesByName("GCUService");
            log($"硬件工作进程 GCUService：运行数量={workers.Length}（未运行不代表组件未安装）");
            foreach (Process worker in workers)
            {
                using (worker)
                {
                    try
                    {
                        if (worker.MainModule?.FileName is string path) WriteFileVersion(path, log);
                    }
                    catch (Exception exception) { log("硬件工作进程版本读取失败；" + ExceptionSummary(exception)); }
                }
            }
        }
        catch (Exception exception) { log("硬件工作进程状态读取失败；" + ExceptionSummary(exception)); }
    }

    private static string ReadServiceState(string name)
    {
        nint manager = OpenSCManager(null, null, 1); // SC_MANAGER_CONNECT
        if (manager == 0) return $"不可读取（系统错误 {Marshal.GetLastWin32Error()}）";
        try
        {
            nint service = OpenService(manager, name, 4); // SERVICE_QUERY_STATUS only
            if (service == 0) return $"不可读取（系统错误 {Marshal.GetLastWin32Error()}）";
            try
            {
                if (!QueryServiceStatus(service, out ServiceStatus status)) return $"不可读取（系统错误 {Marshal.GetLastWin32Error()}）";
                return status.CurrentState switch
                {
                    1 => "已停止", 2 => "正在启动", 3 => "正在停止", 4 => "运行中",
                    5 => "正在恢复", 6 => "正在暂停", 7 => "已暂停", _ => $"未知（{status.CurrentState}）"
                };
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenService(nint manager, string name, uint desiredAccess);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(nint service, out ServiceStatus status);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);
}
