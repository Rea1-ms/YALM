using Microsoft.Win32;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace YALM.Mechrevo;

/// <summary>Reads an audited vendor assembly as data; never loads or executes it.</summary>
internal static class GcuConnectionDiscovery
{
    // SHA-256 of the bridge in the user's original 5.17.49.6 package.
    // The method layout below is only accepted for this exact binary.
    private const string KnownBridgeHash = "29EF699B684FF93709205D4EA3B10630D623DBB076B81C9FE44024AE0DA9007F";
    private static readonly Dictionary<ushort, OpCode> Opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => unchecked((ushort)code.Value));

    public static GcuConnectionOptions? Find(MachineInfo machine)
    {
        string? id = GcuEnvironment.Get("CLIENT_ID");
        string? user = GcuEnvironment.Get("USERNAME");
        string? password = GcuEnvironment.Get("PASSWORD");
        string? portText = GcuEnvironment.Get("PORT");
        int port = 13688;
        if (portText is not null && (!int.TryParse(portText, out port) || port is < 1 or > 65535)) return null;
        // An explicit but incomplete override must not silently fall back.
        if (id is not null || user is not null || password is not null)
            return !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(user) && !string.IsNullOrEmpty(password)
                ? new(id, user, password, port) { TurboSupported = machine.TurboSupported } : null;

        try
        {
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\GCUBridge", false);
            string command = Environment.ExpandEnvironmentVariables(service?.GetValue("ImagePath")?.ToString() ?? "").Trim();
            int end = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (end < 0) return null;
            string path = command[..(end + 4)].Trim('"');
            if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFileName(path), "GCUBridge.exe", StringComparison.OrdinalIgnoreCase)) return null;
            using var stream = File.OpenRead(path);
            if (Convert.ToHexString(SHA256.HashData(stream)) != KnownBridgeHash) return null;
            stream.Position = 0;
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            string[] parts = Strings(pe, metadata, "MQTTService", "ClientManager", ".ctor").ToArray();
            string? prefix = Strings(pe, metadata, "MQTT_SERVER", "MQTTServer", "InitialzeClient")
                .FirstOrDefault(value => value == "UWPClient");
            if (prefix is null || parts.Length != 4 || parts[1] != parts[2]) return null;
            // Use a high slot; the vendor does not provide exclusive reservations.
            // The process mutex prevents collisions between this tool's local instances.
            const string slot = "19";
            return new(prefix + parts[0] + slot, prefix + parts[1] + slot, prefix + parts[3] + slot, port)
            { TurboSupported = machine.TurboSupported };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

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
