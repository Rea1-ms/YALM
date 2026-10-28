using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using YALM.Mechrevo;

namespace YALM.UiTests;

internal static partial class Program
{
    private const string ReviewedPlatformWorker = "806805D99231048ECE844C491EC793600DE4E953EA6AB43EAECA89601500EC26";
    private const string ReviewedAuroraWorker = "79516561E3C7B4383831C2DC8C095851E75488927515518D5A862F403B8E6533";
    private const int PlatformFanCapabilityAddress = 1934;
    private const int PlatformProjectIdAddress = 1856;

    private static void CheckPlatforms(string[] args)
    {
        Assembly assembly = typeof(GcuController).Assembly;
        Type discovery = assembly.GetType("YALM.Mechrevo.GcuPlatformDiscovery")!;
        Type machineType = assembly.GetType("YALM.Mechrevo.MachineInfo")!;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo supports = discovery.GetMethod("SupportsRamFan15", statics)!;
        MethodInfo inspect = discovery.GetMethod("InspectAsync", statics)!;
        int[] ordinaryProjects = [1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 15, 16, 22];

        // Test every raw byte, including commercial IDs whose masked low bits
        // resemble an ordinary project. Marketing names cannot admit a branch.
        foreach (string worker in new[] { ReviewedPlatformWorker, ReviewedAuroraWorker })
        {
            for (int project = byte.MinValue; project <= byte.MaxValue; project++)
                Require(PlatformSupports(supports, worker, PlatformRegisters((byte)project)) == ordinaryProjects.Contains(project),
                    $"worker {worker}: raw firmware project {project} matches only a reviewed ordinary branch");
            for (int flags = byte.MinValue; flags <= byte.MaxValue; flags++)
                Require(PlatformSupports(supports, worker, PlatformRegisters(1, (byte)flags)) == ((flags & 0x40) != 0),
                    $"worker {worker}: fan capability byte {flags} requires the reviewed RAM-fan capability bit");

            foreach (EcRegisterSnapshot invalid in new[]
            {
                new EcRegisterSnapshot(),
                new EcRegisterSnapshot { Registers = new Dictionary<int, byte> { [PlatformFanCapabilityAddress] = 0x40 } },
                new EcRegisterSnapshot { Registers = new Dictionary<int, byte> { [PlatformProjectIdAddress] = 1 } },
                PlatformRegisters(1) with { LastError = "offline read failed" },
                PlatformRegisters(1) with { ErrorCode = 5 }
            })
                Require(!PlatformSupports(supports, worker, invalid),
                    "missing or failed capability observations cannot establish a hardware protocol");
        }
        foreach (string hash in new[] { "", new string('0', 64), ReviewedPlatformWorker[..63] + "0" })
            Require(!PlatformSupports(supports, hash, PlatformRegisters(1)),
                "unknown worker fingerprints cannot authorize the register layout");

        CheckMachineProfiles(machineType);
        CheckLegacyTelemetry();
        CheckPlatformDiagnostics(machineType, inspect, discovery);

        object unknown = PlatformMachine(machineType, "MECHREVO", "Offline test", "Unknown board");
        foreach (string path in new[] { assembly.Location, Path.Combine(AppContext.BaseDirectory, "missing-platform", "GCUService.exe") })
        {
            int reads = 0;
            object inspected = InspectPlatform(inspect, unknown, path, (_, _) =>
            {
                reads++;
                return Task.FromResult(PlatformRegisters(1));
            });
            Require(ReferenceEquals(inspected, unknown) && reads == 0 && !PlatformFlag(inspected, "SupportsEcTelemetry"),
                "an unknown or missing worker is rejected before any register query");
            foreach (string previousProfile in new[] { "GCU-RamFan1.5", "GCU-Legacy-ReadOnly" })
            {
                object previous = PlatformMachine(machineType, "MECHREVO", "Offline test", "Unknown board");
                machineType.GetProperty("ProtocolProfile")!.SetValue(previous, previousProfile);
                machineType.GetProperty("ProtocolWorkerHash")!.SetValue(previous, ReviewedPlatformWorker);
                inspected = InspectPlatform(inspect, previous, path, (_, _) =>
                {
                    reads++;
                    return Task.FromResult(PlatformRegisters(1));
                });
                Require(PlatformWasRevoked(inspected) && reads == 0,
                    "a missing or unknown worker revokes previous full and legacy protocol qualifications before reading");
            }
        }

        CheckUnknownPlatformControls(machineType, null);
        CheckUnknownPlatformControls(machineType, "GCU-Legacy-ReadOnly");
        CheckFullPlatformTelemetryGuard(machineType);
        CheckCustomPlatformGuard();
        CheckPlatformManifest(args, machineType, inspect);
    }

    private static void CheckMachineProfiles(Type machineType)
    {
        foreach ((string manufacturer, string model, string board, bool verified) in new[]
        {
            ("MECHREVO", "Offline test", "GM6PX0X", true),
            (" mechrevo ", " gm6px0x ", "Unknown board", true),
            ("MECHREVO", "Offline test", " gm6px0x ", true),
            ("OTHER", "GM6PX0X", "GM6PX0X", false),
            ("", "GM6PX0X", "GM6PX0X", false),
            ("MECHREVO-compatible", "GM6PX0X", "GM6PX0X", false),
            ("MECHREVO", "prefix GM6PX0X suffix", "Unknown board", false),
            ("MECHREVO", "Offline test", "GM6PX0X-revision", false),
            ("MECHREVO", "Offline test", "Unknown board", false)
        })
        {
            object machine = PlatformMachine(machineType, manufacturer, model, board);
            Require(PlatformFlag(machine, "HasVerifiedBoard") == verified &&
                PlatformFlag(machine, "SupportsEcTelemetry") == verified &&
                PlatformFlag(machine, "SupportsEcProtection") == verified &&
                PlatformFlag(machine, "SupportsModeControl") == verified &&
                PlatformFlag(machine, "SupportsCustomControl") == verified &&
                PlatformFlag(machine, "SupportsPeripheralControl") == verified &&
                PlatformFlag(machine, "SupportsExtendedEcDiagnostics") == verified,
                "board recognition requires an exact board or model plus the expected manufacturer");
        }

        object protocolMachine = PlatformMachine(machineType, "MECHREVO", "Offline test", "Unknown board");
        machineType.GetProperty("ProtocolProfile")!.SetValue(protocolMachine, "GCU-RamFan1.5");
        machineType.GetProperty("ProtocolWorkerHash")!.SetValue(protocolMachine, ReviewedPlatformWorker);
        Require(PlatformFlag(protocolMachine, "SupportsEcTelemetry") && PlatformFlag(protocolMachine, "SupportsModeControl") &&
            PlatformFlag(protocolMachine, "SupportsEcProtection") &&
            !PlatformFlag(protocolMachine, "SupportsCustomControl") && !PlatformFlag(protocolMachine, "SupportsPeripheralControl") &&
            !PlatformFlag(protocolMachine, "SupportsExtendedEcDiagnostics") && !PlatformFlag(protocolMachine, "HasVerifiedBoard"),
            "a reviewed protocol enables its normal telemetry without inheriting the board-only extended dump");

        object legacyMachine = PlatformMachine(machineType, "MECHREVO", "Offline test", "Unknown board");
        machineType.GetProperty("ProtocolProfile")!.SetValue(legacyMachine, "GCU-Legacy-ReadOnly");
        Require(PlatformFlag(legacyMachine, "SupportsEcTelemetry") && !PlatformFlag(legacyMachine, "SupportsModeControl") &&
            !PlatformFlag(legacyMachine, "SupportsCustomControl") && !PlatformFlag(legacyMachine, "SupportsPeripheralControl") &&
            !PlatformFlag(legacyMachine, "SupportsEcProtection") && !PlatformFlag(legacyMachine, "SupportsExtendedEcDiagnostics"),
            "legacy firmware observations grant RPM telemetry only and never authorize control or protection reads");

        object otherBrand = PlatformMachine(machineType, "OTHER", "Offline test", "Unknown board");
        machineType.GetProperty("ProtocolProfile")!.SetValue(otherBrand, "GCU-RamFan1.5");
        machineType.GetProperty("ProtocolWorkerHash")!.SetValue(otherBrand, ReviewedPlatformWorker);
        Require(!PlatformFlag(otherBrand, "SupportsEcTelemetry") && !PlatformFlag(otherBrand, "SupportsModeControl"),
            "a protocol profile cannot bypass the manufacturer requirement");
        machineType.GetProperty("ProtocolProfile")!.SetValue(otherBrand, "GCU-Legacy-ReadOnly");
        Require(!PlatformFlag(otherBrand, "SupportsEcTelemetry") && !PlatformFlag(otherBrand, "SupportsEcProtection"),
            "a legacy profile cannot grant telemetry to another manufacturer");
    }

    private static void CheckFullPlatformTelemetryGuard(Type machineType)
    {
        object machine = PlatformMachine(machineType, "MECHREVO", "Offline test", "Unknown board");
        machineType.GetProperty("ProtocolProfile")!.SetValue(machine, "GCU-RamFan1.5");
        machineType.GetProperty("ProtocolWorkerHash")!.SetValue(machine, ReviewedPlatformWorker);
        using var form = (Form)Activator.CreateInstance(MainForm, machine, false, false, false)!;
        var controller = new GcuController(new OfflineTransport());
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Set(controller, "_snapshot", PlatformControlSnapshot(now));
            Set(form, "_controller", controller);
            Set(form, "_machineBlockReason", null);
            var complete = new EcFanSnapshot
            {
                CpuFanRpm = 2000, GpuFanRpm = 2100, UpdatedAt = now,
                FanSafetyProtect = false, GenericThermalProtect = false
            };
            foreach ((EcFanSnapshot? telemetry, bool expected) in new (EcFanSnapshot?, bool)[]
            {
                (null, false),
                (complete with { FanSafetyProtect = null, GenericThermalProtect = null }, false),
                (complete with { FanSafetyProtect = null }, false),
                (complete with { GenericThermalProtect = null }, false),
                (complete with { UpdatedAt = now.AddMinutes(-1) }, false),
                (complete, true),
                (complete with { FanSafetyProtect = true }, true),
                (complete with { GenericThermalProtect = true }, false)
            })
            {
                Set(form, "_ecSnapshot", telemetry);
                Call(form, "RenderSnapshot");
                Require(Get<System.Collections.IDictionary>(form, "_modeButtons").Values.Cast<Control>().All(button => button.Enabled == expected) &&
                    Get<Dictionary<PerformanceMode, ToolStripMenuItem>>(form, "_trayModes").Values.All(item => item.Enabled == expected),
                    "new full platforms require fresh RPM and both protection observations; thermal protection blocks mode controls");
                Require(!Get<ToolStripMenuItem>(form, "_trayFanBoost").Enabled && !Get<Control>(form, "_fnLock").Enabled,
                    "full mode qualification does not enable custom or peripheral writes");
            }
        }
        finally
        {
            Set(form, "_controller", null);
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void CheckLegacyTelemetry()
    {
        // Construction and decoding are pure; never invoke ReadAsync or the driver.
        var reader = new EcFanReader(readProtection: false);
        Require(reader.ReadAddresses.Order().SequenceEqual(new[] { 1124, 1125, 1131, 1132 }),
            "legacy telemetry requests exactly the four reviewed RPM bytes");
        var values = new Dictionary<int, byte> { [1124] = 7, [1125] = 208, [1131] = 52, [1132] = 8 };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        EcFanSnapshot snapshot = EcFanDecoder.Decode(values, now, readProtection: false);
        Require(snapshot.CpuFanRpm == 2000 && snapshot.GpuFanRpm == 2100 && snapshot.IsFresh(now) && snapshot.LastError is null,
            "legacy telemetry decodes both RPM byte orders without needing protection registers");
        Require(snapshot.FanSafetyProtect is null && snapshot.GenericThermalProtect is null,
            "unread legacy protection flags remain unknown instead of being reported as inactive");
        values[EcFanRegisters.FanSafetyFlags] = 0xFF;
        values[EcFanRegisters.GenericThermalFlags] = 0xFF;
        snapshot = EcFanDecoder.Decode(values, now, readProtection: false);
        Require(snapshot.FanSafetyProtect is null && snapshot.GenericThermalProtect is null,
            "legacy decoding ignores incidental protection bytes even when supplied");
        values.Remove(1131);
        snapshot = EcFanDecoder.Decode(values, now, readProtection: false);
        Require(snapshot.CpuFanRpm is null && snapshot.GpuFanRpm is null && snapshot.LastError is not null,
            "incomplete legacy RPM data is rejected instead of constructing a partial observation");
    }

    private static void CheckPlatformDiagnostics(Type machineType, MethodInfo inspect, Type discovery)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo workerPath = discovery.GetMethod("WorkerPathFromService", statics)!;

        var missingServiceLog = new List<string>();
        Require(workerPath.Invoke(null, [false, null, (Action<string>)missingServiceLog.Add]) is null &&
            missingServiceLog.Any(line => line.Contains("未找到 GCUBridge 服务", StringComparison.Ordinal)),
            "diagnostics identify a missing bridge service without exposing a path");

        var malformedServiceLog = new List<string>();
        Require(workerPath.Invoke(null, [true, "not-a-service-command", (Action<string>)malformedServiceLog.Add]) is null &&
            malformedServiceLog.Any(line => line.Contains("未解析到有效的绝对程序路径", StringComparison.Ordinal)),
            "diagnostics identify an unusable service command without echoing its contents");

        string validBridge = Path.Combine(Path.GetTempPath(), "GCUBridge.exe");
        var validServiceLog = new List<string>();
        object? worker = workerPath.Invoke(null, [true, $"\"{validBridge}\" -service", (Action<string>)validServiceLog.Add]);
        Require(worker is string derivedWorkerPath && derivedWorkerPath.EndsWith(Path.Combine("MyControlCenter", "GCUService.exe"), StringComparison.OrdinalIgnoreCase) &&
            validServiceLog.Any(line => line.Contains("同目录 MyControlCenter/GCUService.exe", StringComparison.Ordinal)),
            "diagnostics explain how the hardware worker path is derived from the bridge service");

        object machine = PlatformMachine(machineType, "MECHREVO", "Offline test", "Unknown board");
        var unknownLog = new List<string>();
        object inspected = InspectPlatform(inspect, machine, Path.Combine(AppContext.BaseDirectory, "missing-platform", "GCUService.exe"),
            (_, _) => Task.FromResult(PlatformRegisters(1)), unknownLog.Add);
        Require(ReferenceEquals(inspected, machine) &&
            unknownLog.Any(line => line.Contains("不存在", StringComparison.Ordinal)) &&
            unknownLog.Any(line => line.Contains("厂商匹配=True", StringComparison.Ordinal)) &&
            unknownLog.All(line => !line.Contains("missing-platform", StringComparison.Ordinal)),
            "diagnostics report the missing worker and vendor decision without leaking the original path");

        var existingLog = new List<string>();
        inspected = InspectPlatform(inspect, machine, typeof(GcuController).Assembly.Location,
            (_, _) => Task.FromResult(PlatformRegisters(1)), existingLog.Add);
        Require(ReferenceEquals(inspected, machine) &&
            existingLog.Any(line => line.Contains("文件大小=", StringComparison.Ordinal)) &&
            existingLog.Any(line => line.Contains("SHA256=", StringComparison.Ordinal)) &&
            existingLog.Any(line => line.Contains("未匹配已核对样本", StringComparison.Ordinal)),
            "diagnostics record worker size and fingerprint before rejecting an unreviewed worker");

        CheckAppTypeDiagnostics(machineType, inspect);

        string retained = Path.GetFullPath(Path.Combine(".cache", "platform-diagnostics", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(retained);
        try
        {
            foreach (long length in new[] { 0L, 63L, 64L * 1024 * 1024 + 1 })
            {
                string path = Path.Combine(retained, $"worker-{length}.exe");
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) stream.SetLength(length);
                var logs = new List<string>();
                int reads = 0;
                inspected = InspectPlatform(inspect, machine, path, (_, _) =>
                {
                    reads++;
                    return Task.FromResult(PlatformRegisters(1));
                }, logs.Add);
                Require(ReferenceEquals(inspected, machine) && reads == 0 &&
                    logs.Any(line => line.Contains($"文件大小={length} 字节", StringComparison.Ordinal) &&
                        line.Contains("超出允许范围", StringComparison.Ordinal)),
                    "invalid worker sizes are explained before any capability read");
            }

            var deniedLogs = new List<string>();
            inspected = InspectPlatform(inspect, machine, retained, (_, _) =>
                throw new InvalidOperationException("a directory cannot authorize firmware reads"), deniedLogs.Add);
            Require(ReferenceEquals(inspected, machine) &&
                deniedLogs.Any(line => line.Contains("权限不足", StringComparison.Ordinal)) &&
                deniedLogs.All(line => !line.Contains(retained, StringComparison.Ordinal)),
                "opening a directory as a worker reports access failure without exposing its path");
        }
        finally { Console.WriteLine($"Platform diagnostic fixtures retained: {retained}"); }
    }

    private static void CheckAppTypeDiagnostics(Type machineType, MethodInfo inspect)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo classify = machineType.GetMethod("ClassifyOemAppTypeValue", statics)!;
        MethodInfo failure = machineType.GetMethod("ClassifyOemAppTypeFailure", statics)!;
        const string sentinel = "PLATFORM_DIAGNOSTIC_PRIVATE_SENTINEL";
        var observations = new List<(object Observation, int? Value, string Status, string Expected)>();
        foreach (var test in new (bool Key, object? Raw, int? Value, string Status, string Expected)[]
        {
            (false, null, null, "MissingKey", "注册项不存在"),
            (true, null, null, "MissingValue", "AppType 值缺失"),
            (true, sentinel, null, "InvalidType", "不是 DWORD 整数"),
            (true, "1", null, "InvalidType", "不是 DWORD 整数"),
            (true, 1L, null, "InvalidType", "不是 DWORD 整数"),
            (true, 0, null, "InvalidValue", "数值无效"),
            (true, 99, null, "InvalidValue", "数值无效"),
            (true, 1, 1, "Supported", "匹配已核对分支"),
            (true, 2, 2, "Supported", "匹配已核对分支"),
            (true, 3, 3, "UnsupportedBranch", "类型 3 使用不同的模式编号分支")
        })
            observations.Add((classify.Invoke(null, [test.Key, test.Raw])!, test.Value, test.Status, test.Expected));

        foreach (var test in new (Exception Error, string Status, string Expected)[]
        {
            (new UnauthorizedAccessException(sentinel), "AccessDenied", "读取 AppType 权限不足"),
            (new System.Security.SecurityException(sentinel), "AccessDenied", "读取 AppType 权限不足"),
            (new IOException(sentinel, new InvalidOperationException(sentinel)), "ReadFailed", "读取 AppType 失败")
        })
            observations.Add((failure.Invoke(null, [test.Error])!, null, test.Status, test.Expected));

        foreach (var test in observations)
        {
            Type observationType = test.Observation.GetType();
            object? value = observationType.GetProperty("Value")!.GetValue(test.Observation);
            object status = observationType.GetProperty("Status")!.GetValue(test.Observation)!;
            Require(Equals(value, test.Value) && status.ToString() == test.Status &&
                !test.Observation.ToString()!.Contains(sentinel, StringComparison.Ordinal),
                "application-type classification preserves support semantics without retaining raw registry data or exceptions");
            object machine = PlatformMachine(machineType, "MECHREVO", "Offline test", "Unknown board");
            machineType.GetProperty("OemAppType")!.SetValue(machine, value);
            machineType.GetProperty("OemAppTypeStatus")!.SetValue(machine, status);
            var logs = new List<string>();
            InspectPlatform(inspect, machine, Path.Combine(AppContext.BaseDirectory, "missing-platform", "GCUService.exe"),
                (_, _) => throw new InvalidOperationException("a missing worker cannot query firmware"), logs.Add);
            Require(logs.Any(line => line.Contains(test.Expected, StringComparison.Ordinal)) &&
                logs.All(line => !line.Contains(sentinel, StringComparison.Ordinal)),
                "platform diagnostics distinguish missing, malformed, inaccessible and unsupported application types safely");
        }
    }

    private static void CheckUnknownPlatformControls(Type machineType, string? profile)
    {
        object machine = PlatformMachine(machineType, "MECHREVO", "Offline test", "Unknown board");
        machineType.GetProperty("ProtocolProfile")!.SetValue(machine, profile);
        using var form = (Form)Activator.CreateInstance(MainForm, machine, false, false, false)!;
        var transport = new OfflineTransport();
        var controller = new GcuController(transport);
        Type moreType = MainForm.GetNestedType("MoreSettingsForm", BindingFlags.NonPublic)!;
        using var more = (Form)Activator.CreateInstance(moreType, form)!;
        Set(form, "_settingsForm", more);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Set(controller, "_snapshot", PlatformControlSnapshot(now));
            Set(form, "_controller", controller);
            Set(form, "_gpuRoute", new GpuRouteSnapshot { Route = GpuDisplayRoute.Hybrid, UpdatedAt = now });
            Set(form, "_ecSnapshot", new EcFanSnapshot { CpuFanRpm = 2000, GpuFanRpm = 2100, UpdatedAt = now });
            Call(form, "RenderSnapshot");
            Require(Get<Dictionary<PerformanceMode, ToolStripMenuItem>>(form, "_trayModes").Values.All(item => !item.Enabled) &&
                Get<Dictionary<GpuDisplayRoute, ToolStripMenuItem>>(form, "_trayGpuModes").Values.All(item => !item.Enabled) &&
                !Get<ToolStripMenuItem>(form, "_trayFanBoost").Enabled,
                "complete live data does not enable controls on an unidentified or legacy platform");
            Require(new[] { "_fnLock", "_winLock", "_backlightButton", "_keyboardEffect", "_keyboardColor" }
                    .All(name => !Get<Control>(form, name).Enabled),
                "complete keyboard reports do not enable unknown or legacy platform writes");
            Require(Get<Dictionary<GcuQuickSetting, CheckBox>>(more, "_quickSettings").Values.All(check => !check.Enabled) &&
                !Get<Control>(more, "_brightness").Enabled,
                "more-settings switches and brightness respect unknown or legacy platform restrictions");
            Wait((Task)Call(form, "SwitchModeAsync", PerformanceMode.Silent)!);
            Wait((Task)Call(form, "SwitchGpuModeAsync", GpuDisplayRoute.Direct)!);
            Wait((Task)Call(form, "ToggleTrayFanBoostAsync")!);
            foreach (GcuQuickSetting setting in Enum.GetValues<GcuQuickSetting>())
                Wait((Task)Call(form, "ToggleQuickSettingAsync", setting)!);
            Wait((Task)Call(form, "ToggleBacklightPowerAsync")!);
            Set(more, "_brightnessDirty", true);
            Wait((Task)Call(more, "ApplyBrightnessAsync")!);
            int keyboardWrites = 0;
            Func<Task<GcuWriteResult>> keyboardOperation = () =>
            {
                keyboardWrites++;
                return Task.FromResult(new GcuWriteResult(GcuWriteStatus.Confirmed, "offline confirmation"));
            };
            Wait((Task)Call(form, "RunKeyboardAsync", keyboardOperation)!);
            Require(transport.Publishes == 0 && keyboardWrites == 0,
                "direct mode, keyboard and quick-setting handlers reject unknown and legacy platforms before sending");
        }
        finally
        {
            Set(form, "_settingsForm", null);
            Set(form, "_controller", null);
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void CheckCustomPlatformGuard()
    {
        Type customType = typeof(GcuController).Assembly.GetType("YALM.Mechrevo.CustomModeForm")!;
        var controller = new GcuController(new OfflineTransport());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Set(controller, "_snapshot", PlatformControlSnapshot(now));
        string? blockReason = "尚未识别到已适配的硬件协议，当前仅查看状态。";
        Func<string?> guard = () => blockReason;
        Func<EcFanSnapshot?> telemetry = () => new EcFanSnapshot { CpuFanRpm = 2000, GpuFanRpm = 2100, UpdatedAt = now };
        using var form = (Form)Activator.CreateInstance(customType, controller, telemetry, false, guard)!;
        try
        {
            Require((string?)Call(form, "GetWriteBlockReason", true) == blockReason &&
                (string?)Call(form, "GetWriteBlockReason", false) == blockReason,
                "platform restrictions apply to both custom edits and profile activation");
            Require(!Get<Control>(form, "_activateProfile").Enabled && !Get<Control>(form, "_fanBoost").Enabled &&
                !Get<Control>(form, "_applyCpuCurve").Enabled && !Get<Control>(form, "_applyGpuCurve").Enabled,
                "the custom editor displays platform restrictions even with fresh complete state");
            int writes = 0;
            Func<Task<GcuWriteResult>> action = () =>
            {
                writes++;
                return Task.FromResult(new GcuWriteResult(GcuWriteStatus.Confirmed, "offline confirmation"));
            };
            Wait((Task)Call(form, "RunAsync", "offline request", action, true)!);
            Wait((Task)Call(form, "RunAsync", "offline request", action, false)!);
            Require(writes == 0, "direct custom write calls obey the platform guard");
            blockReason = null;
            Call(form, "RenderSnapshot", false);
            Require(Call(form, "GetWriteBlockReason", true) is null && Get<Control>(form, "_activateProfile").Enabled,
                "the same complete state becomes eligible when the platform restriction is resolved");
            blockReason = "offline platform changed";
            Wait((Task)Call(form, "RunAsync", "offline request", action, true)!);
            Require(writes == 0 && Get<Label>(form, "_status").Text == blockReason,
                "a platform restriction is rechecked at click time after a previously enabled render");
        }
        finally
        {
            Call(form, "OnClosed", form, new FormClosedEventArgs(CloseReason.None));
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static GcuSnapshot PlatformControlSnapshot(DateTimeOffset now) => new()
    {
        IsConnected = true, Mode = 3, ModeUpdatedAt = now, IsAc = true, IsAcUpdatedAt = now,
        TurboSupported = true, TurboSupportedUpdatedAt = now,
        CpuFanRpm = 2000, CpuFanUpdatedAt = now, GpuFanRpm = 2100, GpuFanUpdatedAt = now,
        GpuTarget = GpuDisplayRoute.Hybrid, GpuTargetUpdatedAt = now,
        GpuSwitchSupported = true, GpuSwitchSupportUpdatedAt = now,
        FnLocked = false, FnLockUpdatedAt = now,
        Keyboard = new GcuKeyboardSettings
        {
            Type = "SingleZone", Effect = "Single", Power = true, Brightness = 3, MonochromeIndex = 17,
            ManualIndices = [1, 2, 3, 4, 5, 6], ManualInterval = 5, BreathingIndex = 5
        }, KeyboardUpdatedAt = now,
        QuickSettings = Enum.GetValues<GcuQuickSetting>().ToDictionary(setting => setting, _ => new GcuQuickSettingState(false, now)),
        CustomSettings = new GcuCustomSettings { ProfileIndex = 0, FanBoostEnabled = false }, CustomSettingsUpdatedAt = now,
        FanTable = new FanCurveTable(true, "offline", false, null, null, [], []), FanTableUpdatedAt = now
    };

    // Optional vendor-file coverage; ordinary regression tests need no package
    // cache. Files are hashed as data and all firmware reads use injected values.
    private static void CheckPlatformManifest(string[] args, Type machineType, MethodInfo inspect)
    {
        int index = Array.IndexOf(args, "--platform-manifest");
        if (index < 0) return;
        Require(index + 1 < args.Length && args.Count(arg => arg == "--platform-manifest") == 1,
            "the platform manifest option requires one absolute path");
        string manifest = args[index + 1];
        Require(Path.IsPathFullyQualified(manifest), "the platform manifest path must be absolute");
        PlatformFixture[]? fixtures = JsonSerializer.Deserialize<PlatformFixture[]>(File.ReadAllText(manifest));
        Require(fixtures is { Length: > 0 }, "the platform manifest requires at least one local worker");
        string? retainedDirectory = null;
        try
        {
            foreach (PlatformFixture fixture in fixtures!)
            {
                Require(Path.IsPathFullyQualified(fixture.Path) && fixture.Hash.Length == 64 && fixture.Hash.All(Uri.IsHexDigit),
                    "platform fixtures require an absolute file path and a SHA-256 fingerprint");
                using (FileStream stream = File.OpenRead(fixture.Path))
                    Require(Convert.ToHexString(SHA256.HashData(stream)).Equals(fixture.Hash, StringComparison.OrdinalIgnoreCase),
                        "the local hardware worker matches its expected fingerprint");
                object machine = PlatformMachine(machineType, "MECHREVO", "Offline test", "Unknown board");
                int reads = 0;
                object inspected = InspectPlatform(inspect, machine, fixture.Path, (addresses, _) =>
                {
                    reads++;
                    Require(addresses.Order().SequenceEqual(new[] { PlatformProjectIdAddress, PlatformFanCapabilityAddress }.Order()),
                        "platform discovery reads only the two reviewed capability addresses");
                    return Task.FromResult(PlatformRegisters(1));
                });
                Require(PlatformFlag(inspected, "SupportsModeControl") == fixture.ExpectedSupported &&
                    PlatformFlag(inspected, "SupportsEcTelemetry") == fixture.ExpectedSupported &&
                    PlatformFlag(inspected, "SupportsEcProtection") == fixture.ExpectedSupported && reads == (fixture.ExpectedSupported ? 1 : 0),
                    "only an audited worker can query firmware capabilities and establish the platform");
                Require(!PlatformFlag(inspected, "SupportsExtendedEcDiagnostics"),
                    "protocol discovery does not authorize the board-only extended diagnostics");
                if (!fixture.ExpectedSupported) continue;
                CheckReviewedPlatformDiagnostics(inspect, fixture.Path, inspected);

                foreach (int? appType in new int?[] { null, 0, 3, 99 })
                {
                    object unsupportedApp = PlatformMachine(machineType, "MECHREVO", "Offline test", "Unknown board");
                    machineType.GetProperty("ProtocolProfile")!.SetValue(unsupportedApp, "GCU-RamFan1.5");
                    machineType.GetProperty("ProtocolWorkerHash")!.SetValue(unsupportedApp, fixture.Hash);
                    machineType.GetProperty("OemAppType")!.SetValue(unsupportedApp, appType);
                    reads = 0;
                    var logs = new List<string>();
                    object denied = InspectPlatform(inspect, unsupportedApp, fixture.Path, (_, _) =>
                    {
                        reads++;
                        return Task.FromResult(PlatformRegisters(1));
                    }, logs.Add);
                    string expected = appType switch
                    {
                        null => "未采集到有效的 AppType",
                        3 => "类型 3 使用不同的模式编号分支",
                        _ => "应用类型数值无效"
                    };
                    Require(PlatformWasRevoked(denied) && reads == 0 &&
                        logs.Any(line => line.Contains(expected, StringComparison.Ordinal)) &&
                        logs.Any(line => line.Contains("跳过专用寄存器探测", StringComparison.Ordinal)),
                        $"application type {appType?.ToString() ?? "unknown"} revokes a previous full profile before querying firmware");
                }
                object secondAppType = PlatformMachine(machineType, "MECHREVO", "Offline test", "Unknown board");
                machineType.GetProperty("OemAppType")!.SetValue(secondAppType, 2);
                reads = 0;
                object secondResult = InspectPlatform(inspect, secondAppType, fixture.Path, (_, _) =>
                {
                    reads++;
                    return Task.FromResult(PlatformRegisters(1));
                });
                Require(reads == 1 && PlatformFlag(secondResult, "SupportsModeControl") && PlatformFlag(secondResult, "SupportsEcProtection"),
                    "the second reviewed application type can establish the full protocol profile");

                foreach (EcRegisterSnapshot rejected in new[] { PlatformRegisters(1, 0), PlatformRegisters(0x81), new EcRegisterSnapshot() })
                {
                    object denied = InspectPlatform(inspect, inspected, fixture.Path, (_, _) => Task.FromResult(rejected));
                    Require(PlatformWasRevoked(denied),
                        "lost capability bits, unsupported projects or incomplete firmware responses revoke a previously supported profile");
                }
                foreach (object bypassMachine in new[]
                {
                    PlatformMachine(machineType, "OTHER", "Offline test", "Unknown board"),
                    PlatformMachine(machineType, "MECHREVO", "Offline test", "GM6PX0X")
                })
                {
                    reads = 0;
                    object bypassed = InspectPlatform(inspect, bypassMachine, fixture.Path, (_, _) =>
                    {
                        reads++;
                        return Task.FromResult(PlatformRegisters(1));
                    });
                    Require(ReferenceEquals(bypassed, bypassMachine) && reads == 0,
                        "other brands and an already verified board require no additional firmware probes");
                }

                retainedDirectory ??= Path.GetFullPath(Path.Combine(".cache", "platform-checks", Guid.NewGuid().ToString("N")));
                Directory.CreateDirectory(retainedDirectory);
                string mutated = Path.Combine(retainedDirectory, fixture.Hash + "-modified.exe");
                using (FileStream source = File.OpenRead(fixture.Path))
                using (var destination = new FileStream(mutated, FileMode.CreateNew, FileAccess.Write))
                {
                    source.CopyTo(destination);
                    destination.WriteByte(0xA5);
                }
                reads = 0;
                object modifiedResult = InspectPlatform(inspect, machine, mutated, (_, _) =>
                {
                    reads++;
                    return Task.FromResult(PlatformRegisters(1));
                });
                Require(ReferenceEquals(modifiedResult, machine) && reads == 0,
                    "a modified worker cannot reach the injected firmware reader");
            }
        }
        finally
        {
            if (retainedDirectory is not null) Console.WriteLine($"Platform mutation fixtures retained: {retainedDirectory}");
        }
    }

    private static void CheckReviewedPlatformDiagnostics(MethodInfo inspect, string path, object previous)
    {
        const string sentinel = "PLATFORM_CAPABILITY_PRIVATE_SENTINEL";
        foreach (var test in new (EcRegisterSnapshot Registers, string Reason, string Detail)[]
        {
            (PlatformRegisters(1) with { ErrorCode = 5, LastError = sentinel }, "固件能力读取报错", "错误码=5"),
            (PlatformRegisters(1) with { LastError = sentinel }, "固件能力读取报错", "存在读取错误=True"),
            (new EcRegisterSnapshot(), "固件能力寄存器缺失：1934、1856", "寄存器 1934=缺失"),
            (new EcRegisterSnapshot { Registers = new Dictionary<int, byte> { [PlatformFanCapabilityAddress] = 0x40 } },
                "固件能力寄存器缺失：1856", "原始项目号 1856=缺失"),
            (new EcRegisterSnapshot { Registers = new Dictionary<int, byte> { [PlatformProjectIdAddress] = 1 } },
                "固件能力寄存器缺失：1934", "寄存器 1934=缺失"),
            (PlatformRegisters(1, 0), "风扇能力位 0x40 未置位", "寄存器 1934=0x00"),
            (PlatformRegisters(0x81), "原始项目号未匹配已核对的普通协议分支", "原始项目号 1856=129（0x81）")
        })
        {
            var logs = new List<string>();
            object denied = InspectPlatform(inspect, previous, path, (_, _) => Task.FromResult(test.Registers), logs.Add);
            Require(PlatformWasRevoked(denied) &&
                logs.Any(line => line.Contains(test.Reason, StringComparison.Ordinal)) &&
                logs.Any(line => line.Contains(test.Detail, StringComparison.Ordinal)) &&
                logs.All(line => !line.Contains(sentinel, StringComparison.Ordinal)),
                "capability rejection diagnostics preserve numeric evidence and precise reasons without copying raw errors");
        }

        foreach (Exception error in new Exception[]
        {
            new IOException(sentinel, new InvalidOperationException(sentinel)),
            new UnauthorizedAccessException(sentinel),
            new ArgumentException(sentinel),
            new System.Security.SecurityException(sentinel)
        })
        {
            var logs = new List<string>();
            object denied = InspectPlatform(inspect, previous, path,
                (_, _) => Task.FromException<EcRegisterSnapshot>(error), logs.Add);
            Require(PlatformWasRevoked(denied) &&
                logs.Any(line => line.Contains("固件能力读取失败", StringComparison.Ordinal) &&
                    line.Contains(error.GetType().Name, StringComparison.Ordinal)) &&
                logs.All(line => !line.Contains(sentinel, StringComparison.Ordinal)),
                "capability read exceptions revoke support and report only the safe exception summary");
        }
    }

    private static EcRegisterSnapshot PlatformRegisters(byte project, byte flags = 0x40) => new()
    {
        Registers = new Dictionary<int, byte>
        {
            [PlatformFanCapabilityAddress] = flags,
            [PlatformProjectIdAddress] = project
        }
    };

    private static bool PlatformSupports(MethodInfo supports, string hash, EcRegisterSnapshot registers) =>
        (bool)supports.Invoke(null, [hash, registers])!;

    private static object PlatformMachine(Type machineType, string manufacturer, string model, string board)
    {
        object machine = Activator.CreateInstance(machineType, manufacturer, model, "", board)!;
        machineType.GetProperty("OemAppType")!.SetValue(machine, 1);
        return machine;
    }

    private static bool PlatformFlag(object machine, string property) =>
        (bool)machine.GetType().GetProperty(property)!.GetValue(machine)!;

    private static bool PlatformWasRevoked(object machine) =>
        machine.GetType().GetProperty("ProtocolProfile")!.GetValue(machine) is null &&
        machine.GetType().GetProperty("ProtocolWorkerHash")!.GetValue(machine) is null &&
        !PlatformFlag(machine, "SupportsModeControl") && !PlatformFlag(machine, "SupportsEcTelemetry") &&
        !PlatformFlag(machine, "SupportsEcProtection") && !PlatformFlag(machine, "SupportsCustomControl") &&
        !PlatformFlag(machine, "SupportsPeripheralControl");

    private static object InspectPlatform(MethodInfo inspect, object machine, string path,
        Func<IEnumerable<int>, CancellationToken, Task<EcRegisterSnapshot>> readRegisters,
        Action<string>? log = null)
    {
        var task = (Task)inspect.Invoke(null, [machine, path, readRegisters, log, CancellationToken.None])!;
        task.GetAwaiter().GetResult();
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private sealed record PlatformFixture(string Path, string Hash, bool ExpectedSupported);
}
