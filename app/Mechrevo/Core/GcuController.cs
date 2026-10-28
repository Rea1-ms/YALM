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
    private bool _modeControlSupported = true;
    private bool _customControlSupported = true;
    private bool _peripheralControlSupported = true;
    // Hardware writes are opt-in for the current connection.  Protocol
    // capability and this user grant are intentionally kept separate: the
    // former controls what the adapter knows how to do, while the latter is
    // the final session-local safety gate.
    private bool _writePermissionGranted;
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

    /// <summary>Whether the current connection has an explicit user grant for hardware writes.</summary>
    public bool WritePermissionGranted { get { lock (_sync) return _writePermissionGranted; } }

    /// <summary>Whether this connection has at least one reviewed write capability.</summary>
    public bool WritePermissionAvailable
    {
        get
        {
            lock (_sync)
                return _snapshot.IsConnected && (_modeControlSupported || _customControlSupported || _peripheralControlSupported);
        }
    }

    /// <summary>
    /// Grants or revokes hardware writes for this connection. Granting is
    /// deliberately rejected while disconnected or when the adapter exposes
    /// no reviewed write capability. Revoking never sends a hardware command.
    /// </summary>
    public bool SetWritePermission(bool enabled)
    {
        bool changed;
        lock (_sync)
        {
            if (enabled && (!_snapshot.IsConnected ||
                !(_modeControlSupported || _customControlSupported || _peripheralControlSupported)))
                return false;
            changed = _writePermissionGranted != enabled;
            _writePermissionGranted = enabled;
        }
        if (changed) RaiseChanged();
        return true;
    }

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
                    _modeControlSupported = options.ModeControlSupported;
                    _customControlSupported = options.CustomControlSupported;
                    _peripheralControlSupported = options.PeripheralControlSupported;
                    _writePermissionGranted = options.WritePermissionGranted &&
                        (options.ModeControlSupported || options.CustomControlSupported || options.PeripheralControlSupported);
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
            ct: ct,
            queryOnly: true).ConfigureAwait(false);
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
                await PublishWriteAsync("Fan/Control", primary, token, WriteCapability.Custom).ConfigureAwait(false);
                await PublishWriteAsync("LCHWOC/Control", "{\"IsCustomRun\":true}", token, WriteCapability.Custom).ConfigureAwait(false);
            },
            snapshot => snapshot.Mode == 3 &&
                snapshot.CustomSettings?.ProfileIndex == profileIndex,
            requireCustomSettings: true,
            success: $"已切换到自定义 Profile-{profileIndex + 1}。",
            ct: ct).ConfigureAwait(false);
    }

    public Task<GcuWriteResult> SetGpuTargetAsync(GpuDisplayRoute target, CancellationToken ct = default)
    {
        GcuSnapshot before = Snapshot;
        if (!before.CanSetGpuTarget(target, _time.GetUtcNow()))
            return Task.FromResult(new GcuWriteResult(GcuWriteStatus.Rejected, "显示切换能力或目标状态尚未确认，暂时不能切换。"));
        if (before.GpuTarget == target)
            return Task.FromResult(new GcuWriteResult(GcuWriteStatus.Rejected, "该显示目标已保存，无需重复设置。"));
        string action = target == GpuDisplayRoute.Direct ? "DGPU_DIRECT_CONNECT_TOGGLE_ON" : "DGPU_DIRECT_CONNECT_TOGGLE_OFF";
        string name = target == GpuDisplayRoute.Direct ? "独显直连" : "混合模式";
        return ConfirmPeripheralAsync("Setting/Control", "Setting/Status", JsonSerializer.Serialize(new { Action = action }),
            report => report.GpuTarget == target && report.GpuSwitchSupported == true,
            $"已保存显示目标：{name}，重启后生效。", ct,
            canPublish: current => current.CanSetGpuTarget(target, _time.GetUtcNow()) && current.GpuTarget != target);
    }

    public Task<GcuWriteResult> SetFnLockAsync(bool locked, CancellationToken ct = default)
    {
        if (!Snapshot.IsFnLockFresh(_time.GetUtcNow()))
            return Task.FromResult(new GcuWriteResult(GcuWriteStatus.Rejected, "功能键锁定状态尚未读取或已过期。"));
        string payload = JsonSerializer.Serialize(new { Action = locked ? "FNKEY_LOCK" : "FNKEY_UNLOCK" });
        return ConfirmPeripheralAsync("Setting/Control", "Setting/Status", payload,
            report => report.FnLocked == locked, "功能键锁定已回读确认。", ct);
    }

    public Task<GcuWriteResult> SetQuickSettingAsync(GcuQuickSetting setting, bool enabled, CancellationToken ct = default)
    {
        GcuQuickSettingDefinition? definition = GcuQuickSettings.Find(setting);
        if (definition is null)
            return Task.FromResult(new GcuWriteResult(GcuWriteStatus.Rejected, "不支持的快捷设置。"));
        bool CanPublish(GcuSnapshot current)
        {
            GcuQuickSettingState state = current.QuickSetting(setting);
            return state.IsFresh(_time.GetUtcNow()) && state.Enabled != enabled;
        }
        if (!CanPublish(Snapshot))
            return Task.FromResult(new GcuWriteResult(GcuWriteStatus.Rejected, "设置状态未读取、已过期或与目标相同，请重新读取。"));
        return ConfirmPeripheralAsync("Setting/Control", "Setting/Status",
            JsonSerializer.Serialize(new { Action = enabled ? definition.EnableAction : definition.DisableAction }),
            report => report.QuickSetting(setting).Enabled == enabled,
            definition.Title + "已回读确认。", ct, canPublish: CanPublish);
    }

    public Task<GcuWriteResult> SetKeyboardPowerAsync(bool on, CancellationToken ct = default)
    {
        if (!Snapshot.IsKeyboardFresh(_time.GetUtcNow()) || Snapshot.Keyboard is not { SingleZone: true, Power: not null })
            return Task.FromResult(new GcuWriteResult(GcuWriteStatus.Rejected, "尚未读到支持的键盘开关状态。"));
        return ConfirmPeripheralAsync("Keyboard/Ctrl", "Keyboard/Status",
            JsonSerializer.Serialize(new { function = "SetPower", powerstatus = on ? 1 : 0 }),
            report => report.Keyboard is { SingleZone: true } keyboard && keyboard.Power == on,
            "键盘灯开关已回读确认。", ct);
    }

    public Task<GcuWriteResult> RefreshKeyboardAsync(CancellationToken ct = default) =>
        ConfirmPeripheralAsync("Keyboard/Ctrl", "Keyboard/Status", GetStatus,
            report => report.Keyboard?.HasEffectParameters == true,
            "键盘设置已重新读取。", ct, queryOnly: true);

    public Task<GcuWriteResult> SetKeyboardEffectAsync(GcuKeyboardDraft draft, CancellationToken ct = default)
    {
        GcuSnapshot before = Snapshot;
        if (!before.IsKeyboardFresh(_time.GetUtcNow()) || before.Keyboard?.SingleZone != true)
            return Task.FromResult(new GcuWriteResult(GcuWriteStatus.Rejected, "尚未读到支持的单区键盘状态。"));
        if (!draft.IsValid)
            return Task.FromResult(new GcuWriteResult(GcuWriteStatus.Rejected, "键盘设置不完整或超出支持范围。"));
        if (!draft.BasedOn.SameEffectParameters(before.Keyboard) || draft.BasedOn.Effect != before.Keyboard.Effect)
            return Task.FromResult(new GcuWriteResult(GcuWriteStatus.Rejected, "键盘设置已变化，请重新读取后再应用。"));
        int brightness = before.Keyboard.Brightness!.Value;
        return ConfirmPeripheralAsync("Keyboard/Ctrl", "Keyboard/Status",
            JsonSerializer.Serialize(GcuKeyboardProtocol.SingleZoneEffect(draft, brightness)),
            report => report.Keyboard is { SingleZone: true } keyboard && keyboard.Effect == draft.Effect &&
                keyboard.Brightness == brightness && keyboard.MonochromeIndex == draft.MonochromeIndex &&
                keyboard.ManualIndices?.SequenceEqual(draft.ManualIndices) == true &&
                keyboard.ManualInterval == draft.ManualInterval && keyboard.BreathingIndex == draft.BasedOn.BreathingIndex &&
                // This service does not expose trustworthy custom RGB readback for SingleZone.
                (draft.Effect != "Single" || draft.MonochromeIndex != 51) &&
                (draft.Effect != "Manual" || !draft.ManualIndices.Contains(51)),
            "键盘灯效已回读确认。", ct, waitForConfirmation: false);
    }

    public Task<GcuWriteResult> SetKeyboardBrightnessAsync(int brightness, CancellationToken ct = default)
    {
        if (!Snapshot.IsKeyboardFresh(_time.GetUtcNow()) ||
            Snapshot.Keyboard is not { SingleZone: true, Brightness: not null })
            return Task.FromResult(new GcuWriteResult(GcuWriteStatus.Rejected, "尚未读到支持的键盘亮度状态。"));
        if (brightness is < 0 or > 4)
            return Task.FromResult(new GcuWriteResult(GcuWriteStatus.Rejected, "键盘亮度必须在 0～4 档之间。"));
        // The dedicated command leaves the current effect and six-color sequence intact.
        return ConfirmPeripheralAsync("Keyboard/Ctrl", "Keyboard/Status",
            JsonSerializer.Serialize(new
            {
                function = "SetLightingLevel", mode = "Lighting",
                light = brightness.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }),
            report => report.Keyboard is { SingleZone: true } keyboard && keyboard.Brightness == brightness,
            "键盘亮度已回读确认。", ct);
    }

    private async Task<GcuWriteResult> ConfirmPeripheralAsync(string controlTopic, string statusTopic,
        string payload, Func<GcuSnapshot, bool> confirmed, string success, CancellationToken ct,
        bool queryOnly = false, bool waitForConfirmation = true, Func<GcuSnapshot, bool>? canPublish = null)
    {
        if (!await _operation.WaitAsync(0, ct).ConfigureAwait(false))
            return new(GcuWriteStatus.Busy, "上一项操作尚未完成。");
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool published = false;
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
            if (!queryOnly && !_peripheralControlSupported)
                return new(GcuWriteStatus.Rejected, "此服务协议尚未适配键盘和设备设置写入。");
            if (!queryOnly && !WritePermissionGranted)
                return new(GcuWriteStatus.Rejected, "硬件写入尚未允许，请在更多设置中确认警告后开启。");
            if (canPublish is not null && !canPublish(Snapshot))
                return new(GcuWriteStatus.Rejected, "设置状态已变化或过期，请重新读取后再应用。");
            using var linked = LinkedSession(ct);
            if (!queryOnly)
            {
                await PublishWriteAsync(controlTopic, payload, linked.Token, WriteCapability.Peripheral).ConfigureAwait(false);
                published = true;
            }
            _transport.MessageReceived += Observe;
            long started = _time.GetTimestamp();
            while (_time.GetElapsedTime(started) < _confirmationTimeout)
            {
                await _transport.PublishAsync(controlTopic, GetStatus, linked.Token).ConfigureAwait(false);
                // Lighting does not report its effect name on this service. Send one query,
                // then release the UI; normal status updates continue independently.
                if (!waitForConfirmation)
                    return received.Task.IsCompleted ? new(GcuWriteStatus.Confirmed, success)
                        : new(GcuWriteStatus.Sent, "灯效请求已发送，状态将自动刷新；实际效果尚未确认。");
                TimeSpan remaining = _confirmationTimeout - _time.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) break;
                await Task.WhenAny(received.Task,
                    Task.Delay(remaining < _pollInterval ? remaining : _pollInterval, _time, linked.Token)).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                if (received.Task.IsCompleted) return new(GcuWriteStatus.Confirmed, success);
            }
            return new(GcuWriteStatus.TimedOut, "请求已发送，服务尚未回报完整的目标设置，请检查实际效果。");
        }
        catch (WritePermissionRevokedException)
        {
            return new(GcuWriteStatus.Rejected, "硬件写入许可已关闭，后续设置未发送；实际状态请重新读取确认。");
        }
        catch (OperationCanceledException)
        {
            if (!waitForConfirmation && published)
                return new(GcuWriteStatus.Sent, "灯效请求已发送，状态读取已中断；实际效果尚未确认。");
            return new(ct.IsCancellationRequested ? GcuWriteStatus.Cancelled : GcuWriteStatus.Disconnected,
                "操作已中断，设置是否生效尚未确认。");
        }
        catch
        {
            if (!waitForConfirmation && published)
                return new(GcuWriteStatus.Sent, "灯效请求已发送，状态暂无法读取；实际效果尚未确认。");
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
            token => PublishWriteAsync("Fan/Control",
                enabled ? "{\"Action\":\"FAN_BOOST_ON\"}" : "{\"Action\":\"FAN_BOOST_OFF\"}", token,
                WriteCapability.Custom),
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
            token => PublishWriteAsync("Fan/Control", payload, token, WriteCapability.Custom),
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
        GcuSnapshot before = Snapshot;
        if (before.Mode != 3)
            return new(GcuWriteStatus.Rejected, "风扇曲线只能在自定义模式中应用。");
        FanCurveTable? table = before.FanTable;
        if (table is null || !before.IsFanTableFresh(_time.GetUtcNow()) ||
            string.IsNullOrWhiteSpace(table.Name) ||
            table.Curve(target).Count != 16)
            return new(GcuWriteStatus.Rejected, "当前风扇表尚未读取或已经过期。");

        int pointCount = FanCurveDraft.EditablePointCount(table.Curve(target));
        if (pointCount == 0)
            return new(GcuWriteStatus.Rejected, "当前风扇表的有效温度槽结构不完整。");
        for (int i = pointCount; i < duties.Count; i++)
        {
            if (duties[i] != duties[pointCount - 1])
                return new(GcuWriteStatus.Rejected, "风扇曲线尾值必须与最后一个有效点相同。");
        }

        var payload = new Dictionary<string, object>
        {
            ["Action"] = "SET_FAN_SPEED_CURVE_SETTING",
            ["Name"] = table.Name,
            ["Type"] = target == FanCurveTarget.Cpu ? "CPU" : "GPU"
        };
        for (int i = 0; i < duties.Count; i++)
            payload[$"T{i}"] = duties[i].ToString(System.Globalization.CultureInfo.InvariantCulture);

        return await ConfirmWriteAsync(
            token => PublishWriteAsync("Fan/Control", JsonSerializer.Serialize(payload), token, WriteCapability.Custom),
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
            if (!_modeControlSupported)
                return new(ModeSwitchStatus.Rejected, "此服务协议尚未适配该性能模式。");
            if (!WritePermissionGranted)
                return new(ModeSwitchStatus.Rejected, "硬件写入尚未允许，请在更多设置中确认警告后开启。");
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
            await PublishWriteAsync("Fan/Control", $"{{\"Action\":\"{action}\",\"ProfileIndex\":0}}", linked.Token,
                WriteCapability.Mode).ConfigureAwait(false);
            await PublishWriteAsync("LCHWOC/Control", $"{{\"IsNormalRun\":{(int)mode}}}", linked.Token,
                WriteCapability.Mode).ConfigureAwait(false);
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
        catch (WritePermissionRevokedException)
        {
            return new(ModeSwitchStatus.Rejected, "硬件写入许可已关闭，后续设置未发送；实际模式请重新读取确认。");
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

    private sealed class WritePermissionRevokedException : Exception
    {
        public WritePermissionRevokedException() : base("硬件写入许可已关闭。") { }
    }

    private enum WriteCapability { Mode, Custom, Peripheral }

    /// <summary>
    /// Checks the session grant immediately before each side-effecting
    /// publish. The check and invocation are kept in one lock so revocation
    /// cannot interleave between them; subsequent commands in a multi-command
    /// operation are checked independently.
    /// </summary>
    private Task PublishWriteAsync(string topic, string payload, CancellationToken ct, WriteCapability capability)
    {
        lock (_sync)
        {
            bool capabilitySupported = capability switch
            {
                WriteCapability.Mode => _modeControlSupported,
                WriteCapability.Custom => _customControlSupported,
                WriteCapability.Peripheral => _peripheralControlSupported,
                _ => false
            };
            if (_disposed || !_transport.IsConnected || !_snapshot.IsConnected ||
                !_writePermissionGranted || !capabilitySupported)
                throw new WritePermissionRevokedException();
            return _transport.PublishAsync(topic, payload, ct);
        }
    }

    private async Task<GcuWriteResult> ConfirmWriteAsync(
        Func<CancellationToken, Task> send,
        Func<GcuSnapshot, bool> confirmed,
        bool requireCustomSettings = false,
        bool requireFanTable = false,
        string success = "服务已回读目标状态。",
        CancellationToken ct = default,
        bool queryOnly = false)
    {
        if (!await _operation.WaitAsync(0, ct).ConfigureAwait(false))
            return new(GcuWriteStatus.Busy, "上一项操作尚未完成。");
        try
        {
            if (_disposed || !_transport.IsConnected || !Snapshot.IsConnected)
                return new(GcuWriteStatus.Disconnected, "尚未连接厂商服务。");
            if (!queryOnly && !_customControlSupported)
                return new(GcuWriteStatus.Rejected, "此服务协议尚未适配自定义档位、功耗和风扇曲线写入。");
            if (!queryOnly && !WritePermissionGranted)
                return new(GcuWriteStatus.Rejected, "硬件写入尚未允许，请在更多设置中确认警告后开启。");

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
        catch (WritePermissionRevokedException)
        {
            return new(GcuWriteStatus.Rejected, "硬件写入许可已关闭，后续设置未发送；实际状态请重新读取确认。");
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
            _writePermissionGranted = false;
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
