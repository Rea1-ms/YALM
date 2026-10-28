using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using YALM.Mechrevo;

namespace YALM.UiTests;

internal static partial class Program
{
    // Vendor binaries remain local and ignored. A manifest supplies paths and
    // expected fingerprints, never credentials or executable assembly references.
    private static void CheckBridgePackages(string[] args)
    {
        int index = Array.IndexOf(args, "--bridge-manifest");
        if (index < 0) return; // Ordinary diagnostics already cover unknown and missing files.

        string? retainedDirectory = null;
        try
        {
            BridgeRequire(index + 1 < args.Length && args.Count(arg => arg == "--bridge-manifest") == 1,
                "The bridge manifest option requires one absolute path.");
            string manifestPath = args[index + 1];
            BridgeRequire(Path.IsPathFullyQualified(manifestPath), "The bridge manifest path must be absolute.");
            BridgeFixture[]? fixtures = JsonSerializer.Deserialize<BridgeFixture[]>(File.ReadAllText(manifestPath));
            BridgeRequire(fixtures is { Length: > 0 }, "The bridge manifest must contain at least one component.");

            Assembly assembly = typeof(GcuController).Assembly;
            Type discovery = assembly.GetType("YALM.Mechrevo.GcuConnectionDiscovery")!;
            Type imageReader = assembly.GetType("YALM.Mechrevo.GcuBridgeImage")!;
            Type machineType = assembly.GetType("YALM.Mechrevo.MachineInfo")!;
            MethodInfo read = discovery.GetMethod("ReadBridge", BindingFlags.Static | BindingFlags.NonPublic)!;
            const BindingFlags staticMethods = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo isProtected = imageReader.GetMethod("IsProtected", staticMethods)!;
            MethodInfo readDecrypted = imageReader.GetMethod("ReadDecrypted", staticMethods)!;
            object machine = Activator.CreateInstance(machineType, "MECHREVO", "Offline test", "", "GM6PX0X")!;
            PropertyInfo turboProperty = machineType.GetProperty("TurboSupported")!;
            var acceptedCredentials = new Dictionary<string, GcuConnectionOptions>(StringComparer.OrdinalIgnoreCase);
            int supported = 0;
            int rejected = 0;
            int recovered = 0;

            foreach (BridgeFixture fixture in fixtures!)
            {
                BridgeRequire(!string.IsNullOrWhiteSpace(fixture.Path) && Path.IsPathFullyQualified(fixture.Path),
                    "Every bridge component path must be absolute.");
                BridgeRequire(fixture.Hash is { Length: 64 } && fixture.Hash.All(Uri.IsHexDigit),
                    "Every bridge fingerprint must be a SHA-256 value.");
                BridgeRequire(fixture.CredentialsMatchHash is null ||
                    (fixture.CredentialsMatchHash.Length == 64 && fixture.CredentialsMatchHash.All(Uri.IsHexDigit)),
                    "Every optional credential comparison fingerprint must be a SHA-256 value.");
                string hash;
                using (FileStream source = File.OpenRead(fixture.Path))
                    hash = Convert.ToHexString(SHA256.HashData(source));
                BridgeRequire(string.Equals(hash, fixture.Hash, StringComparison.OrdinalIgnoreCase),
                    "The bridge component differs from the manifest fingerprint.");
                if ((bool)isProtected.Invoke(null, [hash])!)
                {
                    CheckProtectedBridgeImage(fixture.Path, hash, isProtected, readDecrypted);
                    recovered++;
                }

                var logs = new List<string>();
                const int testPort = 14739;
                GcuConnectionOptions? first = null;
                foreach (bool? turbo in new bool?[] { true, false, null })
                {
                    turboProperty.SetValue(machine, turbo);
                    object?[] invokeArgs = [machine, fixture.Path, testPort, null, new Action<string>(logs.Add)];
                    var result = (GcuConnectionOptions?)read.Invoke(null, invokeArgs);
                    BridgeRequire((result is not null) == fixture.ExpectedSupported,
                        $"Bridge {hash}: discovery does not match the audited expectation.");
                    if (!fixture.ExpectedSupported)
                    {
                        BridgeRequire(invokeArgs[3] is string failure && !string.IsNullOrWhiteSpace(failure),
                            $"Bridge {hash}: an unsupported component must explain its rejection.");
                        continue;
                    }

                    BridgeRequire(invokeArgs[3] is null, $"Bridge {hash}: successful discovery must not retain a failure.");
                    BridgeRequire(result!.Port == testPort && result.TurboSupported == turbo,
                        $"Bridge {hash}: discovery must preserve the requested port and capability.");
                    BridgeRequire(!string.IsNullOrWhiteSpace(result.ClientId) &&
                        !string.IsNullOrWhiteSpace(result.UserName) && !string.IsNullOrEmpty(result.Password),
                        $"Bridge {hash}: discovery must produce all three authentication fields.");
                    if (first is not null)
                        BridgeRequire(result.ClientId == first.ClientId && result.UserName == first.UserName && result.Password == first.Password,
                            $"Bridge {hash}: capability changes must not alter the authentication slot.");
                    first ??= result;
                    CheckBridgeLogPrivacy(logs, result, hash);
                }

                if (fixture.ExpectedSupported)
                {
                    acceptedCredentials[hash] = first!;
                    retainedDirectory ??= Path.GetFullPath(Path.Combine(".cache", "bridge-package-checks", Guid.NewGuid().ToString("N")));
                    Directory.CreateDirectory(retainedDirectory);
                    string modifiedPath = Path.Combine(retainedDirectory, $"{supported:D2}-{hash}.exe");
                    using (FileStream source = File.OpenRead(fixture.Path))
                    using (var modified = new FileStream(modifiedPath, FileMode.CreateNew, FileAccess.Write))
                    {
                        source.CopyTo(modified);
                        modified.WriteByte(0xA5);
                    }
                    logs.Clear();
                    object?[] modifiedArgs = [machine, modifiedPath, testPort, null, new Action<string>(logs.Add)];
                    var modifiedResult = (GcuConnectionOptions?)read.Invoke(null, modifiedArgs);
                    BridgeRequire(modifiedResult is not null || modifiedArgs[3] is string,
                        $"Bridge {hash}: a modified component must either pass the generic parser or explain its rejection.");
                    if (modifiedResult is not null)
                    {
                        BridgeRequire(modifiedResult.BridgeVersionKnown == false &&
                            !modifiedResult.ModeControlSupported && !modifiedResult.CustomControlSupported &&
                            !modifiedResult.PeripheralControlSupported &&
                            logs.Any(line => line.Contains("通用静态解析", StringComparison.Ordinal)),
                            $"Bridge {hash}: an unknown component that parses generically remains read-only.");
                    }
                    CheckBridgeLogPrivacy(logs, first!, hash);
                    supported++;
                }
                else rejected++;
            }

            retainedDirectory ??= Path.GetFullPath(Path.Combine(".cache", "bridge-package-checks", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(retainedDirectory);
            foreach (long size in new long[] { 0, 16L * 1024 * 1024 + 1 })
            {
                string invalidSizePath = Path.Combine(retainedDirectory, $"invalid-length-{size}.exe");
                using (var file = new FileStream(invalidSizePath, FileMode.CreateNew, FileAccess.Write))
                    file.SetLength(size);
                var sizeLogs = new List<string>();
                object?[] sizeArgs = [machine, invalidSizePath, 14739, null, new Action<string>(sizeLogs.Add)];
                BridgeRequire(read.Invoke(null, sizeArgs) is null &&
                    sizeArgs[3] is string failure && failure.Contains("文件大小异常", StringComparison.Ordinal),
                    $"Bridge length {size}: an abnormal file size must be rejected explicitly.");
                BridgeRequire(!sizeLogs.Any(line => line.Contains("SHA256=", StringComparison.Ordinal)),
                    $"Bridge length {size}: the size check must reject the image before fingerprinting.");
            }

            int comparisons = 0;
            foreach (BridgeFixture fixture in fixtures!.Where(item => item.CredentialsMatchHash is not null))
            {
                BridgeRequire(acceptedCredentials.TryGetValue(fixture.Hash, out GcuConnectionOptions? actual) &&
                    acceptedCredentials.TryGetValue(fixture.CredentialsMatchHash!, out _),
                    "Credential comparisons require two accepted components in the manifest.");
                GcuConnectionOptions expected = acceptedCredentials[fixture.CredentialsMatchHash!];
                BridgeRequire(actual!.ClientId == expected.ClientId && actual.UserName == expected.UserName && actual.Password == expected.Password,
                    $"Bridge {fixture.Hash}: authentication differs from the audited comparison component.");
                comparisons++;
            }

            Console.WriteLine($"PASS: bridge components accepted={supported}, rejected={rejected}, modified components checked={supported}, abnormal sizes rejected=2, protected images recovered={recovered}, credential comparisons={comparisons}");
        }
        catch (BridgeCheckFailure) { throw; }
        catch (Exception error)
        {
            // Reflection or malformed metadata must not cause arbitrary vendor
            // strings to escape through the test runner's exception printing.
            throw new BridgeCheckFailure($"Bridge package checks failed: {error.GetType().Name} (0x{error.HResult:X8}).");
        }
        finally
        {
            if (retainedDirectory is not null)
                Console.WriteLine($"Bridge mutation fixtures retained: {retainedDirectory}");
        }
    }

    private static void CheckProtectedBridgeImage(string path, string hash, MethodInfo isProtected, MethodInfo readDecrypted)
    {
        byte[] original = File.ReadAllBytes(path);
        byte[] untouched = (byte[])original.Clone();
        var recovered = (byte[]?)readDecrypted.Invoke(null, [hash, original]);
        BridgeRequire(recovered is { Length: > 0 } && !ReferenceEquals(recovered, original),
            $"Bridge {hash}: static recovery must return a new image.");
        BridgeRequire(original.AsSpan().SequenceEqual(untouched),
            $"Bridge {hash}: static recovery must leave the original image unchanged.");

        byte[] modified = (byte[])original.Clone();
        modified[^1] ^= 0x01;
        byte[] modifiedBefore = (byte[])modified.Clone();
        BridgeRequire(readDecrypted.Invoke(null, [hash, modified]) is null && modified.AsSpan().SequenceEqual(modifiedBefore),
            $"Bridge {hash}: static recovery must reject modified bytes without changing its input.");

        byte[] truncated = original[..(original.Length / 2)];
        byte[] truncatedBefore = (byte[])truncated.Clone();
        BridgeRequire(readDecrypted.Invoke(null, [hash, truncated]) is null && truncated.AsSpan().SequenceEqual(truncatedBefore),
            $"Bridge {hash}: static recovery must reject truncated bytes without changing its input.");

        string unknownHash = new('0', 64);
        BridgeRequire(!(bool)isProtected.Invoke(null, [unknownHash])! &&
            readDecrypted.Invoke(null, [unknownHash, original]) is null && original.AsSpan().SequenceEqual(untouched),
            $"Bridge {hash}: static recovery must reject an unknown fingerprint without changing its input.");
    }

    private static void CheckBridgeLogPrivacy(IEnumerable<string> logs, GcuConnectionOptions options, string hash)
    {
        string text = string.Join("\n", logs);
        BridgeRequire(!text.Contains(options.ClientId, StringComparison.Ordinal) &&
            !text.Contains(options.UserName, StringComparison.Ordinal) &&
            !text.Contains(options.Password, StringComparison.Ordinal),
            $"Bridge {hash}: diagnostics must not contain authentication values.");
    }

    private static void BridgeRequire(bool condition, string message)
    {
        if (!condition) throw new BridgeCheckFailure(message);
        _checks++;
    }

    private sealed class BridgeCheckFailure(string message) : Exception(message);

    private sealed record BridgeFixture
    {
        public required string Path { get; init; }
        public required string Hash { get; init; }
        public required bool ExpectedSupported { get; init; }
        public string? CredentialsMatchHash { get; init; }
    }
}
