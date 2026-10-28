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

    // These workers share the reviewed four-byte RPM read path. Their thermal
    // protection semantics are not established, so none can grant control.
    private static readonly IReadOnlyDictionary<string, string> ReviewedReadOnlyWorkers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [LegacyReadOnlyWorker] = "3.4.15.0",
        ["178953B081593B69E30A0728C9E5935ED63FA36FF1CE6DBDC654D7548B6E3E20"] = "3.4.39.0 / 3.0.0.14",
        ["8C490C54A15977F7421AC819A54370CC40E5C9E1DCAC94177FBFE1821DE94A2F"] = "4.17.47.13"
    };

    // These commercial workers can also fall back to ECIO.dll. Only the
    // explicit shared-memory setting and its reviewed project check qualify
    // them for our existing read driver; the fallback is never invoked here.
    private static readonly IReadOnlyDictionary<string, string> ReviewedH2RamWorkers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["5119C511B7D0B8D0ADB4B0EEAE89CB66DCC47A8AAE996FD8E4F6A38DFE6DF0FA"] = "1.1.0.32",
        ["503E2AD88824288835F5070CA9552353F865C0411087A81CB0ED1EB7079EE1BA"] = "1.1.0.49",
        ["B44CCCF4DDE1D9A9D529E4648F2466008949911235BF2CFF173B4240ABCA9B39"] = "1.1.0.63"
    };

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
        if (!NeedsProtocolInspection(machine, log)) return machine;
        try
        {
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\GCUBridge", false);
            string? worker = WorkerPathFromService(service is not null, service?.GetValue("ImagePath")?.ToString(), log);
            if (worker is null) return machine;
            return await InspectWorkerAsync(machine, worker, new EcFanReader().ReadRegistersAsync, log, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            System.Security.SecurityException)
        {
            log?.Invoke("机型协议：服务定位失败，保留只读；原因=" + FileFailureReason(ex) + "；" + MechrevoDiagnostics.ExceptionSummary(ex));
            return machine;
        }
    }

    internal static async Task<MachineInfo> InspectAsync(MachineInfo machine, string workerPath,
        Func<IEnumerable<int>, CancellationToken, Task<EcRegisterSnapshot>> readRegisters,
        Action<string>? log = null, CancellationToken ct = default)
    {
        machine = ClearProtocol(machine);
        if (!NeedsProtocolInspection(machine, log)) return machine;
        return await InspectWorkerAsync(machine, workerPath, readRegisters, log, ct).ConfigureAwait(false);
    }

    private static bool NeedsProtocolInspection(MachineInfo machine, Action<string>? log)
    {
        log?.Invoke($"机型协议：厂商匹配={machine.IsMechrevo}；已验证主板或机型匹配={machine.HasVerifiedBoard}；应用类型={machine.OemAppType?.ToString() ?? "未知"}；{machine.DescribeOemAppType()}");
        if (!machine.IsMechrevo)
            log?.Invoke("机型协议：厂商标识未匹配机械革命，跳过硬件协议探测，保留只读。");
        else if (machine.HasVerifiedBoard)
            log?.Invoke("机型协议：已匹配经过验证的 GM6PX0X 主板或机型，使用现有适配，无需额外能力探测。");
        else
            log?.Invoke("机型协议：主板或机型未匹配固定适配，继续核对硬件工作进程与固件能力。");
        return machine.IsMechrevo && !machine.HasVerifiedBoard;
    }

    internal static string? WorkerPathFromService(bool serviceFound, string? imagePath, Action<string>? log = null)
    {
        if (!serviceFound)
        {
            log?.Invoke("机型协议：未找到 GCUBridge 服务注册项，无法定位硬件工作进程，保留只读。");
            return null;
        }
        string? bridge = MechrevoDiagnostics.ServiceExecutable(imagePath);
        if (bridge is null)
        {
            log?.Invoke("机型协议：GCUBridge 服务已注册，但未解析到有效的绝对程序路径，保留只读。");
            return null;
        }
        if (!Path.GetFileName(bridge).Equals("GCUBridge.exe", StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke("机型协议：服务程序文件名不是预期的 GCUBridge.exe，无法定位硬件工作进程，保留只读。");
            return null;
        }
        log?.Invoke("机型协议：已解析 GCUBridge 服务程序，按同目录 MyControlCenter/GCUService.exe 定位硬件工作进程。");
        return Path.Combine(Path.GetDirectoryName(bridge)!, "MyControlCenter", "GCUService.exe");
    }

    private static async Task<MachineInfo> InspectWorkerAsync(MachineInfo machine, string workerPath,
        Func<IEnumerable<int>, CancellationToken, Task<EcRegisterSnapshot>> readRegisters,
        Action<string>? log, CancellationToken ct)
    {
        string? hash = ReadWorkerHash(workerPath, log);
        if (hash is null) return machine;
        if (ReviewedReadOnlyWorkers.TryGetValue(hash, out string? readOnlyVersion))
        {
            log?.Invoke($"机型协议：旧版硬件服务={readOnlyVersion}，仅启用已核对的转速读取；保护读取未适配，所有控制保持禁用。");
            return machine with { ProtocolProfile = "GCU-Legacy-ReadOnly", ProtocolWorkerHash = hash };
        }
        if (ReviewedH2RamWorkers.TryGetValue(hash, out string? h2RamVersion))
            return await InspectH2RamWorkerAsync(machine, hash, h2RamVersion, readRegisters, log, ct).ConfigureAwait(false);
        if (!ReviewedWorkers.TryGetValue(hash, out string? version))
        {
            log?.Invoke("机型协议：硬件工作进程 SHA256 未匹配已核对样本，跳过专用寄存器探测，保留只读；桥接连接能力独立判断。");
            return machine;
        }
        log?.Invoke($"机型协议：硬件工作进程 SHA256 匹配已核对版本 {version}。");

        if (machine.OemAppType is not (1 or 2))
        {
            log?.Invoke($"机型协议：厂商应用类型={machine.OemAppType?.ToString() ?? "未知"}；{machine.DescribeOemAppType()}；未匹配已核对的类型 1 或 2，跳过专用寄存器探测，保留只读。");
            return machine;
        }

        EcRegisterSnapshot values;
        try
        {
            values = await readRegisters(CapabilityAddresses, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            System.Security.SecurityException)
        {
            log?.Invoke("机型协议：固件能力读取失败，保留只读；" + MechrevoDiagnostics.ExceptionSummary(ex));
            return machine;
        }
        log?.Invoke("机型协议：" + DescribeCapabilities(values));
        string? failure = RamFan15BlockReason(hash, values);
        log?.Invoke(failure is null
            ? $"机型协议：硬件服务={version}；应用类型、风扇能力位与原始项目号均匹配，启用 GCU-RamFan1.5 已核对能力。"
            : $"机型协议：{failure}，保留只读。");
        return failure is null ? machine with { ProtocolProfile = "GCU-RamFan1.5", ProtocolWorkerHash = hash } : machine;
    }

    private static async Task<MachineInfo> InspectH2RamWorkerAsync(MachineInfo machine, string hash, string version,
        Func<IEnumerable<int>, CancellationToken, Task<EcRegisterSnapshot>> readRegisters,
        Action<string>? log, CancellationToken ct)
    {
        log?.Invoke($"机型协议：已核对旧商用硬件服务={version}；{machine.DescribeOemH2Ram()}");
        if (machine.OemH2RamSupported != true)
        {
            log?.Invoke("机型协议：共享内存读取开关未明确开启，跳过寄存器探测，不使用独立驱动回退路径。");
            return machine;
        }
        EcRegisterSnapshot values;
        try
        {
            values = await readRegisters([ProjectIdAddress], ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            System.Security.SecurityException)
        {
            log?.Invoke("机型协议：共享内存项目号读取失败，保留只读；" + MechrevoDiagnostics.ExceptionSummary(ex));
            return machine;
        }
        if (values.ErrorCode is not (null or 0) || values.LastError is not null)
        {
            log?.Invoke($"机型协议：共享内存项目号读取报错，保留只读；错误码={values.ErrorCode?.ToString() ?? "无"}；存在读取错误={values.LastError is not null}");
            return machine;
        }
        if (!values.Registers.TryGetValue(ProjectIdAddress, out byte project))
        {
            log?.Invoke($"机型协议：共享内存项目号寄存器缺失：{ProjectIdAddress}，保留只读。");
            return machine;
        }
        if (project is < 1 or > 16)
        {
            log?.Invoke($"机型协议：共享内存原始项目号={project}（0x{project:X2}），未匹配已核对的 1～16 分支，保留只读。");
            return machine;
        }
        log?.Invoke($"机型协议：共享内存原始项目号={project}（0x{project:X2}），仅启用已核对的转速读取；保护读取未适配，所有控制保持禁用。");
        return machine with { ProtocolProfile = "GCU-Legacy-ReadOnly", ProtocolWorkerHash = hash };
    }

    private static MachineInfo ClearProtocol(MachineInfo machine) =>
        machine.ProtocolProfile is null && machine.ProtocolWorkerHash is null ? machine
            : machine with { ProtocolProfile = null, ProtocolWorkerHash = null };

    internal static bool SupportsRamFan15(string hash, EcRegisterSnapshot values) =>
        RamFan15BlockReason(hash, values) is null;

    private static string? RamFan15BlockReason(string hash, EcRegisterSnapshot values)
    {
        if (!ReviewedWorkers.ContainsKey(hash)) return "硬件工作进程指纹未核对";
        if (values.ErrorCode is not (null or 0) || values.LastError is not null)
            return "固件能力读取报错";
        int[] missing = CapabilityAddresses.Where(address => !values.Registers.ContainsKey(address)).ToArray();
        if (missing.Length > 0) return "固件能力寄存器缺失：" + string.Join("、", missing);
        if ((values.Registers[FanCapabilityAddress] & 0x40) == 0)
            return "风扇能力位 0x40 未置位，不支持已核对的普通内存风扇协议";
        // Commercial and extended project IDs use different protocol branches.
        // No marketing-name or prefix match can turn an unknown project into support.
        return values.Registers[ProjectIdAddress] is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or
            10 or 11 or 12 or 13 or 15 or 16 or 22 ? null : "原始项目号未匹配已核对的普通协议分支";
    }

    private static string DescribeCapabilities(EcRegisterSnapshot values)
    {
        string fan = values.Registers.TryGetValue(FanCapabilityAddress, out byte flags) ? $"0x{flags:X2}" : "缺失";
        string project = values.Registers.TryGetValue(ProjectIdAddress, out byte id) ? $"{id}（0x{id:X2}）" : "缺失";
        return $"固件能力回报；寄存器 {FanCapabilityAddress}={fan}；原始项目号 {ProjectIdAddress}={project}；错误码={values.ErrorCode?.ToString() ?? "无"}；存在读取错误={values.LastError is not null}";
    }

    private static string? ReadWorkerHash(string path, Action<string>? log)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length is < 64 or > 64 * 1024 * 1024)
            {
                log?.Invoke($"机型协议：硬件工作进程文件大小={stream.Length} 字节，超出允许范围 64～67108864，跳过指纹读取，保留只读。");
                return null;
            }
            string hash = Convert.ToHexString(SHA256.HashData(stream));
            log?.Invoke($"机型协议：硬件工作进程文件大小={stream.Length} 字节；SHA256={hash}");
            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            System.Security.SecurityException)
        {
            log?.Invoke("机型协议：硬件工作进程文件" + FileFailureReason(ex) + "，跳过指纹读取，保留只读；" + MechrevoDiagnostics.ExceptionSummary(ex));
            return null;
        }
    }

    private static string FileFailureReason(Exception exception) => exception switch
    {
        FileNotFoundException or DirectoryNotFoundException => "不存在",
        UnauthorizedAccessException or System.Security.SecurityException => "权限不足",
        ArgumentException => "路径无效",
        _ => "读取失败"
    };
}
