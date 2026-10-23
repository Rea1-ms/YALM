using System.Text.Json;

namespace YALM.Mechrevo;

/// <summary>A bounded, read-only connection check. Never invokes SetModeAsync.</summary>
internal static class MechrevoProbe
{
    public static async Task<int> RunAsync(string outputPath)
    {
        if (!Path.IsPathFullyQualified(outputPath)) return 2;
        MachineInfo machine = MachineInfo.Read();
        GcuConnectionOptions? options = GcuConnectionDiscovery.Find(machine);
        var observations = new List<GcuSnapshot>();
        var ecObservations = new List<EcFanSnapshot>();
        var frames = new System.Collections.Concurrent.ConcurrentDictionary<string, JsonElement>();
        var ecReader = new EcFanReader();
        string? error = null;
        bool complete = false;
        if (options is null) error = "未能自动读取本机原版服务连接信息。";
        else
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var transport = new GcuMqttTransport();
            transport.MessageReceived += (topic, payload) =>
            {
                // Only hardware telemetry, never authentication or general service topics.
                if (topic is not ("Fan/Status" or "System/FanInfo" or "System/CpuInfo" or "System/GpuInfo")) return;
                try { using var json = JsonDocument.Parse(payload); frames[topic] = json.RootElement.Clone(); }
                catch (JsonException) { }
            };
            await using var controller = new GcuController(transport, readAcPower: MachineInfo.ReadAcPower);
            try
            {
                await controller.ConnectAsync(options, timeout.Token);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), timeout.Token);
                    var snapshot = controller.Snapshot;
                    observations.Add(snapshot);
                    var ecSnapshot = await ecReader.ReadAsync(timeout.Token);
                    ecObservations.Add(ecSnapshot);
                    complete |= snapshot.IsStatusFresh() && snapshot.IsFanFresh() && ecSnapshot.IsFresh();
                    if (attempt < 2) await controller.RefreshAsync(timeout.Token);
                }
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
            Machine = machine,
            ConnectionDiscovered = options is not null,
            CompleteTelemetryReceived = complete,
            Error = error,
            Observations = observations,
            EcObservations = ecObservations,
            TelemetryFrames = frames
        };
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return complete && error is null ? 0 : 1;
    }
}
