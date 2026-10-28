using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using System.Buffers;
using System.Text;

namespace YALM.Mechrevo;

/// <summary>
/// The small MQTT adapter used by the panel. It deliberately has no built-in
/// username or password; the installed vendor service supplies those values.
/// </summary>
internal sealed class GcuMqttTransport : IGcuTransport
{
    private readonly MqttClientFactory _factory = new();
    private IMqttClient? _client;
    private int _disconnecting;
    private readonly Action<string>? _log;

    public GcuMqttTransport(Action<string>? log = null) => _log = log;

    public bool IsConnected => _client?.IsConnected == true;
    public event Action<string, string>? MessageReceived;
    public event Action? Disconnected;

    public async Task ConnectAsync(GcuConnectionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ClientId)) throw new ArgumentException("客户端标识不能为空。", nameof(options));
        if (string.IsNullOrWhiteSpace(options.UserName)) throw new ArgumentException("用户名不能为空。", nameof(options));
        if (string.IsNullOrEmpty(options.Password)) throw new ArgumentException("密码不能为空。", nameof(options));
        if (options.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(options));

        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        IMqttClient client = _factory.CreateMqttClient();
        _client = client;
        client.ApplicationMessageReceivedAsync += args =>
        {
            // Retained values may describe an earlier client session and cannot
            // confirm the current hardware state.
            if (ReferenceEquals(_client, client) && !args.ApplicationMessage.Retain && args.ApplicationMessage.Payload.Length <= 131072)
            {
                ReadOnlySequence<byte> rawPayload = args.ApplicationMessage.Payload;
                string payload;
                if (rawPayload.IsEmpty)
                    payload = string.Empty;
                else
                {
                    byte[] bytes = new byte[checked((int)rawPayload.Length)];
                    rawPayload.CopyTo(bytes);
                    payload = Encoding.UTF8.GetString(bytes);
                }
                MessageReceived?.Invoke(args.ApplicationMessage.Topic, payload);
            }
            return Task.CompletedTask;
        };
        client.DisconnectedAsync += args =>
        {
            if (ReferenceEquals(_client, client) && Interlocked.CompareExchange(ref _disconnecting, 0, 0) == 0)
            {
                _log?.Invoke($"消息服务连接结束：曾连接={args.ClientWasConnected}；原因代码={args.Reason}；连接响应={args.ConnectResult?.ResultCode.ToString() ?? "无"}" +
                    (args.Exception is null ? "" : "；" + MechrevoDiagnostics.ExceptionSummary(args.Exception)));
                Disconnected?.Invoke();
            }
            return Task.CompletedTask;
        };

        try
        {
            _log?.Invoke($"连接消息服务：127.0.0.1:{options.Port}；协议=MQTT 3.1.1；清理会话=True；超时=5 秒");
            var mqttOptions = new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", options.Port)
                .WithCredentials(options.UserName, options.Password)
                .WithClientId(options.ClientId)
                .WithProtocolVersion(MqttProtocolVersion.V311)
                .WithCleanSession(true)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(5))
                .WithTimeout(TimeSpan.FromSeconds(5))
                .Build();
            var result = await client.ConnectAsync(mqttOptions, ct).ConfigureAwait(false);
            _log?.Invoke($"消息服务连接响应：{result.ResultCode}；连接状态={client.IsConnected}");
            if (result.ResultCode != MqttClientConnectResultCode.Success || !client.IsConnected)
                throw new InvalidOperationException("厂商服务拒绝了连接。");
        }
        catch (Exception exception)
        {
            _log?.Invoke("消息服务连接失败；" + MechrevoDiagnostics.ExceptionSummary(exception));
            _client = null;
            client.Dispose();
            throw;
        }
    }

    public async Task SubscribeAsync(IReadOnlyCollection<string> topics, CancellationToken ct)
    {
        IMqttClient client = _client ?? throw new InvalidOperationException("尚未连接 MQTT 服务。");
        if (!client.IsConnected) throw new InvalidOperationException("MQTT 服务连接已断开。");
        var filters = topics.Select(topic => new MqttTopicFilterBuilder()
            .WithTopic(topic)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .Build()).ToList();
        if (filters.Count == 0) throw new ArgumentException("订阅主题不能为空。", nameof(topics));
        MqttClientSubscribeResult result;
        try
        {
            result = await client.SubscribeAsync(new MqttClientSubscribeOptions { TopicFilters = filters }, ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log?.Invoke("消息服务状态订阅失败；" + MechrevoDiagnostics.ExceptionSummary(exception));
            throw;
        }
        _log?.Invoke($"消息服务状态订阅：请求={filters.Count}；响应={result.Items.Count}；结果={string.Join(",", result.Items.Select(item => item.ResultCode).Distinct())}");
        if (result.Items.Count == filters.Count && result.Items.All(item => item.ResultCode is MqttClientSubscribeResultCode.GrantedQoS0 or MqttClientSubscribeResultCode.GrantedQoS1 or MqttClientSubscribeResultCode.GrantedQoS2)) return;
        throw new InvalidOperationException("厂商服务拒绝了状态订阅。");
    }

    public async Task PublishAsync(string topic, string payload, CancellationToken ct)
    {
        IMqttClient client = _client ?? throw new InvalidOperationException("尚未连接 MQTT 服务。");
        if (!client.IsConnected) throw new InvalidOperationException("MQTT 服务连接已断开。");
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .WithRetainFlag(false)
            .Build();
        await client.PublishAsync(message, ct).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        IMqttClient? client = Interlocked.Exchange(ref _client, null);
        if (client is null) return;
        Interlocked.Exchange(ref _disconnecting, 1);
        try
        {
            if (client.IsConnected) await client.DisconnectAsync(cancellationToken: ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _disconnecting, 0);
            client.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
}
