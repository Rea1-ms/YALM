using System.Diagnostics;

namespace YALM.Mechrevo;

/// <summary>
/// Read-only detection of the Mechrevo control center processes that can
/// compete for the vendor control path. It never stops a process or changes
/// its services.
/// </summary>
internal static class MechrevoControlConflictDetector
{
    internal sealed record Observation(string Name, int? SessionId, bool HasExited = false);

    internal sealed record Status(IReadOnlyList<string> RunningProcesses, bool InspectionFailed = false)
    {
        public bool IsConflict => RunningProcesses.Count > 0;

        public string DisplayName => RunningProcesses.Count == 0
            ? "无"
            : string.Join("、", RunningProcesses);

        public string? BlockReason => IsConflict
            ? $"检测到机械革命控制中心或其他控制界面正在运行：{DisplayName}。请退出控制台及其托盘后重试，保留厂商后台服务运行。"
            : InspectionFailed ? "暂时无法确认机械革命控制中心的运行状态，已暂停写入；稍后会自动重试检测。" : null;

        public string? StatusText => IsConflict ? "控制台正在运行，请退出控制台及托盘后重试。"
            : InspectionFailed ? "控制台检测未完成，暂时只读。" : null;

        public string LogSummary => $"机械革命控制中心冲突检测：命中={DisplayName}；检查完整={!InspectionFailed}";
    }

    internal static Status Read(Func<IEnumerable<Observation>>? readProcesses = null, Func<int>? readSession = null)
    {
        var running = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        bool inspectionFailed = false;
        try
        {
            int session = (readSession ?? ReadCurrentSession)();
            if (session < 0) return new Status([], true);
            foreach (Observation process in (readProcesses ?? ReadProcesses)())
            {
                if (process.HasExited || !IsControlCenterProcess(process.Name)) continue;
                if (process.SessionId is null) inspectionFailed = true;
                else if (process.SessionId == session) running.Add(process.Name);
            }
        }
        catch { inspectionFailed = true; }
        return new Status(running.ToArray(), inspectionFailed);
    }

    internal static bool IsControlCenterProcess(string name) =>
        name.Equals("CCUWinUI", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SystrayComponent", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GCUUI", StringComparison.OrdinalIgnoreCase) ||
        // Retain the existing control-app compatibility rules alongside the official UI names.
        name.Contains("ControlCenter", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("GamingCenter", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("L-Mechrevo", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("LMechrevo", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("RevoHelper", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("MechrevoControl", StringComparison.OrdinalIgnoreCase);

    private static int ReadCurrentSession()
    {
        using Process current = Process.GetCurrentProcess();
        return current.SessionId;
    }

    private static IEnumerable<Observation> ReadProcesses()
    {
        Process[] processes = Process.GetProcesses();
        var observations = new List<Observation>();
        try
        {
            foreach (Process process in processes)
            {
                string name;
                try { name = process.ProcessName; }
                catch (InvalidOperationException) { continue; }
                // Do not inspect service handles or executable versions for unrelated processes.
                if (!IsControlCenterProcess(name)) continue;
                try
                {
                    if (!process.HasExited) observations.Add(new(name, process.SessionId));
                }
                catch (InvalidOperationException) { /* The process exited during inspection. */ }
                catch { observations.Add(new(name, null)); }
            }
        }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
        return observations;
    }
}
