using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace YALM.Mechrevo;

/// <summary>Explicit mode changes only. No startup, reconnect, power or fan scheduling.</summary>
public sealed class GcuController : IAsyncDisposable
{
    private static readonly string[] Topics =
        ["Fan/Status", "Fan/Table", "Setting/Status", "Keyboard/Status", "System/FanInfo", "System/CpuInfo", "System/GpuInfo"];
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
    private long _customSettingsVersion;
    private long _fanTableVersion;
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
                    _customSettingsVersion = 0;
                    _fanTableVersion = 0;
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

    public async Task<GcuWriteResult> RefreshCustomAsync(CancellationToken ct = default)
    {
        return await ConfirmWriteAsync(
            async token =>
            {
                await _transport.PublishAsync("Fan/Control", GetStatus, token).ConfigureAwait(false);
                await _transport.PublishAsync("Fan/Control",
                    "{\"Action\":\"GET_FAN_SPEED_CURVE_SETTING\"}", token).ConfigureAwait(false);
            },
            _ => true,
            requireCustomSettings: true,
            requireFanTable: true,
            success: "自定义设置和两路曲线已读取。",
            ct: ct).ConfigureAwait(false);
    }

    public async Task<GcuWriteResult> SetCustomProfileAsync(int profileIndex, CancellationToken ct = default)
    {
        if (profileIndex is < 0 or > 4)
            return new(GcuWriteStatus.Rejected, "自定义档位必须在 0..4 之间。");
        return await ConfirmWriteAsync(
            async token =>
            {
                string primary = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["Action"] = "OPERATING_CUSTOM_MODE",
                    ["ProfileIndex"] = profileIndex
                });
                await _transport.PublishAsync("Fan/Control", primary, token).ConfigureAwait(false);
                await _transport.PublishAsync("LCHWOC/Control", "{\"IsCustomRun\":true}", token).ConfigureAwait(false);
            },
            snapshot => snapshot.Mode == 3 &&
                snapshot.CustomSettings?.ProfileIndex == profileIndex,
            requireCustomSettings: true,
            success: $"已切换到自定义 Profile-{profileIndex + 1}。",
            ct: ct).ConfigureAwait(false);
    }

    public Task<GcuWriteResult> SetFnLockAsync(bool locked, CancellationToken ct = default)
    {
        if (!Snapshot.IsFnLockFresh(_time.GetUtcNow()))
            return Task.FromResult(new GcuWriteResult(GcuWriteStatus.Rejected, "功能键锁定状态尚未读取或已过期。"));
        string payload = JsonSerializer.Serialize(new { Action = locked ? "FNKEY_LOCK" : "FNKEY_UNLOCK" });
        return ConfirmPeripheralAsync("Setting/Control", "Setting/Status", payload,
            report => report.FnLocked == locked, "功能键锁定已回读确认。", ct);
    }

    public Task<GcuWriteResult> SetKeyboardPowerAsync(bool on, CancellationToken ct = default)
    {
        if (!Snapshot.IsKeyboardFresh(_time.GetUtcNow()) || Snapshot.Keyboard is not { SingleZone: true, Power: not null })
            return Task.FromResult(new GcuWriteResult(GcuWriteStatus.Rejected, "尚未读到支持的键盘开关状态。"));
        return ConfirmPeripheralAsync("Keyboard/Ctrl", "Keyboard/Status",
            JsonSerializer.Serialize(new { function = "SetPower", powerstatus = on ? 1 : 0 }),
            report => report.Keyboard?.Power == on, "键盘灯开关已回读确认。", ct);
    }

    public Task<GcuWriteResult> SetKeyboardEffectAsync(string effect, int brightness, int colorIndex,
        CancellationToken ct = default)
    {
        if (!Snapshot.IsKeyboardFresh(_time.GetUtcNow()) || Snapshot.Keyboard?.SingleZone != true)
            return Task.FromResult(new GcuWriteResult(GcuWriteStatus.Rejected, "尚未读到支持的单区键盘状态。"));
        if (effect is not ("Single" or "Rainbow") || brightness is < 0 or > 4 || colorIndex is < 1 or > 30)
            return Task.FromResult(new GcuWriteResult(GcuWriteStatus.Rejected, "键盘灯效、亮度或颜色超出支持范围。"));
        return ConfirmPeripheralAsync("Keyboard/Ctrl", "Keyboard/Status",
            JsonSerializer.Serialize(GcuKeyboardProtocol.SingleZoneEffect(effect, brightness, colorIndex)),
            report => report.Keyboard is { } keyboard && keyboard.Effect == effect &&
                keyboard.Brightness == brightness && (effect != "Single" || keyboard.MonochromeIndex == colorIndex),
            "键盘灯效已回读确认。", ct);
    }

    private async Task<GcuWriteResult> ConfirmPeripheralAsync(string controlTopic, string statusTopic,
        string payload, Func<GcuSnapshot, bool> confirmed, string success, CancellationToken ct)
    {
        if (!await _operation.WaitAsync(0, ct).ConfigureAwait(false))
            return new(GcuWriteStatus.Busy, "上一项操作尚未完成。");
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(string topic, string message)
        {
            if (topic != statusTopic || !_transport.IsConnected || !Snapshot.IsConnected) return;
            // Confirm only fields actually present in a new frame, never merged cached fields.
            GcuSnapshot report = GcuMessageParser.Apply(new GcuSnapshot { IsConnected = true },
                topic, message, _time.GetUtcNow(), out _);
            if (confirmed(report)) received.TrySetResult();
        }
        try
        {
            if (_disposed || !_transport.IsConnected || !Snapshot.IsConnected)
                return new(GcuWriteStatus.Disconnected, "尚未连接厂商服务。");
            using var linked = LinkedSession(ct);
            await _transport.PublishAsync(controlTopic, payload, linked.Token).ConfigureAwait(false);
            _transport.MessageReceived += Observe;
            long started = _time.GetTimestamp();
            while (_time.GetElapsedTime(started) < _confirmationTimeout)
            {
                await _transport.PublishAsync(controlTopic, GetStatus, linked.Token).ConfigureAwait(false);
                TimeSpan remaining = _confirmationTimeout - _time.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) break;
                await Task.WhenAny(received.Task,
                    Task.Delay(remaining < _pollInterval ? remaining : _pollInterval, _time, linked.Token)).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                if (received.Task.IsCompleted) return new(GcuWriteStatus.Confirmed, success);
            }
            return new(GcuWriteStatus.TimedOut, "请求已发送，服务尚未回报完整的目标设置，请检查实际效果。");
        }
        catch (OperationCanceledException)
        {
            return new(ct.IsCancellationRequested ? GcuWriteStatus.Cancelled : GcuWriteStatus.Disconnected,
                "操作已中断，设置是否生效尚未确认。");
        }
        catch
        {
            return new(GcuWriteStatus.Failed, "设置请求未完成，请检查厂商服务。");
        }
        finally
        {
            _transport.MessageReceived -= Observe;
            _operation.Release();
        }
    }

    public async Task<GcuWriteResult> SetFanBoostAsync(bool enabled, CancellationToken ct = default)
    {
        if (Snapshot.Mode != 3)
            return new(GcuWriteStatus.Rejected, "风扇增强只能在自定义模式中切换。");

        return await ConfirmWriteAsync(
            token => _transport.PublishAsync("Fan/Control",
                enabled ? "{\"Action\":\"FAN_BOOST_ON\"}" : "{\"Action\":\"FAN_BOOST_OFF\"}", token),
            snapshot => snapshot.CustomSettings?.FanBoostEnabled == enabled,
            requireCustomSettings: true,
            success: enabled ? "风扇增强已开启。" : "风扇增强已关闭。",
            ct: ct).ConfigureAwait(false);
    }

    public async Task<GcuWriteResult> SetCustomFieldAsync(
        GcuCustomWriteField field, int value, CancellationToken ct = default)
    {
        GcuSnapshot before = Snapshot;
        GcuCustomSettings? settings = before.CustomSettings;
        if (settings is null || !before.IsCustomSettingsFresh(_time.GetUtcNow()))
            return new(GcuWriteStatus.Rejected, "自定义设置尚未读取或已过期。");
        if (before.Mode != 3)
            return new(GcuWriteStatus.Rejected, "请先切换到自定义模式，再修改这些参数。");
        if (!TryEncodeCustomField(settings, field, value, out string wireKey, out string wireValue,
            out Func<GcuCustomSettings, bool> confirmed, out string rejection))
            return new(GcuWriteStatus.Rejected, rejection);

        string payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Action"] = "SET_OPERATING_MODE_DETAIL",
            [wireKey] = wireValue
        });
        return await ConfirmWriteAsync(
            token => _transport.PublishAsync("Fan/Control", payload, token),
            snapshot => snapshot.CustomSettings is { } current && confirmed(current),
            requireCustomSettings: true,
            success: "服务已回读目标参数。",
            ct: ct).ConfigureAwait(false);
    }

    public async Task<GcuWriteResult> SetFanCurveAsync(
        FanCurveTarget target,
        IReadOnlyList<int> duties,
        CancellationToken ct = default)
    {
        if (duties.Count != 16 || duties.Any(value => value is < 0 or > 100))
            return new(GcuWriteStatus.Rejected, "风扇曲线必须包含 16 个 0..100 的整数占空比。");
        if (duties[0] != 0)
            return new(GcuWriteStatus.Rejected, "风扇曲线第 1 个温度点的占空比固定为 0%。");
        for (int i = 1; i < duties.Count; i++)
        {
            if (duties[i] < duties[i - 1])
                return new(GcuWriteStatus.Rejected, "风扇曲线占空比不能随温度下降。");
        }
        for (int i = 11; i < 16; i++)
        {
            if (duties[i] != duties[10])
                return new(GcuWriteStatus.Rejected, "风扇曲线最后 5 个尾值必须与第 11 个有效点相同。");
        }

        GcuSnapshot before = Snapshot;
        if (before.Mode != 3)
            return new(GcuWriteStatus.Rejected, "风扇曲线只能在自定义模式中应用。");
        FanCurveTable? table = before.FanTable;
        if (table is null || !before.IsFanTableFresh(_time.GetUtcNow()) ||
            string.IsNullOrWhiteSpace(table.Name) ||
            table.Curve(target).Count != 16)
            return new(GcuWriteStatus.Rejected, "当前风扇表尚未读取或已经过期。");

        var payload = new Dictionary<string, object>
        {
            ["Action"] = "SET_FAN_SPEED_CURVE_SETTING",
            ["Name"] = table.Name,
            ["Type"] = target == FanCurveTarget.Cpu ? "CPU" : "GPU"
        };
        for (int i = 0; i < duties.Count; i++)
            payload[$"T{i}"] = duties[i].ToString(System.Globalization.CultureInfo.InvariantCulture);

        return await ConfirmWriteAsync(
            token => _transport.PublishAsync("Fan/Control", JsonSerializer.Serialize(payload), token),
            snapshot => CurveMatches(snapshot.FanTable, target, duties),
            requireFanTable: true,
            success: (target == FanCurveTarget.Cpu ? "CPU" : "GPU") + " 风扇曲线已回读确认。",
            ct: ct).ConfigureAwait(false);
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
        await _transport.PublishAsync("Keyboard/Ctrl", GetStatus, ct).ConfigureAwait(false);
    }

    private async Task<GcuWriteResult> ConfirmWriteAsync(
        Func<CancellationToken, Task> send,
        Func<GcuSnapshot, bool> confirmed,
        bool requireCustomSettings = false,
        bool requireFanTable = false,
        string success = "服务已回读目标状态。",
        CancellationToken ct = default)
    {
        if (!await _operation.WaitAsync(0, ct).ConfigureAwait(false))
            return new(GcuWriteStatus.Busy, "上一项操作尚未完成。");
        try
        {
            if (_disposed || !_transport.IsConnected || !Snapshot.IsConnected)
                return new(GcuWriteStatus.Disconnected, "尚未连接厂商服务。");

            long modeBaseline;
            long customBaseline;
            long tableBaseline;
            lock (_sync)
            {
                modeBaseline = _modeVersion;
                customBaseline = _customSettingsVersion;
                tableBaseline = _fanTableVersion;
            }

            using var linked = LinkedSession(ct);
            await send(linked.Token).ConfigureAwait(false);
            long started = _time.GetTimestamp();
            while (_time.GetElapsedTime(started) < _confirmationTimeout)
            {
                await _transport.PublishAsync("Fan/Control", GetStatus, linked.Token).ConfigureAwait(false);
                if (requireFanTable)
                    await _transport.PublishAsync("Fan/Control",
                        "{\"Action\":\"GET_FAN_SPEED_CURVE_SETTING\"}", linked.Token).ConfigureAwait(false);

                bool changed;
                lock (_sync)
                {
                    bool modeChanged = _modeVersion > modeBaseline;
                    bool customChanged = _customSettingsVersion > customBaseline;
                    bool tableChanged = _fanTableVersion > tableBaseline;
                    changed = (!requireCustomSettings || customChanged) &&
                        (!requireFanTable || tableChanged) &&
                        (modeChanged || customChanged || tableChanged) &&
                        confirmed(_snapshot);
                }
                if (changed)
                {
                    SetError(null);
                    return new(GcuWriteStatus.Confirmed, success);
                }

                TimeSpan remaining = _confirmationTimeout - _time.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < _pollInterval ? remaining : _pollInterval, _time, linked.Token)
                    .ConfigureAwait(false);
            }

            SetError("等待服务回读超时；参数是否生效尚未确认。");
            return new(GcuWriteStatus.TimedOut, "等待服务回读超时，界面不会把请求显示成已生效。");
        }
        catch (OperationCanceledException)
        {
            return ct.IsCancellationRequested
                ? new(GcuWriteStatus.Cancelled, "操作已取消；已经发送的命令仍可能由服务执行。")
                : new(GcuWriteStatus.Disconnected, "连接已断开，参数是否生效尚未确认。");
        }
        catch (Exception)
        {
            SetError("参数请求未完成，回读尚未确认。");
            return new(GcuWriteStatus.Failed, "参数请求未完成，回读尚未确认。");
        }
        finally { _operation.Release(); }
    }

    private static bool TryEncodeCustomField(
        GcuCustomSettings settings,
        GcuCustomWriteField field,
        int value,
        out string wireKey,
        out string wireValue,
        out Func<GcuCustomSettings, bool> confirmed,
        out string rejection)
    {
        wireKey = "";
        wireValue = "";
        confirmed = _ => false;
        rejection = "该参数在当前固件状态下不可调。";

        static bool InRange(int value, int? minimum, int? maximum) =>
            minimum.HasValue && maximum.HasValue && minimum <= maximum &&
            value >= minimum.Value && value <= maximum.Value;

        switch (field)
        {
            case GcuCustomWriteField.Pl1 when InRange(value, settings.Pl1Minimum, settings.Pl1Maximum):
                wireKey = "PL1";
                wireValue = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                confirmed = current => current.Pl1 == value;
                break;
            case GcuCustomWriteField.Pl2 when InRange(value, settings.Pl2Minimum, settings.Pl2Maximum):
                wireKey = "PL2";
                wireValue = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                confirmed = current => current.Pl2 == value;
                break;
            case GcuCustomWriteField.Pl4 when InRange(value, settings.Pl4Minimum, settings.Pl4Maximum):
                if (settings.Pl4HalfWatt == true)
                {
                    if ((value & 1) != 0)
                    {
                        rejection = "该机型的瞬时功耗按半瓦单位写入，只能选择偶数瓦。";
                        return false;
                    }
                    wireValue = (value / 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                else
                {
                    wireValue = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                wireKey = "PL4";
                confirmed = current => current.Pl4 == value;
                break;
            case GcuCustomWriteField.TccTarget when settings.TjMax is { } tjMax &&
                value is >= 75 and <= 95 && value <= tjMax:
                wireKey = "CpuTccOffset";
                wireValue = (tjMax - value).ToString(System.Globalization.CultureInfo.InvariantCulture);
                confirmed = current => current.TccTarget == value;
                break;
            case GcuCustomWriteField.TccEnabled when value is 0 or 1:
                wireKey = "CpuTccOffsetSwitch";
                wireValue = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                confirmed = current => current.TccEnabled == (value == 1);
                break;
            case GcuCustomWriteField.FanSwitchSpeed when value is >= 100 and <= 25500:
                int quantized = value / 100 * 100;
                wireKey = "FanSwitchSpeed";
                wireValue = quantized.ToString(System.Globalization.CultureInfo.InvariantCulture);
                confirmed = current => current.FanSwitchSpeed == quantized;
                break;
            case GcuCustomWriteField.FanSwitchSpeedEnabled when value is 0 or 1:
                wireKey = "FanSwitchSpeedEnabled";
                wireValue = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                confirmed = current => current.FanSwitchSpeedEnabled == (value == 1);
                break;
            default:
                return false;
        }
        rejection = "";
        return true;
    }

    private static bool CurveMatches(
        FanCurveTable? table,
        FanCurveTarget target,
        IReadOnlyList<int> duties)
    {
        if (table is null || table.Curve(target).Count != 16 || duties.Count != 16) return false;
        IReadOnlyList<FanCurvePoint> points = table.Curve(target);
        return points.Select(point => point.Duty).SequenceEqual(duties);
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
            var next = GcuMessageParser.Apply(_snapshot, topic, payload, _time.GetUtcNow(),
                out bool modeReported, out bool customSettingsReported, out bool fanTableReported);
            // When available, the operating system is the sole power source.
            // A delayed vendor message must not replace a fresh unplugged/unknown reading.
            if (_readAcPower is not null)
                next = next with { IsAc = _snapshot.IsAc, IsAcUpdatedAt = _snapshot.IsAcUpdatedAt };
            if (modeReported) _modeVersion++;
            if (customSettingsReported) _customSettingsVersion++;
            if (fanTableReported) _fanTableVersion++;
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
        lock (_sync)
        {
            _snapshot = new GcuSnapshot { LastError = error };
            _modeVersion = 0;
            _customSettingsVersion = 0;
            _fanTableVersion = 0;
        }
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
