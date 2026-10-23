using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace YALM.Mechrevo;

/// <summary>Issues only the vendor driver's EC read request.</summary>
public sealed class EcFanReader
{
    private const string DevicePath = @"\\.\ACPIDriver";
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint EcReadControlCode = 0x9C40A488;
    private const int RequestDelayMilliseconds = 10;

    private static readonly int[] Addresses =
    [
        EcFanRegisters.CpuFanRpmHigh,
        EcFanRegisters.CpuFanRpmLow,
        EcFanRegisters.GpuFanRpmHigh,
        EcFanRegisters.GpuFanRpmLow,
        EcFanRegisters.FanSafetyFlags,
        EcFanRegisters.GenericThermalFlags
    ];

    private readonly object _sync = new();
    private readonly TimeProvider _time;

    public EcFanReader(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    public Task<EcFanSnapshot> ReadAsync(CancellationToken ct = default) =>
        Task.Run(() => Read(ct), ct);

    private EcFanSnapshot Read(CancellationToken ct)
    {
        lock (_sync)
        {
            ct.ThrowIfCancellationRequested();
            var observedAt = _time.GetUtcNow();
            // The read IOCTL carries FILE_WRITE_ACCESS, so the handle needs both rights.
            // No EC write request is present in this type.
            using SafeFileHandle device = CreateFile(
                DevicePath,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                dwFlagsAndAttributes: 0,
                hTemplateFile: IntPtr.Zero);

            if (device.IsInvalid)
            {
                int errorCode = Marshal.GetLastWin32Error();
                return ReadFailure(errorCode, observedAt);
            }

            var registers = new Dictionary<int, byte>(Addresses.Length);
            foreach (int address in Addresses)
            {
                ct.ThrowIfCancellationRequested();
                byte[] input = BitConverter.GetBytes((uint)address);
                byte[] output = new byte[4];
                bool succeeded = DeviceIoControl(
                    device,
                    EcReadControlCode,
                    input,
                    input.Length,
                    output,
                    output.Length,
                    out int bytesReturned,
                    IntPtr.Zero);

                if (!succeeded || bytesReturned < output.Length)
                    return ReadFailure(succeeded ? 13 : Marshal.GetLastWin32Error(), observedAt);

                registers[address] = output[0];
                Thread.Sleep(RequestDelayMilliseconds);
            }

            return EcFanDecoder.Decode(registers, observedAt);
        }
    }

    private static EcFanSnapshot ReadFailure(int errorCode, DateTimeOffset observedAt) => new()
    {
        ErrorCode = errorCode,
        LastError = $"EC 风扇读取失败（错误 {errorCode}）。",
        UpdatedAt = observedAt
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        byte[] inputBuffer,
        int inputBufferSize,
        byte[] outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);
}
