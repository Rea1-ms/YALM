using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace YALM.Mechrevo;

/// <summary>A bounded, read-only service and EC diagnostic. Never writes an EC register.</summary>
internal static class MechrevoProbe
{
    private const string GetStatus = "{\"Action\":\"GETSTATUS\"}";

    private static readonly string[] AdditionalTopics =
    [
        "Fan/Table",
        "Keyboard/Status",
        "Setting/Status",
        "Settings/DeviceSwitchItemStatus",
        "System/BatteryProtection",
        "System/FanErrorInfo",
        "GPUDevice/#",
        "LCHWOC/Status",
        "Customize/#"
    ];

    private static readonly int[] CoreRegisterAddresses =
    [
        1830, // 0x726 custom-mode flag
        1873, // 0x751 fan mode and boost
        1883, // 0x75B CPU fan duty
        1884, // 0x75C GPU fan duty
        1923, 1924, 1925, 1926, 1927, // 0x783..0x787 power, TCC, fan speed
        1957, // 0x7A5 fan-safety flags
        1958, // 0x7A6 battery-protection mode byte
        1977, // 0x7B9 charge upper limit
        1989, // 0x7C5 fan table flags
        1990, // 0x7C6 fan table enable
        1992, // 0x7C8 thermal flags
        2000  // 0x7D0 charge lower limit
    ];

    private static readonly int[] FanCurveAddresses =
    [
        3840, 3841, 3842, 3843, 3844, 3845, 3846, 3847,
        3848, 3849, 3850, 3851, 3852, 3853, 3854, 3855,
        3857, 3858, 3859, 3860, 3861, 3862, 3863, 3864,
        3865, 3866, 3867, 3868, 3869, 3870, 3871,
        3872, 3873, 3874, 3875, 3876, 3877, 3878, 3879,
        3880, 3881, 3882, 3883, 3884, 3885, 3886, 3887,
        3888, 3889, 3890, 3891, 3892, 3893, 3894, 3895,
        3896, 3897, 3898, 3899, 3900, 3901, 3902, 3903,
        3905, 3906, 3907, 3908, 3909, 3910, 3911, 3912,
        3913, 3914, 3915, 3916, 3917, 3918, 3919,
        3920, 3921, 3922, 3923, 3924, 3925, 3926, 3927,
        3928, 3929, 3930, 3931, 3932, 3933, 3934, 3935
    ];

    public static async Task<int> RunAsync(string outputPath, int captureSeconds = 15)
    {
        if (!Path.IsPathFullyQualified(outputPath)) return 2;
        if (captureSeconds is < 5 or > 120) return 2;

        MachineInfo machine = await GcuPlatformDiscovery.DiscoverAsync(MachineInfo.Read(), Logger.WriteLine);
        GcuConnectionOptions? options = GcuConnectionDiscovery.Find(machine, out string? discoveryFailure, Logger.WriteLine);
        var samples = new List<object>();
        var curves = new List<object>();
        var frames = new ConcurrentDictionary<string, JsonElement>();
        // These registers are specific to the verified EC layout. A working
        // service connection does not establish a compatible hardware map.
        bool ecSupported = machine.SupportsEcTelemetry;
        bool extendedEcSupported = machine.SupportsExtendedEcDiagnostics;
        EcFanReader? ecReader = ecSupported ? new EcFanReader(readProtection: machine.SupportsEcProtection) : null;
        string? error = null;
        bool complete = false;
        bool serviceComplete = false;
        bool ecComplete = false;
        int sampleCount = 0;
        if (options is null) error = discoveryFailure ?? "未能自动读取本机原版服务连接信息。";
        else
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(captureSeconds + 45));
            var transport = new GcuMqttTransport(Logger.WriteLine);
            transport.MessageReceived += (topic, payload) =>
            {
                // Only explicit hardware/status topics are retained.
                if (topic is not ("Fan/Status" or "System/FanInfo" or "System/CpuInfo" or "System/GpuInfo") &&
                    !topic.StartsWith("GPUDevice/", StringComparison.Ordinal) &&
                    !topic.StartsWith("Customize/", StringComparison.Ordinal) &&
                    !AdditionalTopics.Contains(topic, StringComparer.Ordinal)) return;
                try { using var json = JsonDocument.Parse(payload); frames[topic] = json.RootElement.Clone(); }
                catch (JsonException) { }
            };
            await using var controller = new GcuController(transport, readAcPower: MachineInfo.ReadAcPower);
            try
            {
                await controller.ConnectAsync(options, timeout.Token);
                await transport.SubscribeAsync(AdditionalTopics, timeout.Token);
                await RequestExtendedStatusAsync(transport, timeout.Token);
                await Task.Delay(TimeSpan.FromMilliseconds(350), timeout.Token);
                if (ecReader is not null && extendedEcSupported)
                    curves.Add(await ReadCurveSnapshotAsync("initial", ecReader, timeout.Token));

                var clock = Stopwatch.StartNew();
                do
                {
                    await RequestExtendedStatusAsync(transport, timeout.Token);
                    await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);

                    EcRegisterSnapshot? registers = null;
                    EcFanSnapshot? ecFan = null;
                    if (ecReader is not null)
                    {
                        registers = await ecReader.ReadRegistersAsync(
                            extendedEcSupported ? ecReader.ReadAddresses.Concat(CoreRegisterAddresses)
                                : ecReader.ReadAddresses,
                            timeout.Token);
                        ecFan = registers.LastError is null
                            ? EcFanDecoder.Decode(registers.Registers, registers.UpdatedAt ?? DateTimeOffset.UtcNow,
                                machine.SupportsEcProtection)
                            : new EcFanSnapshot
                            {
                                ErrorCode = registers.ErrorCode,
                                LastError = registers.LastError,
                                UpdatedAt = registers.UpdatedAt
                            };
                    }
                    GcuSnapshot snapshot = controller.Snapshot;
                    bool sampleServiceComplete = snapshot.IsStatusFresh() && snapshot.IsFanFresh();
                    bool sampleEcComplete = ecFan?.IsFresh() == true;
                    bool sampleComplete = sampleServiceComplete && (!ecSupported || sampleEcComplete);
                    complete |= sampleComplete;
                    serviceComplete |= sampleServiceComplete;
                    ecComplete |= sampleEcComplete;
                    sampleCount++;
                    samples.Add(new
                    {
                        Sequence = sampleCount,
                        CapturedAt = DateTimeOffset.Now,
                        ElapsedSeconds = Math.Round(clock.Elapsed.TotalSeconds, 3),
                        Complete = sampleComplete,
                        ServiceComplete = sampleServiceComplete,
                        EcComplete = ecSupported ? (bool?)sampleEcComplete : null,
                        Service = snapshot,
                        EcFan = ecFan,
                        DisplayRoute = GpuRouteReader.Read(),
                        EcRegisters = registers?.Registers.ToDictionary(
                            pair => $"0x{pair.Key:X4}",
                            pair => pair.Value),
                        Frames = SnapshotFrames(frames)
                    });

                    TimeSpan remaining = TimeSpan.FromSeconds(captureSeconds) - clock.Elapsed;
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining < TimeSpan.FromSeconds(3) ? remaining : TimeSpan.FromSeconds(3), timeout.Token);
                }
                while (clock.Elapsed < TimeSpan.FromSeconds(captureSeconds));

                if (ecReader is not null && extendedEcSupported)
                    curves.Add(await ReadCurveSnapshotAsync("final", ecReader, timeout.Token));
            }
            catch (Exception exception)
            {
                error = exception is OperationCanceledException ? "读取超时。" : "本机服务连接或状态读取失败。";
            }
        }
        var report = new
        {
            RecordedAt = DateTimeOffset.Now,
            ReadOnly = true,
            CaptureSeconds = captureSeconds,
            SampleIntervalSeconds = 3,
            Machine = machine,
            ConnectionDiscovered = options is not null,
            TelemetryScope = ecSupported ? "ServiceAndEc" : "ServiceOnly",
            EcTelemetrySupported = ecSupported,
            EcProtectionSupported = machine.SupportsEcProtection,
            ExtendedEcDiagnosticsSupported = extendedEcSupported,
            EcTelemetryStatus = ecSupported ? "使用已核对的硬件寄存器映射。" : "未识别到兼容的硬件寄存器映射；仅采集厂商服务状态，不读取转速与曲线寄存器。",
            CompleteTelemetryReceived = complete,
            CompleteServiceTelemetryReceived = serviceComplete,
            CompleteEcTelemetryReceived = ecSupported ? (bool?)ecComplete : null,
            Error = error,
            Samples = samples,
            Curves = curves,
            TelemetryFrames = frames
        };
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return complete && error is null && samples.Count > 0 ? 0 : 1;
    }

    private static async Task RequestExtendedStatusAsync(IGcuTransport transport, CancellationToken ct)
    {
        await transport.PublishAsync("Fan/Control", GetStatus, ct);
        await transport.PublishAsync("Fan/Control", "{\"Action\":\"GET_FAN_SPEED_CURVE_SETTING\"}", ct);
        await transport.PublishAsync("Setting/Control", GetStatus, ct);
        await transport.PublishAsync("Keyboard/Ctrl", GetStatus, ct);
        await transport.PublishAsync("BatteryProtection/Control", "{\"Report\":\"GET\"}", ct);
        await transport.PublishAsync("LCHWOC/Control", GetStatus, ct);
        await transport.PublishAsync("Customize/Control", "{\"Action\":\"GETSUPPORT\"}", ct);
        await transport.PublishAsync("Customize/SupportControl", "{\"Action\":\"GETSUPPORT\"}", ct);
    }

    private static async Task<object> ReadCurveSnapshotAsync(
        string phase,
        EcFanReader ecReader,
        CancellationToken ct)
    {
        EcRegisterSnapshot snapshot = await ecReader.ReadRegistersAsync(FanCurveAddresses, ct);
        return new
        {
            Phase = phase,
            CapturedAt = DateTimeOffset.Now,
            ErrorCode = snapshot.ErrorCode,
            LastError = snapshot.LastError,
            Registers = snapshot.Registers.ToDictionary(
                pair => $"0x{pair.Key:X4}",
                pair => pair.Value)
        };
    }

    private static Dictionary<string, JsonElement> SnapshotFrames(
        ConcurrentDictionary<string, JsonElement> frames) =>
        frames.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
}
