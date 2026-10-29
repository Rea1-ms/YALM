using YALM.Mechrevo;

internal static partial class Program
{
    private static Task MechrevoControlConflictDetection()
    {
        foreach (string name in new[]
        {
            "CCUWinUI", "SystrayComponent", "ControlCenterU", "GamingCenterU", "GCUUI",
            "L-Mechrevo", "LMechrevo", "RevoHelper", "MechrevoControl", "ControlCenter3"
        })
        {
            var running = MechrevoControlConflictDetector.Read(() => [new(name.ToLowerInvariant(), 2)], () => 2);
            Assert.True(running.IsConflict && running.BlockReason is not null,
                $"{name} in the current session must block writes regardless of case");
            Assert.True(running.LogSummary.Contains(name.ToLowerInvariant()), "logs identify the competing process");
        }
        var background = MechrevoControlConflictDetector.Read(() =>
            new[] { "GCUBridge", "GCUService", "UWACPIDriver", "ArmouryCrate.Service", "LightingService", "YALM" }
                .Select(name => new MechrevoControlConflictDetector.Observation(name, null)), () => 2);
        Assert.True(!background.IsConflict && background.BlockReason is null,
            "vendor dependencies and unrelated ASUS services must not block control or require session inspection");

        var otherSession = MechrevoControlConflictDetector.Read(() => [new("CCUWinUI", 3), new("GCUUI", 2, true)], () => 2);
        Assert.True(!otherSession.IsConflict && otherSession.BlockReason is null,
            "other login sessions and exited processes cannot create a local console conflict");
        var failed = MechrevoControlConflictDetector.Read(() => [new("CCUWinUI", null)], () => 2);
        Assert.True(failed.InspectionFailed && failed.BlockReason is not null,
            "uninspectable control-center candidates stay explicitly unknown rather than reporting no conflict");
        var denied = MechrevoControlConflictDetector.Read(() => throw new UnauthorizedAccessException("private-path"), () => 2);
        Assert.True(denied.InspectionFailed && denied.BlockReason is not null && !denied.LogSummary.Contains("private-path"),
            "enumeration failures block safely without logging arbitrary exception details");
        var sessionFailure = MechrevoControlConflictDetector.Read(() => [], () => throw new InvalidOperationException());
        Assert.True(sessionFailure.InspectionFailed && sessionFailure.BlockReason is not null,
            "session failures cannot silently become a global or clean result");
        var duplicates = MechrevoControlConflictDetector.Read(() => [new("GCUUI", 2), new("gcuui", 2)], () => 2);
        Assert.Equal(1, duplicates.RunningProcesses.Count, "duplicate instances have one diagnostic name");
        var clear = MechrevoControlConflictDetector.Read(() => [], () => 2);
        Assert.True(!clear.IsConflict && !clear.InspectionFailed && clear.BlockReason is null,
            "a fresh successful scan clears prior conflicts and failures");
        return Task.CompletedTask;
    }
}
