namespace BabyMonitarr.Backend.Talkback;

public enum TalkbackState
{
    Closed,
    Connecting,
    Open,
    Talking,
}

public sealed record TalkbackStartResult(bool Success, string? Reason, string? Message)
{
    public static readonly TalkbackStartResult Ok = new(true, null, null);
}

/// <summary>
/// Talkback for one room: owns the on-demand Foyer stream, the single active talker, and the
/// timers around them. Closed → Connecting → Open ⇄ Talking; Open closes after an idle period,
/// any stream failure goes back to Closed. Only talkback is affected by failures here.
/// </summary>
public sealed class TalkbackSession : IAsyncDisposable
{
    public static readonly TimeSpan StartBudget = TimeSpan.FromSeconds(9);
    public static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(60);
    // The Google Home web client extends every 240 s.
    public static readonly TimeSpan ExtendInterval = TimeSpan.FromSeconds(240);
    public static readonly TimeSpan NoAudioTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxTalk = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WatchdogTick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(5);

    private readonly IFoyerTalkbackStreamFactory _factory;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _pendingLock = new();

    private IFoyerTalkbackStream? _stream;
    private ITimer? _idleTimer;
    private ITimer? _extendTimer;
    private ITimer? _watchdog;
    private string? _pendingTalker;
    private CancellationTokenSource? _pendingStart;
    private bool _pendingCancelled;
    private long _talkStartedAt;
    private long _lastAudioAt;
    private double _volume;
    private bool _disposed;

    public TalkbackSession(
        TalkbackTarget target,
        IFoyerTalkbackStreamFactory factory,
        TimeProvider time,
        ILogger logger,
        double volume)
    {
        Target = target;
        _factory = factory;
        _time = time;
        _logger = logger;
        _volume = TalkbackGain.Clamp(volume);
    }

    public TalkbackTarget Target { get; }
    public TalkbackState State { get; private set; }

    /// <summary>The connection that is talking; null unless <see cref="State"/> is Talking.</summary>
    public string? Talker { get; private set; }

    /// <summary>Why the last attempt failed; cleared by the next successful start.</summary>
    public string? LastError { get; private set; }

    public double Volume => Volatile.Read(ref _volume);

    /// <summary>State, talker or last error changed.</summary>
    public event Action? Changed;

    public void SetVolume(double volume) => Volatile.Write(ref _volume, TalkbackGain.Clamp(volume));

    public Task<TalkbackStartResult> PrepareAsync() => StartCoreAsync(talker: null);

    public Task<TalkbackStartResult> StartAsync(string connectionId) => StartCoreAsync(connectionId);

    private async Task<TalkbackStartResult> StartCoreAsync(string? talker)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return new TalkbackStartResult(false, TalkbackReasons.CameraError, "Talkback was reset.");

            if (State == TalkbackState.Talking)
            {
                return talker == null || talker == Talker
                    ? TalkbackStartResult.Ok
                    : new TalkbackStartResult(false, TalkbackReasons.Busy, "Someone else is talking in this room.");
            }

            using var budget = new CancellationTokenSource(StartBudget, _time);
            lock (_pendingLock)
            {
                _pendingTalker = talker;
                _pendingStart = budget;
                _pendingCancelled = false;
            }

            bool startSent = false;
            try
            {
                if (_stream == null)
                {
                    SetState(TalkbackState.Connecting);
                    var stream = await _factory.OpenAsync(Target, budget.Token);
                    _stream = stream;
                    stream.Closed += reason => _ = OnStreamClosedAsync(stream, reason);
                    _extendTimer = _time.CreateTimer(_ => _ = ExtendAsync(), null, ExtendInterval, ExtendInterval);
                    SetState(TalkbackState.Open);
                }

                CancelIdleTimer();

                if (talker == null)
                {
                    ScheduleIdleClose();
                    return TalkbackStartResult.Ok;
                }

                startSent = true;
                await _stream.StartTalkbackAsync(budget.Token);
            }
            catch (Exception) when (IsCancelledByStop())
            {
                _logger.LogInformation("Talkback start for room {RoomId} cancelled by the talker", Target.RoomId);
                if (startSent) await TryStopTalkbackAsync();
                if (_stream != null)
                {
                    SetState(TalkbackState.Open);
                    ScheduleIdleClose();
                }
                else
                {
                    SetState(TalkbackState.Closed);
                }
                return new TalkbackStartResult(false, TalkbackReasons.Cancelled, "Talking was cancelled.");
            }
            catch (Exception ex)
            {
                var (reason, message) = ex switch
                {
                    TalkbackUnavailableException u => (u.Reason, u.Message),
                    OperationCanceledException => (TalkbackReasons.CameraError, "The camera did not respond in time."),
                    _ => (TalkbackReasons.CameraError, $"Talkback failed: {ex.Message}"),
                };
                _logger.LogWarning("Talkback start for room {RoomId} failed ({Reason}): {Message}", Target.RoomId, reason, message);

                LastError = message;
                await CloseStreamAsync(sendStop: startSent);
                SetState(TalkbackState.Closed);
                return new TalkbackStartResult(false, reason, message);
            }
            finally
            {
                lock (_pendingLock)
                {
                    _pendingTalker = null;
                    _pendingStart = null;
                }
            }

            long now = _time.GetTimestamp();
            Talker = talker;
            LastError = null;
            Volatile.Write(ref _talkStartedAt, now);
            Volatile.Write(ref _lastAudioAt, now);
            _watchdog = _time.CreateTimer(_ => CheckTalker(), null, WatchdogTick, WatchdogTick);
            SetState(TalkbackState.Talking);
            _logger.LogInformation("Talkback started in room {RoomId}", Target.RoomId);
            return TalkbackStartResult.Ok;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops <paramref name="connectionId"/> talking, or cancels its start still in progress.
    /// Does nothing for any other connection.
    /// </summary>
    public async Task StopAsync(string connectionId, string why)
    {
        lock (_pendingLock)
        {
            if (_pendingTalker == connectionId && _pendingStart != null)
            {
                _pendingCancelled = true;
                _pendingStart.Cancel();
            }
        }

        await _gate.WaitAsync();
        try
        {
            if (State != TalkbackState.Talking || Talker != connectionId) return;

            _logger.LogInformation("Talkback stopped in room {RoomId}: {Why}", Target.RoomId, why);
            StopWatchdog();
            Talker = null;

            if (!await TryStopTalkbackAsync())
            {
                await CloseStreamAsync(sendStop: false);
                SetState(TalkbackState.Closed);
                return;
            }

            SetState(TalkbackState.Open);
            ScheduleIdleClose();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forwards one uplink audio packet (interleaved stereo 48 kHz PCM) when it comes from the talker.</summary>
    public void OnAudio(string connectionId, short[] pcm)
    {
        if (State != TalkbackState.Talking || Talker != connectionId) return;

        Volatile.Write(ref _lastAudioAt, _time.GetTimestamp());
        TalkbackGain.Apply(pcm, Volume);
        _stream?.SendPcm(pcm);
    }

    private void CheckTalker()
    {
        string? talker = Talker;
        if (State != TalkbackState.Talking || talker == null) return;

        if (_time.GetElapsedTime(Volatile.Read(ref _lastAudioAt)) > NoAudioTimeout)
        {
            _ = StopAsync(talker, $"no audio from the talker for {NoAudioTimeout.TotalSeconds:0} s");
        }
        else if (_time.GetElapsedTime(Volatile.Read(ref _talkStartedAt)) > MaxTalk)
        {
            _ = StopAsync(talker, $"talked for the {MaxTalk.TotalMinutes:0}-minute maximum");
        }
    }

    private async Task ExtendAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_stream == null) return;
            using var budget = new CancellationTokenSource(StopBudget, _time);
            await _stream.ExtendAsync(budget.Token);
            _logger.LogDebug("Talkback stream for room {RoomId} extended", Target.RoomId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Extending the talkback stream for room {RoomId} failed: {Error}", Target.RoomId, ex.Message);
            LastError = "The camera stream expired.";
            await CloseStreamAsync(sendStop: Talker != null);
            SetState(TalkbackState.Closed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CloseIfIdleAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State != TalkbackState.Open) return;
            _logger.LogInformation("Closing idle talkback stream for room {RoomId}", Target.RoomId);
            await CloseStreamAsync(sendStop: false);
            SetState(TalkbackState.Closed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task OnStreamClosedAsync(IFoyerTalkbackStream stream, string reason)
    {
        await _gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(_stream, stream)) return;
            _logger.LogWarning("Talkback stream for room {RoomId} ended: {Reason}", Target.RoomId, reason);
            LastError = $"The camera stream ended ({reason}).";
            await CloseStreamAsync(sendStop: false);
            SetState(TalkbackState.Closed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Caller holds the gate. Returns false when STOP could not be delivered.</summary>
    private async Task<bool> TryStopTalkbackAsync()
    {
        if (_stream == null) return false;
        try
        {
            using var budget = new CancellationTokenSource(StopBudget, _time);
            await _stream.StopTalkbackAsync(budget.Token);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("SendTalkback STOP failed for room {RoomId}: {Error}", Target.RoomId, ex.Message);
            return false;
        }
    }

    /// <summary>Caller holds the gate.</summary>
    private async Task CloseStreamAsync(bool sendStop)
    {
        StopWatchdog();
        CancelIdleTimer();
        _extendTimer?.Dispose();
        _extendTimer = null;
        Talker = null;

        var stream = _stream;
        if (stream == null) return;

        if (sendStop) await TryStopTalkbackAsync();
        _stream = null;
        try
        {
            await stream.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Disposing talkback stream for room {RoomId} failed: {Error}", Target.RoomId, ex.Message);
        }
    }

    private bool IsCancelledByStop()
    {
        lock (_pendingLock) return _pendingCancelled;
    }

    private void ScheduleIdleClose()
    {
        CancelIdleTimer();
        _idleTimer = _time.CreateTimer(_ => _ = CloseIfIdleAsync(), null, IdleClose, Timeout.InfiniteTimeSpan);
    }

    private void CancelIdleTimer()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
    }

    private void StopWatchdog()
    {
        _watchdog?.Dispose();
        _watchdog = null;
    }

    private void SetState(TalkbackState state)
    {
        State = state;
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Talkback status listener failed for room {RoomId}", Target.RoomId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_pendingLock)
        {
            _pendingCancelled = true;
            _pendingStart?.Cancel();
        }

        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await CloseStreamAsync(sendStop: Talker != null);
            State = TalkbackState.Closed;
        }
        finally
        {
            _gate.Release();
        }
    }
}
