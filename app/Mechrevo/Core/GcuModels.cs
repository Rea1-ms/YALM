using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace YALM.Mechrevo;

public enum PerformanceMode { Silent = 0, Balanced = 1, Turbo = 2 }

public sealed record GcuConnectionOptions(string ClientId, string UserName, string Password, int Port = 13688)
{
    // Read-only capability supplied by the platform adapter, never inferred from a current mode.
    public bool? TurboSupported { get; init; }

    // Avoid exposing credentials through record-generated logging.
    public override string ToString() => $"GcuConnectionOptions {{ ClientId = {ClientId}, Port = {Port} }}";
}

public interface IGcuTransport : IAsyncDisposable
{
    bool IsConnected { get; }
    /// <summary>Only live, non-retained messages from the current connection are delivered.</summary>
    event Action<string, string>? MessageReceived;
    event Action? Disconnected;
    Task ConnectAsync(GcuConnectionOptions options, CancellationToken ct);
    Task SubscribeAsync(IReadOnlyCollection<string> topics, CancellationToken ct);
    Task PublishAsync(string topic, string payload, CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
}

public enum ModeSwitchStatus { ServiceConfirmed, Rejected, Busy, TimedOut, Disconnected, Cancelled, Failed }

/// <summary>A service readback is not independent verification of firmware or fan operation.</summary>
public sealed record ModeSwitchResult(ModeSwitchStatus Status, string Message)
{
    public bool ServiceConfirmed => Status == ModeSwitchStatus.ServiceConfirmed;
}

/// <summary>Immutable, per-field observations. Missing and rejected values never become zero.</summary>
public sealed record GcuSnapshot
{
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(10);
    public bool IsConnected { get; init; }
    public string? LastError { get; init; }
    public int? Mode { get; init; }
    public int? CpuFanRpm { get; init; }
    public int? GpuFanRpm { get; init; }
    public int? CpuFanDuty { get; init; }
    public int? GpuFanDuty { get; init; }
    public int? CpuTemperature { get; init; }
    public int? GpuTemperature { get; init; }
    public bool? IsAc { get; init; }
    public bool? TurboSupported { get; init; }
    public GcuCustomSettings? CustomSettings { get; init; }
    public FanCurveTable? FanTable { get; init; }
    public GcuKeyboardSettings? Keyboard { get; init; }
    public bool? FnLocked { get; init; }
    public int? CpuUsage { get; init; }
    public int? GpuUsage { get; init; }
    public DateTimeOffset? CpuUsageUpdatedAt { get; init; }
    public DateTimeOffset? GpuUsageUpdatedAt { get; init; }
    public DateTimeOffset? KeyboardUpdatedAt { get; init; }
    public DateTimeOffset? FnLockUpdatedAt { get; init; }
    public DateTimeOffset? ModeUpdatedAt { get; init; }
    public DateTimeOffset? CpuFanUpdatedAt { get; init; }
    public DateTimeOffset? GpuFanUpdatedAt { get; init; }
    public DateTimeOffset? CpuFanDutyUpdatedAt { get; init; }
    public DateTimeOffset? GpuFanDutyUpdatedAt { get; init; }
    public DateTimeOffset? CpuTemperatureUpdatedAt { get; init; }
    public DateTimeOffset? GpuTemperatureUpdatedAt { get; init; }
    public DateTimeOffset? IsAcUpdatedAt { get; init; }
    public DateTimeOffset? TurboSupportedUpdatedAt { get; init; }
    public DateTimeOffset? CustomSettingsUpdatedAt { get; init; }
    public DateTimeOffset? FanTableUpdatedAt { get; init; }

    public bool IsStatusFresh(DateTimeOffset? now = null) =>
        Mode.HasValue && IsAc.HasValue && Fresh(ModeUpdatedAt, now) && Fresh(IsAcUpdatedAt, now);
    public bool IsFanFresh(DateTimeOffset? now = null) =>
        (IsCpuFanFresh(now) || IsCpuFanDutyFresh(now)) && (IsGpuFanFresh(now) || IsGpuFanDutyFresh(now));
    public bool IsCpuFanFresh(DateTimeOffset? now = null) => CpuFanRpm.HasValue && Fresh(CpuFanUpdatedAt, now);
    public bool IsGpuFanFresh(DateTimeOffset? now = null) => GpuFanRpm.HasValue && Fresh(GpuFanUpdatedAt, now);
    public bool IsCpuFanDutyFresh(DateTimeOffset? now = null) => CpuFanDuty.HasValue && Fresh(CpuFanDutyUpdatedAt, now);
    public bool IsGpuFanDutyFresh(DateTimeOffset? now = null) => GpuFanDuty.HasValue && Fresh(GpuFanDutyUpdatedAt, now);
    public bool IsCpuTemperatureFresh(DateTimeOffset? now = null) => CpuTemperature.HasValue && Fresh(CpuTemperatureUpdatedAt, now);
    public bool IsGpuTemperatureFresh(DateTimeOffset? now = null) => GpuTemperature.HasValue && Fresh(GpuTemperatureUpdatedAt, now);
    public bool IsCustomSettingsFresh(DateTimeOffset? now = null) =>
        CustomSettings is not null && Fresh(CustomSettingsUpdatedAt, now);
    public bool IsFanTableFresh(DateTimeOffset? now = null) =>
        FanTable is not null && Fresh(FanTableUpdatedAt, now);
    public bool IsKeyboardFresh(DateTimeOffset? now = null) => Keyboard is not null && Fresh(KeyboardUpdatedAt, now);
    public bool IsFnLockFresh(DateTimeOffset? now = null) => FnLocked.HasValue && Fresh(FnLockUpdatedAt, now);
    public bool IsCpuUsageFresh(DateTimeOffset? now = null) => CpuUsage.HasValue && Fresh(CpuUsageUpdatedAt, now);
    public bool IsGpuUsageFresh(DateTimeOffset? now = null) => GpuUsage.HasValue && Fresh(GpuUsageUpdatedAt, now);
    public bool CanSwitchMode(PerformanceMode mode, DateTimeOffset? now = null) =>
        Enum.IsDefined(mode) && IsStatusFresh(now) && IsFanFresh(now) &&
        (mode != PerformanceMode.Turbo || (TurboSupported == true && IsAc == true));

    private bool Fresh(DateTimeOffset? observed, DateTimeOffset? now)
    {
        if (!IsConnected || !observed.HasValue) return false;
        TimeSpan age = (now ?? DateTimeOffset.UtcNow) - observed.Value;
        return age >= TimeSpan.Zero && age <= Freshness;
    }
}
