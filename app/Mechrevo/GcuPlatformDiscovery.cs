using Microsoft.Win32;
using System.Security.Cryptography;

namespace YALM.Mechrevo;

/// <summary>
/// Identifies a reviewed hardware protocol, independently of bridge authentication.
/// Only the reviewed worker permits the two read-only firmware capability queries.
/// </summary>
internal static class GcuPlatformDiscovery
{
    internal const int FanCapabilityAddress = 1934;
    internal const int ProjectIdAddress = 1856;
    internal static readonly int[] CapabilityAddresses = [FanCapabilityAddress, ProjectIdAddress];
    internal const string LegacyReadOnlyWorker = "8619B7B1AEC9F97E71224CF2FBB2551D22CAB5CD9F872C16441285B83C88AFFE";

    // These are hardware workers, not GCUBridge binaries. See the protocol audit.
    private static readonly IReadOnlyDictionary<string, string> ReviewedWorkers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["806805D99231048ECE844C491EC793600DE4E953EA6AB43EAECA89601500EC26"] = "5.17.49.6",
        ["79516561E3C7B4383831C2DC8C095851E75488927515518D5A862F403B8E6533"] = "5.27.49.9"
    };

    public static async Task<MachineInfo> DiscoverAsync(MachineInfo machine, Action<string>? log = null,
        CancellationToken ct = default)
    {
        machine = ClearProtocol(machine);
        if (!machine.IsMechrevo || machine.HasVerifiedBoard) return machine;
        try
        {
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\GCUBridge", false);
            string? bridge = MechrevoDiagnostics.ServiceExecutable(service?.GetValue("ImagePath")?.ToString());
            if (bridge is null || !Path.GetFileName(bridge).Equals("GCUBridge.exe", StringComparison.OrdinalIgnoreCase))
                return machine;
            string worker = Path.Combine(Path.GetDirectoryName(bridge)!, "MyControlCenter", "GCUService.exe");
            return await InspectAsync(machine, worker, new EcFanReader().ReadRegistersAsync, log, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            System.Security.SecurityException)
        {
            log?.Invoke("机型协议识别失败，保留只读；" + MechrevoDiagnostics.ExceptionSummary(ex));
            return machine;
        }
    }

    internal static async Task<MachineInfo> InspectAsync(MachineInfo machine, string workerPath,
        Func<IEnumerable<int>, CancellationToken, Task<EcRegisterSnapshot>> readRegisters,
        Action<string>? log = null, CancellationToken ct = default)
    {
        machine = ClearProtocol(machine);
        if (!machine.IsMechrevo || machine.HasVerifiedBoard) return machine;
        string? hash = ReadWorkerHash(workerPath);
        if (hash == LegacyReadOnlyWorker)
        {
            log?.Invoke("机型协议：旧版服务仅启用已核对的转速读取；保护读取未适配，所有控制保持禁用。");
            return machine with { ProtocolProfile = "GCU-Legacy-ReadOnly", ProtocolWorkerHash = hash };
        }
        if (hash is null || !ReviewedWorkers.TryGetValue(hash, out string? version))
        {
            log?.Invoke("机型协议：硬件服务版本未核对，跳过专用寄存器探测；桥接连接能力独立判断。");
            return machine;
        }

        if (machine.OemAppType is not (1 or 2))
        {
            log?.Invoke("机型协议：厂商应用类型未确认或模式编号不同，保留只读。");
            return machine;
        }

        EcRegisterSnapshot values = await readRegisters(CapabilityAddresses, ct).ConfigureAwait(false);
        bool supported = SupportsRamFan15(hash, values);
        log?.Invoke($"机型协议：硬件服务={version}；普通内存风扇协议={supported}；识别来源=已核对服务与固件能力");
        return supported ? machine with { ProtocolProfile = "GCU-RamFan1.5", ProtocolWorkerHash = hash } : machine;
    }

    private static MachineInfo ClearProtocol(MachineInfo machine) =>
        machine.ProtocolProfile is null && machine.ProtocolWorkerHash is null ? machine
            : machine with { ProtocolProfile = null, ProtocolWorkerHash = null };

    internal static bool SupportsRamFan15(string hash, EcRegisterSnapshot values) =>
        ReviewedWorkers.ContainsKey(hash) && values.ErrorCode is null or 0 && values.IsComplete(CapabilityAddresses) &&
        (values.Registers[FanCapabilityAddress] & 0x40) != 0 &&
        // Commercial and extended project IDs use different protocol branches.
        // No marketing-name or prefix match can turn an unknown project into support.
        values.Registers[ProjectIdAddress] is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or
            10 or 11 or 12 or 13 or 15 or 16 or 22;

    private static string? ReadWorkerHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length is < 64 or > 64 * 1024 * 1024) return null;
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            System.Security.SecurityException) { return null; }
    }
}
