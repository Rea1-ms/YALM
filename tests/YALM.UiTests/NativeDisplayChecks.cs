using System.Reflection;
using YALM.Mechrevo;

namespace YALM.UiTests;

internal static partial class Program
{
    private static void CheckNativeDisplay()
    {
        CheckJiaolongDisplayAndUnavailableControls();
        CheckNativeDisplayExpiryAndSourceChange();
        CheckNumberedFanDisplay();
    }

    private static void CheckJiaolongDisplayAndUnavailableControls()
    {
        using Form form = CreateNativeDisplayForm();
        var backend = new NativeDisplayBackend();
        object reader = InspectNativeDisplayReader(backend);
        GcuSnapshot snapshot = ReadNativeDisplay(reader);
        Require(snapshot.Mode == 2 && snapshot.IsAc is null && snapshot.CpuFanRpm == 2111 && snapshot.GpuFanRpm == 3222,
            "the native display fixture reads independent mode and fan fields without inventing AC power");
        Set(form, "_nativeReader", reader);
        Set(form, "_nativeSnapshot", snapshot);
        Set(form, "_fanRpm", true);
        Call(form, "RenderSnapshot");
        Require(Get<Label>(form, "_reportedMode").Text == "性能模式：狂暴" &&
            Get<NotifyIcon>(form, "_trayIcon").Text.Contains("狂暴"),
            "a fresh native mode appears in the main window and tray even when AC state is unavailable");
        var modes = Get<System.Collections.IDictionary>(form, "_modeButtons");
        Require(Active((Button)modes[PerformanceMode.Turbo]!) && !Active((Button)modes[PerformanceMode.Balanced]!),
            "the actual native preset is selected independently of whether controls are enabled");
        Require(Get<Label>(form, "_cpuValues").Text.Contains("2111 RPM") &&
            Get<Label>(form, "_gpuValues").Text.Contains("3222 RPM"),
            "native fan telemetry is displayed without an MQTT controller");
        Require(Get<Label>(form, "_connectionStatus").Text.Contains("蛟龙只读接口"),
            "the connection status identifies the actual native read source");
        CheckNativeControlsUnavailable(form);
        Call(form, "OpenCustomMode");
        Require(Get<Form?>(form, "_customModeForm") is null,
            "directly invoking the custom-window entry does not create an editor for native telemetry");
        foreach (PerformanceMode mode in Enum.GetValues<PerformanceMode>())
            Wait((Task)Call(form, "SwitchModeAsync", mode)!);
        Require(backend.Reads == 2, "native display and disabled control handlers never send firmware reads or writes");
        Require(!form.Visible && !Get<NotifyIcon>(form, "_trayIcon").Visible,
            "offline native display checks never show a window or register the tray icon");
    }

    private static void CheckNativeDisplayExpiryAndSourceChange()
    {
        using Form form = CreateNativeDisplayForm();
        var backend = new NativeDisplayBackend();
        object reader = InspectNativeDisplayReader(backend);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GcuSnapshot native = ReadNativeDisplay(reader) with
        {
            CpuTemperature = 64, CpuTemperatureUpdatedAt = now,
            GpuTemperature = 57, GpuTemperatureUpdatedAt = now
        };
        var transport = new OfflineTransport();
        var controller = new GcuController(transport);
        try
        {
            var service = new GcuSnapshot
            {
                IsConnected = true, Mode = 0, ModeUpdatedAt = now,
                CpuFanRpm = 9111, CpuFanUpdatedAt = now, GpuFanRpm = 9222, GpuFanUpdatedAt = now,
                CpuTemperature = 81, CpuTemperatureUpdatedAt = now, GpuTemperature = 82, GpuTemperatureUpdatedAt = now,
                IsAc = true, IsAcUpdatedAt = now
            };
            Set(controller, "_snapshot", service);
            Set(form, "_controller", controller);
            Set(form, "_nativeReader", reader);
            Set(form, "_nativeSnapshot", native);
            Set(form, "_ecSnapshot", new EcFanSnapshot
            {
                CpuFanRpm = 7333, GpuFanRpm = 7444, UpdatedAt = now,
                FanSafetyProtect = false, GenericThermalProtect = false
            });
            Set(form, "_fanRpm", true);
            Call(form, "RenderSnapshot");
            Require(Get<Label>(form, "_reportedMode").Text.Contains("狂暴") &&
                Get<Label>(form, "_cpuValues").Text.Contains("2111 RPM") &&
                !Get<Label>(form, "_cpuValues").Text.Contains("9111"),
                "a native session takes display precedence over retained service and EC snapshots");
            object overlay = Call(form, "ReadOverlay")!;
            Require(OverlayNumber(overlay, "CpuTemperature") == 64 && OverlayNumber(overlay, "GpuFanRpm") == 3222,
                "the overlay reads the same active native snapshot as the main window");
            CheckNativeControlsUnavailable(form);

            foreach (GcuSnapshot unavailable in new[]
            {
                native with { IsConnected = false },
                native with
                {
                    ModeUpdatedAt = now.AddMinutes(-1), CpuFanUpdatedAt = now.AddMinutes(-1),
                    GpuFanUpdatedAt = now.AddMinutes(-1), CpuTemperatureUpdatedAt = now.AddMinutes(-1),
                    GpuTemperatureUpdatedAt = now.AddMinutes(-1)
                },
                new GcuSnapshot { LastError = "离线测试读取失败。" }
            })
            {
                Set(form, "_nativeSnapshot", unavailable);
                Call(form, "RenderSnapshot");
                Require(Get<Label>(form, "_reportedMode").Text == "性能模式：未收到" &&
                    Get<Dictionary<PerformanceMode, ToolStripMenuItem>>(form, "_trayModes").Values.All(item => !item.Checked),
                    "failed or stale native modes clear main and tray selections instead of borrowing service state");
                Require(Get<Label>(form, "_cpuValues").Text == "CPU: 未收到  风扇: 未收到" &&
                    Get<Label>(form, "_gpuValues").Text == "GPU: 未收到  风扇: 未收到",
                    "failed and expired native telemetry never displays retained numbers");
                object missingOverlay = Call(form, "ReadOverlay")!;
                Require(new[] { "CpuTemperature", "GpuTemperature", "CpuFanRpm", "GpuFanRpm" }
                    .All(name => OverlayNumber(missingOverlay, name) is null),
                    "the overlay clears failed or expired native telemetry along with the main window");
                CheckNativeControlsUnavailable(form);
            }

            backend.FailReads = true;
            Set(form, "_nativeSnapshot", ReadNativeDisplay(reader));
            Call(form, "RenderSnapshot");
            Require(Get<Label>(form, "_reportedMode").Text == "性能模式：未收到" &&
                Get<Label>(form, "_connectionStatus").Text.Contains("暂未收到有效状态"),
                "a failed injected read cannot leave the previous native values visible");

            Set(form, "_nativeReader", null);
            Set(form, "_nativeSnapshot", native);
            Set(form, "_ecSnapshot", null);
            Call(form, "RenderSnapshot");
            Require(Get<Label>(form, "_reportedMode").Text == "性能模式：安静" &&
                Get<Label>(form, "_cpuValues").Text.Contains("9111 RPM") &&
                Get<Label>(form, "_connectionStatus").Text == "已连接本机服务",
                "clearing the native reader restores the actual service display source");
            object serviceOverlay = Call(form, "ReadOverlay")!;
            Require(OverlayNumber(serviceOverlay, "CpuTemperature") == 81 && OverlayNumber(serviceOverlay, "GpuFanRpm") == 9222,
                "the overlay also returns to the service snapshot");
            Set(form, "_controller", null);
            Call(form, "RenderSnapshot");
            Require(Get<Label>(form, "_reportedMode").Text == "性能模式：未收到" &&
                Get<Label>(form, "_cpuValues").Text == "CPU: 未收到  风扇: 未收到",
                "clearing both readers cannot expose a cached native snapshot");
            Require(transport.Publishes == 0 && !form.Visible,
                "display source transitions publish no service commands and show no window");
        }
        finally
        {
            Set(form, "_controller", null);
            controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void CheckNumberedFanDisplay()
    {
        using Form form = CreateNativeDisplayForm();
        var backend = new NativeDisplayBackend { Family = NativeHardwareFamily.Imini };
        object reader = InspectNativeDisplayReader(backend);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GcuSnapshot numbered = ReadNativeDisplay(reader);
        Require(numbered.CpuFanRpm is null && numbered.GpuFanRpm is null && numbered.CpuTemperature == 64 &&
            numbered.GpuTemperature is null, "mini-PC observations retain the CPU temperature without assigning fan targets");
        Set(form, "_nativeReader", reader);
        Set(form, "_nativeSnapshot", numbered);
        foreach (bool fanRpm in new[] { true, false })
        {
            Set(form, "_fanRpm", fanRpm);
            Call(form, "RenderSnapshot");
            Require(Get<Label>(form, "_cpuValues").Text == "CPU: 64 ℃  风扇 1: 1444 RPM" &&
                Get<Label>(form, "_gpuValues").Text == "风扇 2: 2555 RPM",
                "numbered fans keep neutral labels and actual RPM instead of implying CPU/GPU ownership or duty");
            object overlay = Call(form, "ReadOverlay")!;
            Require(OverlayNumber(overlay, "CpuFanRpm") is null && OverlayNumber(overlay, "GpuFanRpm") is null &&
                OverlayNumber(overlay, "CpuTemperature") == 64 && OverlayNumber(overlay, "GpuTemperature") is null,
                "numbered fans do not fabricate processor-specific overlay telemetry");
            CheckNativeControlsUnavailable(form);
        }
        Set(form, "_nativeSnapshot", numbered with { Fan1UpdatedAt = now.AddMinutes(-1), Fan2UpdatedAt = now.AddMinutes(-1) });
        Call(form, "RenderSnapshot");
        Require(Get<Label>(form, "_cpuValues").Text == "CPU: 64 ℃  风扇 1: 未收到" &&
            Get<Label>(form, "_gpuValues").Text == "风扇 2: 未收到",
            "numbered fan labels remain neutral when their observations expire");
        Require(!form.Visible, "numbered fan formatting checks do not show a window");
    }

    private static void CheckNativeControlsUnavailable(Form form)
    {
        Require(Get<System.Collections.IDictionary>(form, "_modeButtons").Values.Cast<Control>().All(button => !button.Enabled) &&
            Get<Dictionary<PerformanceMode, ToolStripMenuItem>>(form, "_trayModes").Values.All(item => !item.Enabled),
            "native readings never enable main-window or tray preset controls");
        Require(!Get<Control>(form, "_openCustomMode").Enabled && !Get<ToolStripMenuItem>(form, "_trayCustomSettings").Enabled &&
            !Get<ToolStripMenuItem>(form, "_trayCustomMode").Enabled && !Get<ToolStripMenuItem>(form, "_trayFanBoost").Enabled,
            "native readings keep custom-window and fan-boost entry points unavailable");
        Require(new[] { "_fnLock", "_winLock", "_backlightButton", "_keyboardEffect", "_keyboardColor" }
            .All(name => !Get<Control>(form, name).Enabled) &&
            Get<System.Collections.IDictionary>(form, "_gpuModeButtons").Values.Cast<Control>().All(button => !button.Enabled) &&
            Get<Dictionary<GpuDisplayRoute, ToolStripMenuItem>>(form, "_trayGpuModes").Values.All(item => !item.Enabled),
            "native readings never authorize peripheral controls through main-window or tray entries");
    }

    private static Form CreateNativeDisplayForm()
    {
        Type machineType = MainForm.Assembly.GetType("YALM.Mechrevo.MachineInfo")!;
        object machine = Activator.CreateInstance(machineType, "MECHREVO", "Offline native display", "", "Unknown board")!;
        var form = (Form)Activator.CreateInstance(MainForm, machine, false, false, false)!;
        SetControlConflict(form, []);
        Call(form, "RefreshMachineBlockReason");
        return form;
    }

    private static object InspectNativeDisplayReader(NativeDisplayBackend backend)
    {
        Type readerType = MainForm.Assembly.GetType("YALM.Mechrevo.NativeHardwareReader")!;
        Task inspection = (Task)readerType.GetMethod("InspectAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [true, backend, null, CancellationToken.None])!;
        inspection.GetAwaiter().GetResult();
        object? reader = inspection.GetType().GetProperty("Result")!.GetValue(inspection);
        Require(reader is not null, "the native display fixture uses the reviewed injected endpoint");
        return reader!;
    }

    private static GcuSnapshot ReadNativeDisplay(object reader)
    {
        var task = (Task<GcuSnapshot>)reader.GetType().GetMethod("ReadAsync")!.Invoke(reader, [CancellationToken.None])!;
        return task.GetAwaiter().GetResult();
    }

    private static double? OverlayNumber(object overlay, string name) => overlay.GetType().GetProperty(name)!.GetValue(overlay) switch
    {
        int integer => integer,
        double number => number,
        _ => null
    };

    private sealed class NativeDisplayBackend : INativeHardwareReadBackend
    {
        public int Reads { get; private set; }
        public bool FailReads { get; set; }
        public NativeHardwareFamily Family { get; init; } = NativeHardwareFamily.Jiaolong;
        public IReadOnlyList<NativeHardwareEndpoint> Inspect(CancellationToken ct) => Family == NativeHardwareFamily.Imini
            ? [new(NativeHardwareFamily.Imini, @"PowerSwitchInterface.InstanceName='ACPI\PNP0C14\IP3POWERSWITCH_0'",
                "C15B7057C88B2DBBF304F5DCA79DA276AD909451C709C7CA9493A525049F040D", NativeHardwareSchemaStatus.Matched)
                { CpuTemperatureSupported = true }]
            : [new(NativeHardwareFamily.Jiaolong, JiaolongReadProtocol.InstancePath,
                JiaolongReadProtocol.LegacyImageHash, NativeHardwareSchemaStatus.Matched)];
        public object? Read(NativeHardwareEndpoint endpoint, NativeHardwareReading reading, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Reads++;
            if (FailReads) throw new IOException("offline native read failure");
            if (Family == NativeHardwareFamily.Imini)
                return reading switch
                {
                    NativeHardwareReading.Mode => 0u,
                    NativeHardwareReading.FanCount => 2u,
                    NativeHardwareReading.GpuSupport => 1u,
                    NativeHardwareReading.FanSpeeds => (2555u << 16) | 1444u,
                    NativeHardwareReading.CpuTemperature => 64u,
                    _ => throw new InvalidOperationException("unexpected mini-PC read operation")
                };
            byte[] response = new byte[32];
            switch (reading)
            {
                case NativeHardwareReading.Mode:
                    response[4] = 1;
                    break;
                case NativeHardwareReading.FanSpeeds:
                    response[4] = 0x3F; response[5] = 0x08;
                    response[6] = 0x96; response[7] = 0x0C;
                    break;
                default:
                    throw new InvalidOperationException("unexpected native read operation");
            }
            return response;
        }
    }
}
