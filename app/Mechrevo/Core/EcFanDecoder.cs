using System;
using System.Collections.Generic;

namespace YALM.Mechrevo;

public static class EcFanDecoder
{
    public const int MaximumReasonableRpm = 20000;

    public static EcFanSnapshot Decode(
        IReadOnlyDictionary<int, byte> registers,
        DateTimeOffset observedAt,
        bool readProtection = true)
    {
        ArgumentNullException.ThrowIfNull(registers);
        foreach (int address in readProtection ? EcFanRegisters.AllReadAddresses : EcFanRegisters.RpmReadAddresses)
        {
            if (!registers.TryGetValue(address, out byte value))
                return Failure($"EC 风扇数据不完整（缺少地址 {address}）。", observedAt);
        }

        int cpuRpm = Word(registers[EcFanRegisters.CpuFanRpmHigh], registers[EcFanRegisters.CpuFanRpmLow]);
        int gpuRpm = Word(registers[EcFanRegisters.GpuFanRpmHigh], registers[EcFanRegisters.GpuFanRpmLow]);
        if (cpuRpm > MaximumReasonableRpm || gpuRpm > MaximumReasonableRpm)
            return Failure("EC 风扇转速超出合理范围。", observedAt);

        return new EcFanSnapshot
        {
            CpuFanRpm = cpuRpm,
            GpuFanRpm = gpuRpm,
            FanSafetyProtect = readProtection ? (registers[EcFanRegisters.FanSafetyFlags] & (1 << 3)) != 0 : null,
            GenericThermalProtect = readProtection ? (registers[EcFanRegisters.GenericThermalFlags] & (1 << 4)) != 0 : null,
            UpdatedAt = observedAt
        };
    }

    private static int Word(byte high, byte low) => high * 256 + low;

    private static EcFanSnapshot Failure(string message, DateTimeOffset observedAt) => new()
    {
        LastError = message,
        UpdatedAt = observedAt
    };
}
