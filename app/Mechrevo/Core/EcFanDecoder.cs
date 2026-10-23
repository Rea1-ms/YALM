using System;
using System.Collections.Generic;

namespace YALM.Mechrevo;

public static class EcFanDecoder
{
    public const int MaximumReasonableRpm = 20000;

    public static EcFanSnapshot Decode(
        IReadOnlyDictionary<int, byte> registers,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(registers);
        foreach (int address in RequiredAddresses)
        {
            if (!registers.TryGetValue(address, out byte value))
                return Failure($"EC 风扇数据不完整（缺少地址 {address}）。", observedAt);
        }

        int cpuRpm = Word(registers[EcFanRegisters.CpuFanRpmHigh], registers[EcFanRegisters.CpuFanRpmLow]);
        int gpuRpm = Word(registers[EcFanRegisters.GpuFanRpmHigh], registers[EcFanRegisters.GpuFanRpmLow]);
        if (cpuRpm > MaximumReasonableRpm || gpuRpm > MaximumReasonableRpm)
            return Failure("EC 风扇转速超出合理范围。", observedAt);

        byte safetyFlags = registers[EcFanRegisters.FanSafetyFlags];
        byte thermalFlags = registers[EcFanRegisters.GenericThermalFlags];
        return new EcFanSnapshot
        {
            CpuFanRpm = cpuRpm,
            GpuFanRpm = gpuRpm,
            FanSafetyProtect = (safetyFlags & (1 << 3)) != 0,
            GenericThermalProtect = (thermalFlags & (1 << 4)) != 0,
            UpdatedAt = observedAt
        };
    }

    private static readonly int[] RequiredAddresses =
    [
        EcFanRegisters.CpuFanRpmHigh,
        EcFanRegisters.CpuFanRpmLow,
        EcFanRegisters.GpuFanRpmHigh,
        EcFanRegisters.GpuFanRpmLow,
        EcFanRegisters.FanSafetyFlags,
        EcFanRegisters.GenericThermalFlags
    ];

    private static int Word(byte high, byte low) => high * 256 + low;

    private static EcFanSnapshot Failure(string message, DateTimeOffset observedAt) => new()
    {
        LastError = message,
        UpdatedAt = observedAt
    };
}
