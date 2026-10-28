using System;
using System.Collections.Generic;

namespace YALM.Mechrevo;

public enum JiaolongReadCommand : byte
{
    Mode = 8,
    FanSpeeds = 13,
    CpuTemperature = 22
}

/// <summary>Read-only requests reviewed against the two official Jiaolong control centers.</summary>
public static class JiaolongReadProtocol
{
    public const string Scope = @"root\WMI";
    public const string InstancePath = @"MICommonInterface.InstanceName='ACPI\PNP0C14\MIFS_0'";
    public const string InstanceName = @"ACPI\PNP0C14\MIFS_0";
    public const string ClassName = "MICommonInterface";
    public const string MethodName = "MiInterface";
    public const string InputProperty = "InData";
    public const string OutputProperty = "OutData";
    public const string ServiceName = "BLDHotKeyService";
    public const string ImageName = "蛟龙游戏控制中心.dll";
    public const string LegacyImageHash = "8B4227DA1C37B9A649EE5EF31587498436C8C4784D7CA4DD0AE43712692C09FA";
    public const string ProImageHash = "42DE75E7E12C755B9F4C94EF10FD6CC51E3EE6A1F2F4EF8D1AFBE462E483E50A";

    public static byte[] CreateRequest(JiaolongReadCommand command, bool supportsTemperature = false)
    {
        if (!IsAllowed(command, supportsTemperature))
            throw new ArgumentOutOfRangeException(nameof(command), "Unsupported read-only Jiaolong command.");

        byte[] request = new byte[32];
        request[1] = 250;
        request[3] = (byte)command;
        return request;
    }

    /// <summary>
    /// Creates a new polling snapshot. Invalid or absent replies cannot preserve a previous
    /// value or refresh another field, and do not establish power or control capabilities.
    /// </summary>
    public static GcuSnapshot Decode(IReadOnlyDictionary<JiaolongReadCommand, byte[]?> responses,
        DateTimeOffset observedAt, bool supportsTemperature = false)
    {
        ArgumentNullException.ThrowIfNull(responses);
        GcuSnapshot snapshot = new();

        if (Response(responses, JiaolongReadCommand.Mode, 5) is { } modeReply)
        {
            int? mode = modeReply[4] switch
            {
                0 => (int)PerformanceMode.Balanced,
                1 => (int)PerformanceMode.Turbo,
                2 => (int)PerformanceMode.Silent,
                _ => null
            };
            if (mode.HasValue)
                snapshot = snapshot with { Mode = mode, ModeUpdatedAt = observedAt };
        }

        if (Response(responses, JiaolongReadCommand.FanSpeeds, 8) is { } fanReply)
        {
            int cpuRpm = fanReply[4] | fanReply[5] << 8;
            int gpuRpm = fanReply[6] | fanReply[7] << 8;
            if (cpuRpm <= EcFanDecoder.MaximumReasonableRpm)
                snapshot = snapshot with { CpuFanRpm = cpuRpm, CpuFanUpdatedAt = observedAt };
            if (gpuRpm <= EcFanDecoder.MaximumReasonableRpm)
                snapshot = snapshot with { GpuFanRpm = gpuRpm, GpuFanUpdatedAt = observedAt };
        }

        if (supportsTemperature && Response(responses, JiaolongReadCommand.CpuTemperature, 5) is { } temperatureReply &&
            temperatureReply[4] is >= 1 and <= 125)
        {
            snapshot = snapshot with
            {
                CpuTemperature = temperatureReply[4],
                CpuTemperatureUpdatedAt = observedAt
            };
        }

        return snapshot with
        {
            IsConnected = snapshot.Mode.HasValue || snapshot.CpuFanRpm.HasValue ||
                snapshot.GpuFanRpm.HasValue || snapshot.CpuTemperature.HasValue
        };
    }

    private static bool IsAllowed(JiaolongReadCommand command, bool supportsTemperature) =>
        command is JiaolongReadCommand.Mode or JiaolongReadCommand.FanSpeeds ||
        supportsTemperature && command == JiaolongReadCommand.CpuTemperature;

    private static byte[]? Response(IReadOnlyDictionary<JiaolongReadCommand, byte[]?> responses,
        JiaolongReadCommand command, int minimumLength) =>
        responses.TryGetValue(command, out byte[]? reply) && reply is not null &&
        reply.Length >= minimumLength && reply.Length <= 32 ? reply : null;
}
