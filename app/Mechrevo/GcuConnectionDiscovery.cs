using Microsoft.Win32;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace YALM.Mechrevo;

/// <summary>Reads vendor bridge metadata as data; never loads or executes the vendor assembly.</summary>
internal static class GcuConnectionDiscovery
{
    // Audited on 2026-10-28 against the supplied official installers. A matching
    // file version alone is insufficient: many different binaries use 1.0.1.10.
    // This catalog covers credential extraction only, not machine/hardware support.
    // See docs/官网控制台多版本静态检查记录.md for evidence and remaining limits.
    private static readonly Dictionary<string, string> KnownBridges = new(StringComparer.Ordinal)
    {
        ["29EF699B684FF93709205D4EA3B10630D623DBB076B81C9FE44024AE0DA9007F"] = "5.17.49.6",
        ["45B2C1D9D7D90CC9A31936655E872D4C108703A9F442E3B6E1D6037AF59375B4"] = "5.17.49.19 / 5.27.49.9",
        ["5EBF6789441C63EB657D6D5730C0C7E1635B0C07B590A4F5EEA3E86740D701BF"] = "5.17.51.48",
        ["7A1405E231A5E8581C4E9FC4FCE2302A77578742AC643AA3D42F7DADACA80695"] = "5.57.1.2",
        ["553BA4088ACFFBD087F0CE9763896DF4F4BB4E19F5EE22222C110A3C56EED319"] = "3.4.15.0",
        // These builds additionally need the reviewed in-memory method restoration.
        ["2AA9B29606B292B15B3AF090D138B8455ACA69C5F620239B43F3F86375AE556A"] = "4.17.47.13",
        ["D7D1525F3D5F34A2A7D648C9ADD51B97DB31DA663416060CF2E3D9FC15F3725C"] = "5.56.1.18 / 5.56.60.45",
        ["52B9E579986F84066E5FE5D2B2C4059110628EE4B005391CE0B2F7A6283437FD"] = "5.56.1.24",
        ["CF40A0F56D0AA48D7C4FE667A701C7FC212D323BC67376D63AFB003D5FB872DD"] = "3.4.39.0",
        ["8EE0B5E039BC3F61AD897C210B822FE68412ED655257FB07DC5095CAD9FF1CBA"] = "3.0.0.14",
        ["1E2FBA7FB6C91221EDD69B4D97B82A2650D4522184EF81C7283F27E172695132"] = "1.1.0.32",
        ["E4924979B357FEF9591F3E122D59C5B40EA2E7C2F0829E1E9C69BAC01CC4004E"] = "1.1.0.49",
        ["3FFF9FF854CFFA0FA290D2B67203917A42C394EC204402C23401745EC9924C5B"] = "1.1.0.63"
    };
    private static readonly Dictionary<ushort, OpCode> Opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => unchecked((ushort)code.Value));

    public static GcuConnectionOptions? Find(MachineInfo machine) => Find(machine, out _);

    public static GcuConnectionOptions? Find(MachineInfo machine, out string? failure, Action<string>? log = null)
    {
        failure = null;
        string? id = GcuEnvironment.Get("CLIENT_ID");
        string? user = GcuEnvironment.Get("USERNAME");
        string? password = GcuEnvironment.Get("PASSWORD");
        string? portText = GcuEnvironment.Get("PORT");
        int port = 13688;
        if (!string.IsNullOrWhiteSpace(portText) && (!int.TryParse(portText, out port) || port is < 1 or > 65535))
        {
            failure = "连接端口无效，请检查连接环境变量。";
            log?.Invoke(failure);
            return null;
        }
        // An explicit but incomplete override must not silently fall back.
        if (id is not null || user is not null || password is not null)
        {
            bool complete = !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(user) && !string.IsNullOrEmpty(password);
            log?.Invoke($"连接资料来源：环境变量；客户端标识已填写={!string.IsNullOrWhiteSpace(id)}；用户名已填写={!string.IsNullOrWhiteSpace(user)}；密码已填写={!string.IsNullOrEmpty(password)}；端口={port}");
            if (complete)
            {
                MechrevoDiagnostics.WriteService("GCUBridge", log);
                return ApplyMachinePolicy(new(id!.Trim(), user!.Trim(), password!, port), machine);
            }
            failure = "连接环境变量不完整，未尝试自动发现。";
            log?.Invoke(failure);
            return null;
        }

        try
        {
            log?.Invoke($"连接资料来源：厂商桥接组件自动发现；端口={port}");
            MechrevoDiagnostics.WriteService("GCUBridge", log);
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\GCUBridge", false);
            if (service is null)
            {
                failure = "未安装厂商桥接服务，请检查官方控制中心。";
                log?.Invoke(failure);
                return null;
            }
            string? path = MechrevoDiagnostics.ServiceExecutable(service.GetValue("ImagePath")?.ToString());
            if (path is null || !string.Equals(Path.GetFileName(path), "GCUBridge.exe", StringComparison.OrdinalIgnoreCase))
            {
                failure = "厂商桥接服务路径无效，无法自动连接。";
                log?.Invoke(failure);
                return null;
            }
            return ReadBridge(machine, path, port, out failure, log);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException or IndexOutOfRangeException or OverflowException or InvalidOperationException or System.Security.SecurityException)
        {
            failure = "厂商桥接服务信息读取失败，请查看日志。";
            log?.Invoke(failure + " " + MechrevoDiagnostics.ExceptionSummary(exception));
            return null;
        }
    }

    internal static GcuConnectionOptions? ReadBridge(MachineInfo machine, string path, int port, out string? failure, Action<string>? log)
    {
        failure = null;
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length is < 64 or > 16 * 1024 * 1024)
            {
                failure = "桥接组件文件大小异常，已跳过自动连接。";
                log?.Invoke(failure);
                return null;
            }
            byte[] image = new byte[(int)file.Length];
            file.ReadExactly(image);
            string hash = Convert.ToHexString(SHA256.HashData(image));
            bool known = KnownBridges.TryGetValue(hash, out string? packages);
            log?.Invoke($"桥接组件校验：SHA256={hash}；已验证版本={known}；匹配官方包={packages ?? "未收录"}；校验范围=连接资料静态解析");
            if (!known)
                log?.Invoke("桥接组件版本未收录；尝试通用静态解析连接资料，不加载或执行厂商程序集。");
            if (GcuBridgeImage.IsProtected(hash))
            {
                byte[]? recovered = GcuBridgeImage.ReadDecrypted(hash, image);
                if (recovered is null)
                {
                    failure = "桥接组件静态还原校验失败，已跳过自动连接。";
                    log?.Invoke(failure);
                    return null;
                }
                image = recovered;
                log?.Invoke("桥接组件方法体已在内存中静态还原；未加载或执行厂商程序集。");
            }
            using var stream = new MemoryStream(image, writable: false);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            string[] parts = Strings(pe, metadata, "MQTTService", "ClientManager", ".ctor").ToArray();
            string? prefix = Strings(pe, metadata, "MQTT_SERVER", "MQTTServer", "InitialzeClient")
                .FirstOrDefault(value => value == "UWPClient");
            if (prefix is null || parts.Length != 4 || parts[1] != parts[2])
            {
                failure = known
                    ? "桥接组件结构校验失败，已跳过自动连接。"
                    : "桥接组件结构校验失败，未知版本未能通过通用静态解析，已跳过自动连接。";
                log?.Invoke(failure);
                return null;
            }
            // Use a high slot; the vendor does not provide exclusive reservations.
            // The process mutex prevents collisions between this tool's local instances.
            const string slot = "19";
            log?.Invoke(known
                ? "自动发现成功；使用预生成槽位 19，不记录连接凭据。"
                : "未知桥接版本通过通用静态解析；使用预生成槽位 19，不记录连接凭据。实际连接和状态回报仍需现场确认。");
            return ApplyMachinePolicy(new(prefix + parts[0] + slot, prefix + parts[1] + slot, prefix + parts[3] + slot, port), machine, known);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException or IndexOutOfRangeException or OverflowException or InvalidOperationException or System.Security.SecurityException)
        {
            failure = exception is FileNotFoundException or DirectoryNotFoundException ? "厂商桥接组件文件不存在，无法自动连接。"
                : exception is UnauthorizedAccessException ? "无法读取厂商桥接组件，请检查文件访问权限。"
                : "厂商桥接组件读取失败，请查看日志。";
            log?.Invoke(failure + " " + MechrevoDiagnostics.ExceptionSummary(exception));
            return null;
        }
    }

    private static GcuConnectionOptions ApplyMachinePolicy(GcuConnectionOptions options, MachineInfo machine,
        bool bridgeVersionKnown = true) => options with
    {
        BridgeVersionKnown = bridgeVersionKnown,
        TurboSupported = machine.TurboSupported,
        ModeControlSupported = machine.SupportsModeControl,
        CustomControlSupported = machine.SupportsCustomControl,
        PeripheralControlSupported = machine.SupportsPeripheralControl,
        // An unverified machine may expose a reviewed control protocol, but writes
        // remain opt-in until the user confirms the warning for this connection.
        WritePermissionGranted = machine.HasVerifiedBoard
    };

    private static IEnumerable<string> Strings(PEReader pe, MetadataReader reader, string ns, string typeName, string methodName)
    {
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (reader.GetString(type.Namespace) != ns || reader.GetString(type.Name) != typeName) continue;
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (reader.GetString(method.Name) != methodName || method.RelativeVirtualAddress == 0) continue;
                byte[] il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
                for (int index = 0; index < il.Length;)
                {
                    ushort key = il[index++];
                    if (key == 0xfe) key = (ushort)(0xfe00 | il[index++]);
                    if (!Opcodes.TryGetValue(key, out var code)) throw new BadImageFormatException();
                    if (code == OpCodes.Ldstr)
                        yield return reader.GetUserString(MetadataTokens.UserStringHandle(BitConverter.ToInt32(il, index) & 0xffffff));
                    index += code.OperandType switch
                    {
                        OperandType.InlineNone => 0,
                        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                        OperandType.InlineVar => 2,
                        OperandType.InlineI8 or OperandType.InlineR => 8,
                        OperandType.InlineSwitch => checked(4 + 4 * BitConverter.ToInt32(il, index)),
                        _ => 4
                    };
                    if (index > il.Length) throw new BadImageFormatException();
                }
                yield break;
            }
        }
    }
}
