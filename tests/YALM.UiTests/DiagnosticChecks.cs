using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using YALM.Mechrevo;

namespace YALM.UiTests;

internal static partial class Program
{
    private static void CheckDiagnostics()
    {
        var assembly = typeof(GcuController).Assembly;
        Type discovery = assembly.GetType("YALM.Mechrevo.GcuConnectionDiscovery")!;
        Type machineType = assembly.GetType("YALM.Mechrevo.MachineInfo")!;
        object machine = Activator.CreateInstance(machineType, "MECHREVO", "Offline test", "", "GM6PX0X")!;
        MethodInfo find = discovery.GetMethods().Single(method => method.Name == "Find" && method.GetParameters().Length == 3);
        const string secret = "diagnostic-sentinel-do-not-log";
        string[] names = (from prefix in new[] { "YALM_GCU_", "MHELPER_GCU_" }
                          from field in new[] { "CLIENT_ID", "USERNAME", "PASSWORD", "PORT" }
                          select prefix + field).ToArray();
        var original = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var logs = new List<string>();
        try
        {
            foreach (string name in names) Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable("YALM_GCU_PORT", secret);
            object?[] args = [machine, null, new Action<string>(logs.Add)];
            Require(find.Invoke(null, args) is null && args[1]?.ToString()?.Contains("端口无效") == true,
                "invalid override port explains why no connection was attempted");
            Environment.SetEnvironmentVariable("YALM_GCU_PORT", null);
            Environment.SetEnvironmentVariable("YALM_GCU_CLIENT_ID", secret);
            args[1] = null;
            Require(find.Invoke(null, args) is null && args[1]?.ToString()?.Contains("不完整") == true,
                "incomplete override records failure without falling back to vendor credentials");
            Environment.SetEnvironmentVariable("YALM_GCU_USERNAME", secret);
            Environment.SetEnvironmentVariable("YALM_GCU_PASSWORD", secret);
            args[1] = null;
            Require(find.Invoke(null, args) is GcuConnectionOptions && args[1] is null,
                "complete override remains usable while only field presence is logged");
            Require(!string.Join("\n", logs).Contains(secret), "discovery never logs override values, including an invalid port");
        }
        finally
        {
            foreach (string name in names) Environment.SetEnvironmentVariable(name, original[name]);
        }

        MethodInfo read = discovery.GetMethod("ReadBridge", BindingFlags.Static | BindingFlags.NonPublic)!;
        object?[] bridgeArgs = [machine, assembly.Location, 13688, null, new Action<string>(logs.Add)];
        Require(read.Invoke(null, bridgeArgs) is null &&
            bridgeArgs[3]?.ToString()?.Contains("结构校验失败") == true &&
            bridgeArgs[3]?.ToString()?.Contains("未知版本") == true &&
            logs.Any(line => line.Contains("SHA256=") && line.Contains("已验证版本=False")) &&
            logs.Any(line => line.Contains("尝试通用静态解析")),
            "unverified component records a fingerprint, attempts generic metadata parsing, and explains a structural rejection");
        bridgeArgs[1] = Path.Combine(AppContext.BaseDirectory, "missing-vendor-component", "GCUBridge.exe");
        Require(read.Invoke(null, bridgeArgs) is null && bridgeArgs[3]?.ToString()?.Contains("不存在") == true,
            "missing component has a distinct diagnostic reason");

        Type diagnostics = assembly.GetType("YALM.Mechrevo.MechrevoDiagnostics")!;
        string summary = (string)diagnostics.GetMethod("ExceptionSummary")!.Invoke(null,
            [new InvalidOperationException(secret, new SocketException((int)SocketError.ConnectionRefused))])!;
        Require(summary.Contains("ConnectionRefused") && !summary.Contains(secret),
            "nested network errors retain their codes without exception text");
        diagnostics.GetMethod("WriteStartup")!.Invoke(null, [machine, 96]);
        string startup = File.ReadAllText(Logger.logFile);
        Require(startup.Contains("DPI=96") && startup.Contains("GCUService") && startup.Contains("进程架构="),
            "startup metadata and read-only service inspection reach the regular release log");
        CheckMqttDiagnostics(rejectConnection: true, secret);
        CheckMqttDiagnostics(rejectConnection: false, secret);
    }

    private static void CheckMqttDiagnostics(bool rejectConnection, string secret)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task broker = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using NetworkStream stream = peer.GetStream();
            var connect = await ReadMqttPacket(stream, timeout.Token);
            if (connect.Header != 0x10) throw new InvalidOperationException("Expected CONNECT packet.");
            await stream.WriteAsync(new byte[] { 0x20, 0x02, 0x00, rejectConnection ? (byte)0x05 : (byte)0x00 }, timeout.Token);
            if (rejectConnection) return;
            var subscribe = await ReadMqttPacket(stream, timeout.Token);
            if (subscribe.Header != 0x82) throw new InvalidOperationException("Expected SUBSCRIBE packet.");
            await stream.WriteAsync(new byte[] { 0x90, 0x03, subscribe.Body[0], subscribe.Body[1], 0x80 }, timeout.Token);
            // Keep the socket alive until the test disposes its client.
            await ReadMqttPacket(stream, timeout.Token);
        });
        var logs = new ConcurrentQueue<string>();
        Type transportType = typeof(GcuController).Assembly.GetType("YALM.Mechrevo.GcuMqttTransport")!;
        var transport = (IGcuTransport)Activator.CreateInstance(transportType, new Action<string>(logs.Enqueue))!;
        bool failed = false;
        try
        {
            transport.ConnectAsync(new GcuConnectionOptions(secret, secret, secret, port), timeout.Token).GetAwaiter().GetResult();
            if (!rejectConnection) transport.SubscribeAsync(["Fan/Status"], timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception) { failed = true; }
        finally { transport.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        broker.GetAwaiter().GetResult();
        string text = string.Join("\n", logs);
        Require(failed && text.Contains(rejectConnection ? "NotAuthorized" : "UnspecifiedError"),
            "local broker rejection records its numeric protocol outcome at the failing stage");
        Require(!text.Contains(secret), "MQTT diagnostics exclude all three credential values");
    }

    private static async Task<(byte Header, byte[] Body)> ReadMqttPacket(NetworkStream stream, CancellationToken ct)
    {
        byte[] single = new byte[1];
        await stream.ReadExactlyAsync(single, ct);
        byte header = single[0];
        int length = 0, multiplier = 1;
        for (int i = 0; i < 4; i++)
        {
            await stream.ReadExactlyAsync(single, ct);
            length += (single[0] & 127) * multiplier;
            if ((single[0] & 128) == 0)
            {
                if (length > 4096) throw new InvalidOperationException("Unexpected test packet size.");
                byte[] body = new byte[length];
                await stream.ReadExactlyAsync(body, ct);
                return (header, body);
            }
            multiplier *= 128;
        }
        throw new InvalidOperationException("Malformed test packet.");
    }
}
