using System;
using System.Threading;
using System.Threading.Tasks;

namespace YALM.Mechrevo;

/// <summary>Explicit mode changes only. No startup, reconnect, power or fan scheduling.</summary>
public sealed class GcuController : IAsyncDisposable
{
    private static readonly string[] Topics = ["Fan/Status", "Setting/Status", "System/FanInfo", "System/CpuInfo", "System/GpuInfo"];
    private const string GetStatus = "{\"Action\":\"GETSTATUS\"}";
    private readonly IGcuTransport _transport;
    private readonly TimeProvider _time;
    private readonly TimeSpan _confirmationTimeout;
    private readonly TimeSpan _pollInterval;
    private readonly Func<bool?>? _readAcPower;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private CancellationTokenSource? _session;
    private GcuSnapshot _snapshot = new();
    private long _modeVersion;
    private bool _disposed;

    public GcuController(IGcuTransport transport, TimeProvider? timeProvider = null,
        TimeSpan? confirmationTimeout = null, TimeSpan? confirmationPollInterval = null, Func<bool?>? readAcPower = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _time = timeProvider ?? TimeProvider.System;
        _confirmationTimeout = confirmationTimeout ?? TimeSpan.FromSeconds(8);
        _pollInterval = confirmationPollInterval ?? TimeSpan.FromMilliseconds(500);
        _readAcPower = readAcPower;
        if (_confirmationTimeout <= TimeSpan.Zero || _pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(confirmationTimeout));
        _transport.MessageReceived += OnMessage;
        _transport.Disconnected += OnDisconnected;
    }

    /// <summary>Raised on the caller or transport thread; subscribers must marshal to their UI.</summary>
    public event Action? Changed;
    public GcuSnapshot Snapshot { get { lock (_sync) return _snapshot; } }

    public async Task ConnectAsync(GcuConnectionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ClientId)) throw new ArgumentException("客户端标识不能为空。", nameof(options));
        if (string.IsNullOrWhiteSpace(options.UserName)) throw new ArgumentException("用户名不能为空。", nameof(options));
        if (string.IsNullOrEmpty(options.Password)) throw new ArgumentException("密码不能为空。", nameof(options));
        if (options.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(options));
        await _operation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Snapshot.IsConnected) return;
            await CloseTransportAsync().ConfigureAwait(false);
            var session = new CancellationTokenSource();
            lock (_sync) _session = session;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token);
            try
            {
                await _transport.ConnectAsync(options, linked.Token).ConfigureAwait(false);
                await _transport.SubscribeAsync(Topics, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    _snapshot = new GcuSnapshot
                    {
                        IsConnected = _transport.IsConnected,
                        TurboSupported = options.TurboSupported,
                        TurboSupportedUpdatedAt = options.TurboSupported.HasValue ? _time.GetUtcNow() : null
                    };
                    _modeVersion = 0;
                }
                RaiseChanged();
                // The original service reinitializes hardware telemetry on System_ON.
                // Enable once per connection, not on every status refresh.
                await _transport.PublishAsync("System/Control", "{\"Action\":\"System_ON\"}", linked.Token).ConfigureAwait(false);
                await RefreshCoreAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await CloseTransportAsync().ConfigureAwait(false);
                SetDisconnected(exception is OperationCanceledException ? "连接已取消。" : "无法连接厂商服务，请检查服务与认证信息。");
                throw;
            }
        }
        finally { _operation.Release(); }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _operation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var linked = LinkedSession(ct);
            await RefreshCoreAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetError("状态读取失败，请检查厂商服务。");
            throw;
        }
        finally { _operation.Release(); }
    }

    public async Task<ModeSwitchResult> SetModeAsync(PerformanceMode mode, CancellationToken ct = default)
    {
        if (!await _operation.WaitAsync(0, ct).ConfigureAwait(false))
            return new(ModeSwitchStatus.Busy, "上一项操作尚未完成。");
        try
        {
            if (_disposed || !_transport.IsConnected || !Snapshot.IsConnected)
                return new(ModeSwitchStatus.Disconnected, "尚未连接厂商服务。");
            ReadPlatformPower();
            GcuSnapshot before = Snapshot;
            if (!before.CanSwitchMode(mode, _time.GetUtcNow()))
                return new(ModeSwitchStatus.Rejected, "状态或风扇读数不完整、已过期，或当前不支持该模式。");
            if (before.Mode == (int)mode)
                return new(ModeSwitchStatus.Rejected, "服务已报告该模式，无需重复发送。");

            using var linked = LinkedSession(ct);
            string action = mode switch
            {
                PerformanceMode.Silent => "OPERATING_OFFICE_MODE",
                PerformanceMode.Balanced => "OPERATING_GAMING_MODE",
                PerformanceMode.Turbo => "OPERATING_TURBO_MODE",
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
            await _transport.PublishAsync("Fan/Control", $"{{\"Action\":\"{action}\",\"ProfileIndex\":0}}", linked.Token).ConfigureAwait(false);
            await _transport.PublishAsync("LCHWOC/Control", $"{{\"IsNormalRun\":{(int)mode}}}", linked.Token).ConfigureAwait(false);
            // Readbacks before both commands finish cannot confirm this request. A fresh query follows.
            long baseline;
            lock (_sync) baseline = _modeVersion;
            long started = _time.GetTimestamp();
            while (_time.GetElapsedTime(started) < _confirmationTimeout)
            {
                await _transport.PublishAsync("Fan/Control", GetStatus, linked.Token).ConfigureAwait(false);
                if (Confirmed(mode, baseline))
                    return ConfirmedResult();
                TimeSpan remaining = _confirmationTimeout - _time.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < _pollInterval ? remaining : _pollInterval, _time, linked.Token).ConfigureAwait(false);
                if (Confirmed(mode, baseline))
                    return ConfirmedResult();
            }
            SetError("等待服务模式回读超时；未再次发送切换命令。");
            return new(ModeSwitchStatus.TimedOut, "等待服务回读超时，实际模式尚未确认。");
        }
        catch (OperationCanceledException)
        {
            return ct.IsCancellationRequested
                ? new(ModeSwitchStatus.Cancelled, "操作已取消；已经发送的命令仍可能由服务执行。")
                : new(ModeSwitchStatus.Disconnected, "连接已断开，实际模式尚未确认。");
        }
        catch (Exception)
        {
            SetError("切换请求未完成；实际模式尚未确认。");
            return new(ModeSwitchStatus.Failed, "切换请求未完成，实际模式尚未确认。");
        }
        finally { _operation.Release(); }
    }

    public async Task DisconnectAsync()
    {
        CancelSession();
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseTransportAsync().ConfigureAwait(false);
            SetDisconnected(null);
        }
        finally { _operation.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        await DisconnectAsync().ConfigureAwait(false);
        _transport.MessageReceived -= OnMessage;
        _transport.Disconnected -= OnDisconnected;
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        if (!_transport.IsConnected) throw new InvalidOperationException("尚未连接厂商服务。");
        ReadPlatformPower();
        await _transport.PublishAsync("Fan/Control", GetStatus, ct).ConfigureAwait(false);
        await _transport.PublishAsync("Setting/Control", GetStatus, ct).ConfigureAwait(false);
    }

    private CancellationTokenSource LinkedSession(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_session is null || !_snapshot.IsConnected) throw new InvalidOperationException("尚未连接厂商服务。");
            return CancellationTokenSource.CreateLinkedTokenSource(ct, _session.Token);
        }
    }

    private void ReadPlatformPower()
    {
        if (_readAcPower is null) return;
        bool? isAc;
        try { isAc = _readAcPower(); }
        catch { isAc = null; }
        lock (_sync)
        {
            _snapshot = _snapshot with { IsAc = isAc, IsAcUpdatedAt = isAc.HasValue ? _time.GetUtcNow() : null };
        }
        RaiseChanged();
    }

    private bool Confirmed(PerformanceMode mode, long baseline)
    {
        lock (_sync)
            return _modeVersion > baseline && _snapshot.IsConnected && _transport.IsConnected &&
                _snapshot.Mode == (int)mode && _snapshot.IsStatusFresh(_time.GetUtcNow());
    }

    private ModeSwitchResult ConfirmedResult()
    {
        SetError(null);
        return new(ModeSwitchStatus.ServiceConfirmed, "厂商服务已回报目标模式；硬件效果仍需结合温度与转速观察。");
    }

    private void OnMessage(string topic, string payload)
    {
        bool changed;
        lock (_sync)
        {
            if (_disposed || !_snapshot.IsConnected || !_transport.IsConnected || _session?.IsCancellationRequested != false) return;
            var next = GcuMessageParser.Apply(_snapshot, topic, payload, _time.GetUtcNow(), out bool modeReported);
            // When available, the operating system is the sole power source.
            // A delayed vendor message must not replace a fresh unplugged/unknown reading.
            if (_readAcPower is not null)
                next = next with { IsAc = _snapshot.IsAc, IsAcUpdatedAt = _snapshot.IsAcUpdatedAt };
            if (modeReported) _modeVersion++;
            changed = next != _snapshot;
            _snapshot = next;
        }
        if (changed) RaiseChanged();
    }

    private void OnDisconnected()
    {
        CancelSession();
        SetDisconnected("厂商服务连接已断开。");
    }

    private void CancelSession()
    {
        lock (_sync) _session?.Cancel();
    }

    private async Task CloseTransportAsync()
    {
        CancelSession();
        try { await _transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); }
        finally
        {
            lock (_sync)
            {
                _session?.Dispose();
                _session = null;
            }
        }
    }

    private void SetDisconnected(string? error)
    {
        lock (_sync) { _snapshot = new GcuSnapshot { LastError = error }; _modeVersion = 0; }
        RaiseChanged();
    }

    private void SetError(string? error)
    {
        lock (_sync) _snapshot = _snapshot with { LastError = error };
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception) { /* UI subscribers cannot interrupt transport or confirmation. */ }
        }
    }
}
