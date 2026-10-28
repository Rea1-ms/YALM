using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using YALM.Mechrevo;

namespace YALM.UiTests;

internal static partial class Program
{
    private static void CheckRemainingWorkers(string[] args)
    {
        CheckSharedMemoryConfiguration();
        const string option = "--remaining-worker-manifest";
        int index = Array.IndexOf(args, option);
        if (index < 0) return;
        Require(args.Count(arg => arg == option) == 1 && index + 1 < args.Length,
            "remaining worker fixtures require one manifest path");
        RemainingWorkerFixture[] fixtures = JsonSerializer.Deserialize<RemainingWorkerFixture[]>(
            File.ReadAllText(args[index + 1])) ?? [];
        Require(fixtures.Length > 0, "remaining worker fixture manifest is not empty");
        Assembly assembly = typeof(GcuController).Assembly;
        Type machineType = assembly.GetType("YALM.Mechrevo.MachineInfo")!;
        MethodInfo inspect = assembly.GetType("YALM.Mechrevo.GcuPlatformDiscovery")!
            .GetMethod("InspectAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        string cache = Path.GetFullPath(Path.Combine(".cache", "remaining-worker-checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(cache);
        foreach (RemainingWorkerFixture fixture in fixtures)
        {
            byte[] original = File.ReadAllBytes(fixture.Path);
            Require(Convert.ToHexString(SHA256.HashData(original)) == fixture.Hash,
                "remaining worker fixture is the reviewed original image");
            foreach (int? appType in new int?[] { null, 1, 2, 3 })
            {
                object previous = PlatformMachine(machineType, "MECHREVO", "Offline test", "Unknown board");
                machineType.GetProperty("OemAppType")!.SetValue(previous, appType);
                machineType.GetProperty("ProtocolProfile")!.SetValue(previous, "GCU-RamFan1.5");
                machineType.GetProperty("ProtocolWorkerHash")!.SetValue(previous, ReviewedPlatformWorker);
                int reads = 0;
                object result = InspectPlatform(inspect, previous, fixture.Path, (_, _) =>
                {
                    reads++;
                    throw new InvalidOperationException("Read-only worker identification must not query capability registers.");
                });
                Require(reads == 0 && PlatformFlag(result, "SupportsEcTelemetry") == fixture.ExpectedReadOnly,
                    "reviewed legacy RPM qualification does not borrow mode capability reads");
                Require(!PlatformFlag(result, "SupportsModeControl") && !PlatformFlag(result, "SupportsEcProtection") &&
                    !PlatformFlag(result, "SupportsCustomControl") && !PlatformFlag(result, "SupportsPeripheralControl") &&
                    !PlatformFlag(result, "SupportsExtendedEcDiagnostics"),
                    "old full qualifications are revoked and legacy RPM never enables protection or writes");
                Require((string?)machineType.GetProperty("ProtocolWorkerHash")!.GetValue(result) ==
                    (fixture.ExpectedReadOnly ? fixture.Hash : null),
                    "qualification records only the current reviewed original worker hash");
                if (!fixture.ExpectedReadOnly)
                    Require(PlatformWasRevoked(result), "different hardware read paths remain wholly unqualified");

                object otherBrand = PlatformMachine(machineType, "OTHER", "Offline test", "Unknown board");
                object denied = InspectPlatform(inspect, otherBrand, fixture.Path, (_, _) => throw new InvalidOperationException());
                Require(PlatformWasRevoked(denied), "legacy worker identity cannot bypass the manufacturer restriction");
            }

            if (fixture.RequiresH2Ram)
                CheckSharedMemoryWorker(machineType, inspect, fixture);

            string changed = Path.Combine(cache, fixture.Hash + ".exe");
            File.WriteAllBytes(changed, [.. original, 0x1f]);
            object oldReadOnly = PlatformMachine(machineType, "MECHREVO", "Offline test", "Unknown board");
            machineType.GetProperty("ProtocolProfile")!.SetValue(oldReadOnly, "GCU-Legacy-ReadOnly");
            machineType.GetProperty("ProtocolWorkerHash")!.SetValue(oldReadOnly, fixture.Hash);
            object revoked = InspectPlatform(inspect, oldReadOnly, changed, (_, _) => throw new InvalidOperationException());
            Require(PlatformWasRevoked(revoked), "a changed worker revokes prior RPM qualification before any hardware query");
        }
    }

    private static void CheckSharedMemoryConfiguration()
    {
        Type machineType = typeof(GcuController).Assembly.GetType("YALM.Mechrevo.MachineInfo")!;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        MethodInfo classify = machineType.GetMethod("ClassifyOemH2RamValue", statics)!;
        MethodInfo failure = machineType.GetMethod("ClassifyOemH2RamFailure", statics)!;
        const string sensitive = "synthetic-sensitive-registry-marker";
        foreach (var sample in new (bool Found, object? Value, bool? Supported, string Status)[]
        {
            (false, sensitive, null, "MissingKey"), (true, null, null, "MissingValue"),
            (true, sensitive, null, "InvalidType"), (true, "1", null, "InvalidType"),
            (true, 1L, null, "InvalidType"), (true, true, null, "InvalidType"),
            (true, -1, null, "InvalidValue"), (true, 2, null, "InvalidValue"),
            (true, 0, false, "Disabled"), (true, 1, true, "Supported")
        })
        {
            object observation = classify.Invoke(null, [sample.Found, sample.Value])!;
            Type observationType = observation.GetType();
            Require((bool?)observationType.GetProperty("Supported")!.GetValue(observation) == sample.Supported &&
                observationType.GetProperty("Status")!.GetValue(observation)!.ToString() == sample.Status,
                "shared-memory configuration requires an explicit DWORD 0 or 1 and classifies missing or invalid input");
            Require(!observation.ToString()!.Contains(sensitive), "shared-memory configuration never stores raw registry text");
        }
        foreach (Exception exception in new Exception[]
        {
            new UnauthorizedAccessException(sensitive), new System.Security.SecurityException(sensitive),
            new IOException(sensitive), new ArgumentException(sensitive)
        })
        {
            object observation = failure.Invoke(null, [exception])!;
            string status = observation.GetType().GetProperty("Status")!.GetValue(observation)!.ToString()!;
            Require(status == (exception is UnauthorizedAccessException or System.Security.SecurityException ? "AccessDenied" : "ReadFailed") &&
                !observation.ToString()!.Contains(sensitive), "shared-memory configuration failures retain classification without exception text");
        }
    }

    private static void CheckSharedMemoryWorker(Type machineType, MethodInfo inspect, RemainingWorkerFixture fixture)
    {
        object machine = PlatformMachine(machineType, "MECHREVO", "Offline test", "Unknown board");
        machineType.GetProperty("OemH2RamSupported")!.SetValue(machine, true);
        object? accepted = null;
        for (int project = byte.MinValue; project <= byte.MaxValue; project++)
        {
            int reads = 0;
            object result = InspectPlatform(inspect, machine, fixture.Path, (addresses, _) =>
            {
                reads++;
                Require(addresses.SequenceEqual(new[] { PlatformProjectIdAddress }),
                    "legacy shared-memory probe reads only the reviewed raw project address");
                return Task.FromResult(new EcRegisterSnapshot
                {
                    Registers = new Dictionary<int, byte> { [PlatformProjectIdAddress] = (byte)project }
                });
            });
            bool expected = project is >= 1 and <= 16;
            Require(reads == 1 && PlatformFlag(result, "SupportsEcTelemetry") == expected,
                "shared-memory RPM qualification accepts only reviewed project IDs 1 through 16");
            Require(!PlatformFlag(result, "SupportsModeControl") && !PlatformFlag(result, "SupportsEcProtection") &&
                !PlatformFlag(result, "SupportsCustomControl") && !PlatformFlag(result, "SupportsPeripheralControl"),
                "a shared-memory RPM match never grants write or protection qualifications");
            if (expected) accepted = result;
            else Require(PlatformWasRevoked(result), "unknown shared-memory projects do not retain old qualifications");
        }
        Require(accepted is not null, "shared-memory fixture produces a reviewed read-only result");

        foreach (bool? support in new bool?[] { null, false })
        {
            machineType.GetProperty("OemH2RamSupported")!.SetValue(accepted, support);
            object revoked = InspectPlatform(inspect, accepted!, fixture.Path, (_, _) => throw new InvalidOperationException());
            Require(PlatformWasRevoked(revoked), "removing explicit shared-memory support revokes the previous RPM grant without querying hardware");
        }
        machineType.GetProperty("OemH2RamSupported")!.SetValue(accepted, true);
        const string sensitive = "synthetic-sensitive-shared-memory-marker";
        foreach (EcRegisterSnapshot bad in new[]
        {
            new EcRegisterSnapshot(),
            new EcRegisterSnapshot { ErrorCode = 5 },
            new EcRegisterSnapshot { LastError = sensitive },
            new EcRegisterSnapshot { Registers = new Dictionary<int, byte> { [PlatformProjectIdAddress] = 1 }, ErrorCode = 5 }
        })
        {
            var logs = new List<string>();
            object rejected = InspectPlatform(inspect, accepted!, fixture.Path, (_, _) => Task.FromResult(bad), logs.Add);
            Require(PlatformWasRevoked(rejected) && !string.Join("\n", logs).Contains(sensitive),
                "failed or incomplete shared-memory reads revoke old grants and do not expose raw error text");
        }
        var exceptionLogs = new List<string>();
        object failed = InspectPlatform(inspect, accepted!, fixture.Path,
            (_, _) => throw new IOException(sensitive), exceptionLogs.Add);
        Require(PlatformWasRevoked(failed) && !string.Join("\n", exceptionLogs).Contains(sensitive) &&
            exceptionLogs.Any(line => line.Contains("项目号读取失败")),
            "shared-memory read exceptions retain a useful stage without exposing exception text");
    }

    private sealed record RemainingWorkerFixture(string Path, string Hash, bool ExpectedReadOnly, bool RequiresH2Ram = false);
}
