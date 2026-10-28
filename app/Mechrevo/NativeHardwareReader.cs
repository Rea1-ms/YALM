using Microsoft.Win32;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using EnumerationOptions = System.Management.EnumerationOptions;

namespace YALM.Mechrevo;

/// <summary>Independent vendor telemetry. It has no control or generic firmware command entry point.</summary>
internal sealed class NativeHardwareReader
{
    internal const string YilongImageHash = "EF6D6C454563CE39517AF000A48EB32080CE8F00272981A27A706DCF1F3C5760";
    internal const string YilongProductCode = "{26D7B486-E029-4006-B76B-871E5229E4D5}";
    internal const string YilongComponentCode = "{D069E4D3-8DCC-2AF7-AF35-123D2A13342C}";
    internal const string IminiImageHash = "C15B7057C88B2DBBF304F5DCA79DA276AD909451C709C7CA9493A525049F040D";
    internal const string IminiProductCode = "{8DC96B3E-FCFA-4636-BFC2-969EE7E45D70}_is1";
    internal const string IminiInstancePath = @"PowerSwitchInterface.InstanceName='ACPI\PNP0C14\IP3POWERSWITCH_0'";
    private readonly NativeHardwareEndpoint _endpoint;
    private readonly INativeHardwareReadBackend _backend;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly TimeProvider _time;

    private NativeHardwareReader(NativeHardwareEndpoint endpoint, INativeHardwareReadBackend backend,
        Action<string>? log, TimeProvider? timeProvider = null)
    {
        _endpoint = endpoint;
        _backend = backend;
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string DisplayName => _endpoint.Family switch
    {
        NativeHardwareFamily.Jiaolong => "蛟龙只读接口",
        NativeHardwareFamily.Yilong => "翼龙只读接口",
        _ => "迷你主机只读接口"
    };

    public bool UsesNumberedFans => _endpoint.Family == NativeHardwareFamily.Imini;

    public bool IsComplete(GcuSnapshot snapshot, DateTimeOffset? now = null)
    {
        if (!snapshot.IsConnected || !snapshot.IsModeFresh(now)) return false;
        return _endpoint.Family switch
        {
            NativeHardwareFamily.Jiaolong => snapshot.IsCpuFanFresh(now) && snapshot.IsGpuFanFresh(now) &&
                (_endpoint.ImageHash != JiaolongReadProtocol.ProImageHash || snapshot.IsCpuTemperatureFresh(now)),
            NativeHardwareFamily.Yilong => !_endpoint.GpuTemperatureSupported || snapshot.IsGpuTemperatureFresh(now),
            NativeHardwareFamily.Imini => snapshot.NumberedFanCount is 1 or 2 && snapshot.IsFan1Fresh(now) &&
                (snapshot.NumberedFanCount == 1 || snapshot.IsFan2Fresh(now)) &&
                (!_endpoint.CpuTemperatureSupported || snapshot.IsCpuTemperatureFresh(now)),
            _ => false
        };
    }

    public static Task<NativeHardwareReader?> DiscoverAsync(MachineInfo machine, Action<string>? log = null,
        CancellationToken ct = default) => InspectAsync(machine.IsMechrevo, new SystemReadBackend(log), log, ct);

    internal static async Task<NativeHardwareReader?> InspectAsync(bool manufacturerMatches,
        INativeHardwareReadBackend backend, Action<string>? log = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!manufacturerMatches)
        {
            log?.Invoke("独立协议：厂商未匹配机械革命，跳过管理接口检查。");
            return null;
        }
        IReadOnlyList<NativeHardwareEndpoint> endpoints;
        try { endpoints = await Task.Run(() => backend.Inspect(ct), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log?.Invoke("独立协议：元数据检查失败，未调用固件方法；" + MechrevoDiagnostics.ExceptionSummary(ex));
            return null;
        }
        ct.ThrowIfCancellationRequested();
        var candidates = new List<NativeHardwareEndpoint>();
        foreach (NativeHardwareEndpoint endpoint in endpoints)
        {
            if (!ReviewedImage(endpoint))
            {
                log?.Invoke("独立协议：控制台文件指纹未匹配已核对样本，跳过固件读取。");
                continue;
            }
            if (endpoint.SchemaStatus != NativeHardwareSchemaStatus.Matched)
            {
                log?.Invoke("独立协议：" + SchemaReason(endpoint.SchemaStatus) + "，跳过固件读取。");
                continue;
            }
            if (!ValidInstance(endpoint))
            {
                log?.Invoke("独立协议：实例身份不匹配，跳过固件读取。");
                continue;
            }
            candidates.Add(endpoint);
        }
        if (candidates.Count != 1)
        {
            log?.Invoke(candidates.Count == 0 ? "独立协议：未发现满足条件的已核对只读接口。"
                : "独立协议：发现多个匹配接口，无法确定当前协议，未调用固件方法。");
            return null;
        }
        var reader = new NativeHardwareReader(candidates[0], backend, log);
        log?.Invoke("独立协议：已识别" + reader.DisplayName + "；仅读取已核对字段，控制功能保持禁用。");
        return reader;
    }

    private static bool ReviewedImage(NativeHardwareEndpoint endpoint) => endpoint.Family switch
    {
        NativeHardwareFamily.Jiaolong => endpoint.ImageHash is JiaolongReadProtocol.LegacyImageHash or JiaolongReadProtocol.ProImageHash,
        NativeHardwareFamily.Yilong => endpoint.ImageHash == YilongImageHash,
        NativeHardwareFamily.Imini => endpoint.ImageHash == IminiImageHash,
        _ => false
    };

    private static bool ValidInstance(NativeHardwareEndpoint endpoint)
    {
        try
        {
            var path = new ManagementPath(endpoint.InstancePath);
            if (!path.IsInstance || (!string.IsNullOrEmpty(path.Server) && path.Server != ".") ||
                !string.IsNullOrEmpty(path.NamespacePath)) return false;
            if (endpoint.Family == NativeHardwareFamily.Yilong)
                return path.ClassName.Equals("ACPIMethod", StringComparison.OrdinalIgnoreCase);
            if (endpoint.Family == NativeHardwareFamily.Imini) return endpoint.InstancePath == IminiInstancePath;
            // The fixed path is generated only after checking InstanceName, never from the installed package.
            return endpoint.Family == NativeHardwareFamily.Jiaolong &&
                endpoint.InstancePath == JiaolongReadProtocol.InstancePath;
        }
        catch { return false; }
    }

    private static string SchemaReason(NativeHardwareSchemaStatus status) => status switch
    {
        NativeHardwareSchemaStatus.MissingClass => "管理类缺失",
        NativeHardwareSchemaStatus.MissingMethod => "读取方法缺失",
        NativeHardwareSchemaStatus.InvalidParameters => "读取参数或返回字段类型不匹配",
        NativeHardwareSchemaStatus.MissingInstance => "固件实例缺失",
        NativeHardwareSchemaStatus.AmbiguousInstances => "固件实例不唯一",
        _ => "管理接口结构未匹配"
    };

    public async Task<GcuSnapshot> ReadAsync(CancellationToken ct = default)
    {
        await _readGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ReadCore(ct), ct).ConfigureAwait(false);
        }
        finally { _readGate.Release(); }
    }

    private GcuSnapshot ReadCore(CancellationToken ct)
    {
        // A new frame is built for every pass; failed fields never inherit previous observations.
        DateTimeOffset observedAt = _time.GetUtcNow();
        bool failed = false;
        object? Read(NativeHardwareReading reading)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                object? response = _backend.Read(_endpoint, reading, ct);
                ct.ThrowIfCancellationRequested();
                bool validShape = _endpoint.Family == NativeHardwareFamily.Jiaolong
                    ? response is byte[] bytes && bytes.Length >= (reading == NativeHardwareReading.FanSpeeds ? 8 : 5) && bytes.Length <= 32
                    : response is uint;
                if (!validShape)
                {
                    failed = true;
                    _log?.Invoke($"独立协议：读取字段 {(int)reading} 的响应类型或长度不匹配，保持未知。");
                    return null;
                }
                return response;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed = true;
                _log?.Invoke($"独立协议：读取字段 {(int)reading} 失败；" + MechrevoDiagnostics.ExceptionSummary(ex));
                return null;
            }
        }

        GcuSnapshot snapshot;
        if (_endpoint.Family == NativeHardwareFamily.Jiaolong)
        {
            bool temperature = _endpoint.ImageHash == JiaolongReadProtocol.ProImageHash;
            var responses = new Dictionary<JiaolongReadCommand, byte[]?>
            {
                [JiaolongReadCommand.Mode] = Read(NativeHardwareReading.Mode) as byte[],
                [JiaolongReadCommand.FanSpeeds] = Read(NativeHardwareReading.FanSpeeds) as byte[]
            };
            if (temperature) responses[JiaolongReadCommand.CpuTemperature] = Read(NativeHardwareReading.CpuTemperature) as byte[];
            snapshot = JiaolongReadProtocol.Decode(responses, observedAt, temperature);
        }
        else if (_endpoint.Family == NativeHardwareFamily.Yilong)
        {
            snapshot = YilongReadProtocol.Parse(Read(NativeHardwareReading.Mode) as uint?,
                _endpoint.GpuTemperatureSupported ? Read(NativeHardwareReading.GpuTemperature) as uint? : null, observedAt);
        }
        else
        {
            uint? mode = Read(NativeHardwareReading.Mode) as uint?;
            uint? count = Read(NativeHardwareReading.FanCount) as uint?;
            uint? gpu = Read(NativeHardwareReading.GpuSupport) as uint?;
            uint? fans = Read(NativeHardwareReading.FanSpeeds) as uint?;
            uint? temperature = _endpoint.CpuTemperatureSupported ? Read(NativeHardwareReading.CpuTemperature) as uint? : null;
            snapshot = IminiReadProtocol.Decode(mode, fans, count, gpu, observedAt, temperature);
        }
        return failed ? snapshot with { LastError = "部分独立接口状态读取失败，缺失字段保持未知。" } : snapshot;
    }

    private sealed class SystemReadBackend(Action<string>? log) : INativeHardwareReadBackend
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

        public IReadOnlyList<NativeHardwareEndpoint> Inspect(CancellationToken ct)
        {
            var endpoints = new List<NativeHardwareEndpoint>();
            InspectFamily(NativeHardwareFamily.Jiaolong, ReadJiaolongImage);
            InspectFamily(NativeHardwareFamily.Yilong, ReadYilongImage);
            InspectFamily(NativeHardwareFamily.Imini, () => ReadInstalledImage(IminiProductCode));
            return endpoints;

            void InspectFamily(NativeHardwareFamily family, Func<string?> image)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string? path = image();
                    if (path is null)
                    {
                        log?.Invoke($"独立协议：接口 {(int)family} 未找到对应控制台安装身份，跳过管理接口探测。");
                        return;
                    }
                    string? hash = ReadImageHash(path);
                    var candidate = new NativeHardwareEndpoint(family, "", hash, NativeHardwareSchemaStatus.MissingClass);
                    if (!ReviewedImage(candidate))
                    {
                        if (hash is not null) endpoints.Add(candidate);
                        return;
                    }
                    NativeHardwareEndpoint endpoint = InspectSchema(family, hash!, ct);
                    if (family == NativeHardwareFamily.Yilong && endpoint.SchemaStatus == NativeHardwareSchemaStatus.Matched)
                    {
                        try { endpoint = endpoint with { GpuTemperatureSupported = HasNvidiaGpu(ct) }; }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { log?.Invoke("独立协议：未能确认显卡厂商，仅保留翼龙模式读取；" + MechrevoDiagnostics.ExceptionSummary(ex)); }
                    }
                    endpoints.Add(endpoint);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log?.Invoke($"独立协议：接口 {(int)family} 的文件或元数据检查失败，未调用固件方法；" + MechrevoDiagnostics.ExceptionSummary(ex));
                }
            }
        }

        private static ManagementScope Scope() => new(@"\\.\root\WMI", new ConnectionOptions { Timeout = Timeout });

        private static bool HasNvidiaGpu(CancellationToken ct)
        {
            using var query = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\CIMV2", new ConnectionOptions { Timeout = Timeout }),
                new ObjectQuery("SELECT PNPDeviceID FROM Win32_VideoController"),
                new EnumerationOptions { Timeout = Timeout, ReturnImmediately = false });
            using ManagementObjectCollection devices = query.Get();
            foreach (ManagementObject device in devices)
                using (device)
                {
                    ct.ThrowIfCancellationRequested();
                    if (device["PNPDeviceID"] is string id && id.StartsWith(@"PCI\VEN_10DE&", StringComparison.OrdinalIgnoreCase)) return true;
                }
            return false;
        }

        private static NativeHardwareEndpoint InspectSchema(NativeHardwareFamily family, string hash, CancellationToken ct)
        {
            string className = family switch
            {
                NativeHardwareFamily.Jiaolong => JiaolongReadProtocol.ClassName,
                NativeHardwareFamily.Yilong => "ACPIMethod",
                _ => IminiReadProtocol.ClassName
            };
            using var definition = new ManagementClass(Scope(), new ManagementPath(className), new ObjectGetOptions { Timeout = Timeout });
            try { definition.Get(); }
            catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.InvalidClass or ManagementStatus.NotFound)
            { return new(family, "", hash, NativeHardwareSchemaStatus.MissingClass); }
            string[] methods = family switch
            {
                NativeHardwareFamily.Jiaolong => [JiaolongReadProtocol.MethodName],
                NativeHardwareFamily.Yilong => ["MemIO", "DoMethod"],
                _ => [IminiReadProtocol.PowerModeMethod, IminiReadProtocol.FeatureMethod, IminiReadProtocol.FanMethod]
            };
            foreach (string methodName in methods)
            {
                MethodData? method = definition.Methods.Cast<MethodData>().SingleOrDefault(item => item.Name == methodName);
                if (method is null) return new(family, "", hash, NativeHardwareSchemaStatus.MissingMethod);
                if (family == NativeHardwareFamily.Imini)
                {
                    (string[] names, string output) = methodName switch
                    {
                        IminiReadProtocol.PowerModeMethod => (Array.Empty<string>(), IminiReadProtocol.PowerModeProperty),
                        IminiReadProtocol.FeatureMethod => (new string[] { "Reserved1", "Reserved2", "Reserved3", "Reserved4" }, IminiReadProtocol.FeatureProperty),
                        _ => (new string[] { "FanNumber" }, IminiReadProtocol.FanProperty)
                    };
                    if (!MatchesInputs(method.InParameters, names, CimType.UInt32, false) ||
                        !Matches(method.OutParameters, output, CimType.UInt32, false))
                        return new(family, "", hash, NativeHardwareSchemaStatus.InvalidParameters);
                    continue;
                }
                bool jiaolong = family == NativeHardwareFamily.Jiaolong;
                if (!MatchesInputs(method.InParameters, [jiaolong ? "InData" : "IData"], CimType.UInt8, true) ||
                    !Matches(method.OutParameters, jiaolong ? "OutData" : "OData", jiaolong ? CimType.UInt8 : CimType.UInt32, jiaolong))
                    return new(family, "", hash, NativeHardwareSchemaStatus.InvalidParameters);
            }
            using ManagementObjectCollection instances = definition.GetInstances(new EnumerationOptions { Timeout = Timeout, ReturnImmediately = false });
            var paths = new List<string>();
            foreach (ManagementObject instance in instances)
            {
                using (instance)
                {
                    ct.ThrowIfCancellationRequested();
                    if (family is NativeHardwareFamily.Jiaolong or NativeHardwareFamily.Imini)
                    {
                        string expected = family == NativeHardwareFamily.Jiaolong ? JiaolongReadProtocol.InstanceName : IminiReadProtocol.InstanceName;
                        if (!Equals(instance["InstanceName"], expected)) continue;
                        paths.Add(family == NativeHardwareFamily.Jiaolong ? JiaolongReadProtocol.InstancePath : IminiInstancePath);
                    }
                    else paths.Add(instance.Path.RelativePath);
                    if (paths.Count > 1) break;
                }
            }
            return paths.Count switch
            {
                1 => new(family, paths[0], hash, NativeHardwareSchemaStatus.Matched)
                {
                    CpuTemperatureSupported = family == NativeHardwareFamily.Imini &&
                        definition.Methods.Cast<MethodData>().Any(method => method.Name == IminiReadProtocol.TemperatureMethod &&
                            MatchesInputs(method.InParameters, ["HwTempType"], CimType.UInt32, false) &&
                            Matches(method.OutParameters, IminiReadProtocol.TemperatureProperty, CimType.UInt32, false))
                },
                0 => new(family, "", hash, NativeHardwareSchemaStatus.MissingInstance),
                _ => new(family, "", hash, NativeHardwareSchemaStatus.AmbiguousInstances)
            };
        }

        private static bool Matches(ManagementBaseObject? parameters, string name, CimType type, bool array) =>
            parameters?.Properties.Cast<PropertyData>().Any(property => property.Name == name && property.Type == type && property.IsArray == array) == true;

        private static bool MatchesInputs(ManagementBaseObject? parameters, string[] names, CimType type, bool array) =>
            (parameters?.Properties.Count ?? 0) == names.Length && names.All(name => Matches(parameters, name, type, array));

        public object? Read(NativeHardwareEndpoint endpoint, NativeHardwareReading reading, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!ReviewedImage(endpoint) || endpoint.SchemaStatus != NativeHardwareSchemaStatus.Matched || !ValidInstance(endpoint))
                throw new InvalidOperationException("未匹配已核对的只读接口。");
            bool jiaolong = endpoint.Family == NativeHardwareFamily.Jiaolong;
            if (endpoint.Family == NativeHardwareFamily.Imini) return ReadImini(endpoint, reading, ct);
            string method;
            byte[] input;
            if (jiaolong)
            {
                JiaolongReadCommand command = reading switch
                {
                    NativeHardwareReading.Mode => JiaolongReadCommand.Mode,
                    NativeHardwareReading.FanSpeeds => JiaolongReadCommand.FanSpeeds,
                    NativeHardwareReading.CpuTemperature when endpoint.ImageHash == JiaolongReadProtocol.ProImageHash => JiaolongReadCommand.CpuTemperature,
                    _ => throw new ArgumentOutOfRangeException(nameof(reading))
                };
                method = JiaolongReadProtocol.MethodName;
                input = JiaolongReadProtocol.CreateRequest(command, endpoint.ImageHash == JiaolongReadProtocol.ProImageHash);
            }
            else
            {
                (method, input) = reading switch
                {
                    NativeHardwareReading.Mode => ("MemIO", YilongReadProtocol.ModeRequest()),
                    NativeHardwareReading.GpuTemperature when endpoint.GpuTemperatureSupported => ("DoMethod", YilongReadProtocol.GpuTemperatureRequest()),
                    _ => throw new ArgumentOutOfRangeException(nameof(reading))
                };
            }
            using var instance = new ManagementObject(Scope(), new ManagementPath(endpoint.InstancePath), new ObjectGetOptions { Timeout = Timeout });
            using ManagementBaseObject parameters = instance.GetMethodParameters(method);
            parameters[jiaolong ? "InData" : "IData"] = input;
            using ManagementBaseObject result = instance.InvokeMethod(method, parameters, new InvokeMethodOptions { Timeout = Timeout });
            ct.ThrowIfCancellationRequested();
            return result[jiaolong ? "OutData" : "OData"];
        }

        private static object? ReadImini(NativeHardwareEndpoint endpoint, NativeHardwareReading reading, CancellationToken ct)
        {
            (string method, string output, IReadOnlyDictionary<string, object> values) = reading switch
            {
                NativeHardwareReading.Mode => (IminiReadProtocol.PowerModeMethod, IminiReadProtocol.PowerModeProperty, IminiReadProtocol.PowerModeParameters),
                NativeHardwareReading.FanCount => (IminiReadProtocol.FeatureMethod, IminiReadProtocol.FeatureProperty, IminiReadProtocol.FanCountParameters),
                NativeHardwareReading.GpuSupport => (IminiReadProtocol.FeatureMethod, IminiReadProtocol.FeatureProperty, IminiReadProtocol.GpuSupportParameters),
                NativeHardwareReading.FanSpeeds => (IminiReadProtocol.FanMethod, IminiReadProtocol.FanProperty, IminiReadProtocol.FanParameters),
                NativeHardwareReading.CpuTemperature when endpoint.CpuTemperatureSupported =>
                    (IminiReadProtocol.TemperatureMethod, IminiReadProtocol.TemperatureProperty, IminiReadProtocol.CpuTemperatureParameters),
                _ => throw new ArgumentOutOfRangeException(nameof(reading))
            };
            using var instance = new ManagementObject(Scope(), new ManagementPath(endpoint.InstancePath), new ObjectGetOptions { Timeout = Timeout });
            using ManagementBaseObject parameters = instance.GetMethodParameters(method);
            foreach (var pair in values) parameters[pair.Key] = pair.Value;
            using ManagementBaseObject result = instance.InvokeMethod(method, parameters, new InvokeMethodOptions { Timeout = Timeout });
            ct.ThrowIfCancellationRequested();
            return result[output];
        }

        private static string? ReadJiaolongImage()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + JiaolongReadProtocol.ServiceName, false);
            string? executable = MechrevoDiagnostics.ServiceExecutable(key?.GetValue("ImagePath") as string);
            if (executable is null || !Path.GetFileName(executable).Equals("BLDHotKeyService.exe", StringComparison.OrdinalIgnoreCase)) return null;
            return Path.Combine(Path.GetDirectoryName(executable)!, JiaolongReadProtocol.ImageName);
        }

        private static string? ReadYilongImage()
        {
            // This reads Windows Installer registration only. It never installs, repairs or provides a component.
            uint length = 1024;
            var path = new StringBuilder((int)length);
            int state = MsiGetComponentPath(YilongProductCode, YilongComponentCode, path, ref length);
            if (state == -3 && length is > 0 and < 32768) // INSTALLSTATE_MOREDATA
            {
                path = new StringBuilder((int)++length);
                state = MsiGetComponentPath(YilongProductCode, YilongComponentCode, path, ref length);
            }
            return ReviewedComponentPath(state, path.ToString()) ?? ReadInstalledImage(YilongProductCode);
        }

        [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetComponentPathW")]
        private static extern int MsiGetComponentPath(string productCode, string componentCode,
            StringBuilder path, ref uint length);

        private static string? ReadInstalledImage(string productCode)
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + productCode, false);
                if (key is null) continue;
                if (key.GetValue("InstallLocation") is string location && Path.IsPathFullyQualified(location))
                {
                    string file = Path.Combine(location, "ControlCenter.exe");
                    if (File.Exists(file)) return file;
                }
                string? icon = MechrevoDiagnostics.ServiceExecutable(key.GetValue("DisplayIcon") as string);
                if (icon is not null && Path.GetFileName(icon).Equals("ControlCenter.exe", StringComparison.OrdinalIgnoreCase)) return icon;
            }
            return null;
        }

        private static string? ReadImageHash(string? path)
        {
            if (path is null) return null;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 64 or > 536870912) throw new InvalidDataException("控制台文件尺寸超出范围。");
            return Convert.ToHexString(SHA256.HashData(file));
        }
    }

    internal static string? ReviewedComponentPath(int installState, string? path)
    {
        // Only locally installed component key paths qualify; source media and advertised states do not.
        if (installState != 3 || string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
                !Path.GetFileName(path).Equals("ControlCenter.exe", StringComparison.OrdinalIgnoreCase)) return null;
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
