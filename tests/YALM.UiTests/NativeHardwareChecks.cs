using System.Reflection;
using YALM.Mechrevo;

namespace YALM.UiTests;

internal static partial class Program
{
    private static readonly Type NativeReaderType = typeof(GcuController).Assembly.GetType("YALM.Mechrevo.NativeHardwareReader")!;

    private static void CheckNativeHardware() => Wait(CheckNativeHardwareAsync());

    private static async Task CheckNativeHardwareAsync()
    {
        const string secret = "SENSITIVE_NATIVE_TEST_MARKER";
        NativeHardwareEndpoint jiaolong = new(NativeHardwareFamily.Jiaolong, JiaolongReadProtocol.InstancePath,
            JiaolongReadProtocol.LegacyImageHash, NativeHardwareSchemaStatus.Matched);
        var log = new List<string>();
        var backend = new NativeReadFake([jiaolong]);
        Require(await NativeInspect(false, backend, log) is null && backend.Inspections == 0 && backend.Readings.Count == 0,
            "native discovery rejects another manufacturer before metadata or firmware access");

        foreach (NativeHardwareSchemaStatus status in Enum.GetValues<NativeHardwareSchemaStatus>().Where(value => value != NativeHardwareSchemaStatus.Matched))
        {
            backend = new([jiaolong with { SchemaStatus = status }]);
            Require(await NativeInspect(true, backend, log) is null && backend.Readings.Count == 0,
                "unmatched native metadata never calls firmware methods");
        }
        foreach (NativeHardwareEndpoint invalid in new[]
        {
            jiaolong with { ImageHash = secret },
            jiaolong with { InstancePath = secret },
            jiaolong with { InstancePath = @"\\remote\root\WMI:MICommonInterface.InstanceName='ACPI\PNP0C14\MIFS_0'" },
            jiaolong with { Family = (NativeHardwareFamily)255 }
        })
        {
            backend = new([invalid]);
            Require(await NativeInspect(true, backend, log) is null && backend.Readings.Count == 0,
                "unreviewed images, remote paths and mismatched instances do not authorize reads");
        }
        backend = new([jiaolong, jiaolong]);
        Require(await NativeInspect(true, backend, log) is null && backend.Readings.Count == 0,
            "ambiguous matching native interfaces are not selected by order");
        backend = new([jiaolong]) { InspectionError = new IOException(secret) };
        Require(await NativeInspect(true, backend, log) is null, "metadata failures leave native discovery unavailable");
        Require(!string.Join("\n", log).Contains(secret, StringComparison.Ordinal),
            "native diagnostic reasons exclude arbitrary paths, hashes and exception messages");

        MethodInfo componentPath = NativeReaderType.GetMethod("ReviewedComponentPath", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (int state in new[] { -7, -6, -5, -4, -3, -2, -1, 0, 1, 2, 4, 5 })
            Require(componentPath.Invoke(null, [state, @"C:\Program Files\ControlCenter\ControlCenter.exe"]) is null,
                "only locally installed MSI components are accepted, not advertised components or source media");
        foreach (string? invalid in new[] { null, "", "ControlCenter.exe", @"\\server\share\ControlCenter.exe", @"C:\installer-icon.exe" })
            Require(componentPath.Invoke(null, [3, invalid]) is null, "MSI component paths require the expected local main executable");
        Require(Equals(componentPath.Invoke(null, [3, @"C:\Program Files\ControlCenter\ControlCenter.exe"]),
            @"C:\Program Files\ControlCenter\ControlCenter.exe"), "the reviewed MSI component resolves its main executable without invoking repair");

        backend = new([jiaolong]) { Response = (_, reading) => reading == NativeHardwareReading.Mode ? NativeBytes(0) : NativeBytes(0xd0, 0x07, 0x34, 0x08) };
        object reader = (await NativeInspect(true, backend, log))!;
        Require(reader is not null && backend.Readings.Count == 0, "discovery itself does not read native hardware");
        GcuSnapshot first = await NativeRead(reader!);
        Require(first.Mode == 1 && first.CpuFanRpm == 2000 && first.GpuFanRpm == 2100 && NativeComplete(reader!, first),
            "legacy Jiaolong reads mode and two actual RPM values without requiring unknown power state");
        Require(first.IsAc is null && first.TurboSupported is null && first.FanTable is null && first.Keyboard is null &&
            !first.CanSwitchMode(PerformanceMode.Balanced), "native telemetry does not synthesize control eligibility");
        Require(backend.Readings.SequenceEqual(new[] { NativeHardwareReading.Mode, NativeHardwareReading.FanSpeeds }),
            "legacy Jiaolong does not issue the later temperature request");
        Require(!NativeComplete(reader!, first, DateTimeOffset.UtcNow.AddSeconds(20)), "expired native data is incomplete");
        Require(!NativeComplete(reader!, first with { IsConnected = false }), "disconnected native data is incomplete");
        backend.Response = (_, _) => null;
        GcuSnapshot missing = await NativeRead(reader!);
        Require(!missing.IsConnected && missing.Mode is null && missing.CpuFanRpm is null && missing.GpuFanRpm is null &&
            missing.ModeUpdatedAt is null && !NativeComplete(reader!, missing), "failed polls cannot reuse a previous native snapshot");

        NativeHardwareEndpoint pro = jiaolong with { ImageHash = JiaolongReadProtocol.ProImageHash };
        backend = new([pro])
        {
            Response = (_, reading) => reading switch
            {
                NativeHardwareReading.Mode => NativeBytes(2),
                NativeHardwareReading.FanSpeeds => NativeBytes(0xd0, 0x07, 0x34, 0x08),
                NativeHardwareReading.CpuTemperature => NativeBytes(65),
                _ => throw new Exception("unexpected reading")
            }
        };
        reader = (await NativeInspect(true, backend, log))!;
        first = await NativeRead(reader);
        Require(first.Mode == 0 && first.CpuTemperature == 65 && NativeComplete(reader, first),
            "reviewed Jiaolong Pro adds its independently decoded CPU temperature");
        backend.Response = (_, reading) => reading == NativeHardwareReading.Mode ? NativeBytes(0) : throw new IOException(secret);
        GcuSnapshot partial = await NativeRead(reader);
        Require(partial.Mode == 1 && partial.CpuTemperature is null && partial.CpuFanRpm is null && partial.LastError is not null &&
            !NativeComplete(reader, partial), "a native read failure preserves only successful fields from the same poll");
        Require(!string.Join("\n", log).Contains(secret, StringComparison.Ordinal), "read failure diagnostics do not leak exception messages");

        NativeHardwareEndpoint yilong = new(NativeHardwareFamily.Yilong, "ACPIMethod.InstanceName=\"offline\"",
            "EF6D6C454563CE39517AF000A48EB32080CE8F00272981A27A706DCF1F3C5760", NativeHardwareSchemaStatus.Matched);
        backend = new([yilong]) { Response = (_, _) => 2u };
        reader = (await NativeInspect(true, backend, log))!;
        first = await NativeRead(reader);
        Require(first.Mode == 2 && NativeComplete(reader, first) && backend.Readings.SequenceEqual(new[] { NativeHardwareReading.Mode }),
            "Yilong without a confirmed Nvidia GPU queries only the reviewed mode register");
        backend = new([yilong with { GpuTemperatureSupported = true }])
        { Response = (_, reading) => reading == NativeHardwareReading.Mode ? 1u : 58u };
        reader = (await NativeInspect(true, backend, log))!;
        first = await NativeRead(reader);
        Require(first.Mode == 1 && first.GpuTemperature == 58 && first.CpuFanRpm is null && first.GpuFanRpm is null && NativeComplete(reader, first),
            "Yilong direct reads do not fabricate fan telemetry from its unsupported mailbox sequence");
        backend.Response = (_, _) => "1";
        Require(!(await NativeRead(reader)).IsConnected, "numeric strings are not accepted as native UInt32 responses");

        NativeHardwareEndpoint imini = new(NativeHardwareFamily.Imini,
            @"PowerSwitchInterface.InstanceName='ACPI\PNP0C14\IP3POWERSWITCH_0'",
            "C15B7057C88B2DBBF304F5DCA79DA276AD909451C709C7CA9493A525049F040D", NativeHardwareSchemaStatus.Matched);
        backend = new([imini])
        {
            Response = (_, reading) => reading switch
            {
                NativeHardwareReading.Mode => 0u,
                NativeHardwareReading.FanCount => 2u,
                NativeHardwareReading.GpuSupport => 1u,
                NativeHardwareReading.FanSpeeds => 2000u | 2100u << 16,
                _ => throw new Exception("unexpected reading")
            }
        };
        reader = (await NativeInspect(true, backend, log))!;
        first = await NativeRead(reader);
        Require(first.Mode == 1 && first.NumberedFanCount == 2 && first.Fan1Rpm == 2000 && first.Fan2Rpm == 2100 &&
            first.CpuFanRpm is null && first.GpuFanRpm is null && NativeComplete(reader, first),
            "imini preserves generic fan identities and evaluates completeness against this poll's capabilities");
        Require((bool)NativeReaderType.GetProperty("UsesNumberedFans")!.GetValue(reader)! && backend.Readings.Count == 4,
            "imini optional temperature is not called unless its separate schema was validated");
        backend.Response = (_, reading) => reading switch
        {
            NativeHardwareReading.Mode => 0u,
            NativeHardwareReading.FanCount => 1u,
            NativeHardwareReading.GpuSupport => 0u,
            NativeHardwareReading.FanSpeeds => 2000u,
            _ => null
        };
        first = await NativeRead(reader);
        Require(first.NumberedFanCount == 1 && first.Fan2Rpm is null && NativeComplete(reader, first),
            "one-fan imini does not inherit a second fan or require an absent fan");
        backend.Response = (_, reading) => reading switch
        {
            NativeHardwareReading.Mode => 0u,
            NativeHardwareReading.FanSpeeds => 2000u,
            _ => null
        };
        Require(!NativeComplete(reader, await NativeRead(reader)), "missing imini capabilities cannot reuse previous fan counts");

        backend = new([imini with { CpuTemperatureSupported = true }])
        {
            Response = (_, reading) => reading switch
            {
                NativeHardwareReading.Mode => 0u,
                NativeHardwareReading.FanCount => 1u,
                NativeHardwareReading.GpuSupport => 0u,
                NativeHardwareReading.FanSpeeds => 2000u,
                NativeHardwareReading.CpuTemperature => 62u,
                _ => null
            }
        };
        reader = (await NativeInspect(true, backend, log))!;
        first = await NativeRead(reader);
        Require(first.CpuTemperature == 62 && NativeComplete(reader, first) && backend.Readings.Count == 5,
            "imini with a reviewed temperature method includes it in its selected telemetry scope");
        Require(!NativeComplete(reader, first with { CpuTemperature = null, CpuTemperatureUpdatedAt = null }),
            "a missing advertised imini temperature keeps that selected scope incomplete");

        backend = new([jiaolong]) { Response = (_, _) => NativeBytes(0), DelayMilliseconds = 20 };
        reader = (await NativeInspect(true, backend, log))!;
        await Task.WhenAll(NativeRead(reader), NativeRead(reader));
        Require(backend.MaxConcurrentReads == 1, "concurrent polls serialize native calls");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        int before = backend.Readings.Count;
        try { await NativeRead(reader, cancelled.Token); Require(false, "cancelled polling must not complete"); }
        catch (OperationCanceledException) { Require(backend.Readings.Count == before, "cancelled native polling does not issue a query"); }
    }

    private static byte[] NativeBytes(params byte[] values)
    {
        byte[] result = new byte[32];
        values.CopyTo(result, 4);
        return result;
    }

    private static async Task<object?> NativeInspect(bool manufacturerMatches, NativeReadFake backend, List<string> log)
    {
        Task task = (Task)NativeReaderType.GetMethod("InspectAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [manufacturerMatches, backend, (Action<string>)log.Add, CancellationToken.None])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    private static Task<GcuSnapshot> NativeRead(object reader, CancellationToken ct = default) =>
        (Task<GcuSnapshot>)NativeReaderType.GetMethod("ReadAsync")!.Invoke(reader, [ct])!;

    private static bool NativeComplete(object reader, GcuSnapshot snapshot, DateTimeOffset? now = null) =>
        (bool)NativeReaderType.GetMethod("IsComplete")!.Invoke(reader, [snapshot, now])!;

    private sealed class NativeReadFake(IReadOnlyList<NativeHardwareEndpoint> endpoints) : INativeHardwareReadBackend
    {
        public int Inspections;
        public Exception? InspectionError;
        public readonly List<NativeHardwareReading> Readings = [];
        public Func<NativeHardwareEndpoint, NativeHardwareReading, object?> Response = (_, _) => null;
        public int DelayMilliseconds;
        public int MaxConcurrentReads;
        private int _active;

        public IReadOnlyList<NativeHardwareEndpoint> Inspect(CancellationToken ct)
        {
            Inspections++;
            if (InspectionError is not null) throw InspectionError;
            return endpoints;
        }

        public object? Read(NativeHardwareEndpoint endpoint, NativeHardwareReading reading, CancellationToken ct)
        {
            int active = Interlocked.Increment(ref _active);
            MaxConcurrentReads = Math.Max(MaxConcurrentReads, active);
            try
            {
                lock (Readings) Readings.Add(reading);
                if (DelayMilliseconds > 0) Thread.Sleep(DelayMilliseconds);
                return Response(endpoint, reading);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
