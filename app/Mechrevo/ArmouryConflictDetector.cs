using System.Diagnostics;

namespace YALM.Mechrevo;

/// <summary>
/// Read-only detection of Armoury Crate processes that can compete for the
/// vendor control path. It never stops a process or changes its services.
/// </summary>
internal static class ArmouryConflictDetector
{
    internal sealed record Status(bool ArmouryCrateService, bool LightingService)
    {
        public bool IsConflict => ArmouryCrateService || LightingService;
    }

    internal static Status Read(Func<string, bool>? isRunning = null)
    {
        isRunning ??= name =>
        {
            try { return Process.GetProcessesByName(name).Length > 0; }
            catch { return false; }
        };

        bool armoury = isRunning("ArmouryCrate.Service");
        bool lighting = isRunning("LightingService");
        return new Status(armoury, lighting);
    }
}
