using BabyMonitarr.Backend.Talkback;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BabyMonitarr.Backend.Tests.Talkback;

public class TalkbackSessionTests
{
    private sealed class FakeStream : IFoyerTalkbackStream
    {
        public List<string> Calls { get; } = new();
        public List<short[]> Sent { get; } = new();
        public Exception? ExtendError { get; set; }
        public bool Disposed { get; private set; }

        public event Action<string>? Closed;

        public void RaiseClosed(string reason) => Closed?.Invoke(reason);

        public Task StartTalkbackAsync(CancellationToken ct)
        {
            Calls.Add("start");
            return Task.CompletedTask;
        }

        public Task StopTalkbackAsync(CancellationToken ct)
        {
            Calls.Add("stop");
            return Task.CompletedTask;
        }

        public Task ExtendAsync(CancellationToken ct)
        {
            Calls.Add("extend");
            return ExtendError == null ? Task.CompletedTask : Task.FromException(ExtendError);
        }

        public void SendPcm(short[] pcm) => Sent.Add((short[])pcm.Clone());

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeFactory : IFoyerTalkbackStreamFactory
    {
        public TaskCompletionSource? Gate { get; set; }
        public Exception? Error { get; set; }
        public List<FakeStream> Opened { get; } = new();
        public FakeStream Last => Opened[^1];

        public async Task<IFoyerTalkbackStream> OpenAsync(TalkbackTarget target, CancellationToken ct)
        {
            if (Gate != null) await Gate.Task.WaitAsync(ct);
            if (Error != null) throw Error;
            var stream = new FakeStream();
            Opened.Add(stream);
            return stream;
        }
    }

    private readonly FakeTimeProvider _time = new();
    private readonly FakeFactory _factory = new();
    private readonly TalkbackSession _session;

    public TalkbackSessionTests()
    {
        _session = new TalkbackSession(
            new TalkbackTarget(1, "DEVICE_AAA", "uuid"), _factory, _time, NullLogger.Instance, volume: 1.0);
    }

    [Fact]
    public async Task Start_opens_the_stream_and_starts_the_speaker()
    {
        var result = await _session.StartAsync("a");

        Assert.True(result.Success);
        Assert.Equal(TalkbackState.Talking, _session.State);
        Assert.Equal("a", _session.Talker);
        Assert.Equal(new[] { "start" }, _factory.Last.Calls);
    }

    [Fact]
    public async Task A_second_talker_is_busy_and_a_repeat_start_is_idempotent()
    {
        await _session.StartAsync("a");

        var other = await _session.StartAsync("b");
        var again = await _session.StartAsync("a");

        Assert.Equal(TalkbackReasons.Busy, other.Reason);
        Assert.True(again.Success);
        Assert.Equal(new[] { "start" }, _factory.Last.Calls);
        Assert.Single(_factory.Opened);
    }

    [Fact]
    public async Task Stop_keeps_the_stream_open_until_the_idle_period_ends()
    {
        await _session.StartAsync("a");

        await _session.StopAsync("a", "released");
        Assert.Equal(TalkbackState.Open, _session.State);
        Assert.Null(_session.Talker);
        Assert.Equal(new[] { "start", "stop" }, _factory.Last.Calls);

        _time.Advance(TalkbackSession.IdleClose - TimeSpan.FromSeconds(1));
        Assert.Equal(TalkbackState.Open, _session.State);

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TalkbackState.Closed, _session.State);
        Assert.True(_factory.Last.Disposed);
    }

    [Fact]
    public async Task A_quick_repress_reuses_the_open_stream()
    {
        await _session.StartAsync("a");
        await _session.StopAsync("a", "released");

        _time.Advance(TimeSpan.FromSeconds(30));
        await _session.StartAsync("a");

        Assert.Single(_factory.Opened);
        Assert.Equal(TalkbackState.Talking, _session.State);
        Assert.Equal(new[] { "start", "stop", "start" }, _factory.Last.Calls);
    }

    [Fact]
    public async Task Stop_from_another_connection_does_nothing()
    {
        await _session.StartAsync("a");

        await _session.StopAsync("b", "released");

        Assert.Equal(TalkbackState.Talking, _session.State);
        Assert.Equal(new[] { "start" }, _factory.Last.Calls);
    }

    [Fact]
    public async Task Release_while_connecting_cancels_before_the_speaker_goes_live()
    {
        _factory.Gate = new TaskCompletionSource();

        var start = _session.StartAsync("a");
        Assert.Equal(TalkbackState.Connecting, _session.State);
        await _session.StopAsync("a", "released");
        var result = await start;

        Assert.False(result.Success);
        Assert.Equal(TalkbackReasons.Cancelled, result.Reason);
        Assert.Equal(TalkbackState.Closed, _session.State);
        Assert.Empty(_factory.Opened);
    }

    [Fact]
    public async Task Audio_is_forwarded_only_from_the_talker_with_the_volume_applied()
    {
        _session.SetVolume(0.5);
        _session.OnAudio("a", new short[] { 10000 });
        await _session.StartAsync("a");

        _session.OnAudio("b", new short[] { 10000 });
        _session.OnAudio("a", new short[] { 10000, -8000 });

        var sent = Assert.Single(_factory.Last.Sent);
        Assert.Equal(new short[] { 5000, -4000 }, sent);
    }

    [Fact]
    public async Task A_talker_that_stops_sending_audio_is_stopped()
    {
        await _session.StartAsync("a");

        _time.Advance(TimeSpan.FromSeconds(5));
        _session.OnAudio("a", new short[] { 1 });
        _time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(TalkbackState.Talking, _session.State);

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TalkbackState.Open, _session.State);
        Assert.Contains("stop", _factory.Last.Calls);
    }

    [Fact]
    public async Task Talking_is_capped()
    {
        await _session.StartAsync("a");

        TalkFor(TalkbackSession.MaxTalk + TimeSpan.FromSeconds(2));

        Assert.Equal(TalkbackState.Open, _session.State);
    }

    [Fact]
    public async Task A_failed_open_reports_the_reason_and_the_next_start_retries()
    {
        _factory.Error = new TalkbackUnavailableException(TalkbackReasons.CameraError, "camera offline");

        var failed = await _session.StartAsync("a");

        Assert.Equal(TalkbackReasons.CameraError, failed.Reason);
        Assert.Equal("camera offline", _session.LastError);
        Assert.Equal(TalkbackState.Closed, _session.State);

        _factory.Error = null;
        var retried = await _session.StartAsync("a");
        Assert.True(retried.Success);
        Assert.Null(_session.LastError);
    }

    [Fact]
    public async Task A_camera_that_never_answers_times_out()
    {
        _factory.Gate = new TaskCompletionSource();

        var start = _session.StartAsync("a");
        _time.Advance(TalkbackSession.StartBudget + TimeSpan.FromSeconds(1));
        var result = await start;

        Assert.Equal(TalkbackReasons.CameraError, result.Reason);
        Assert.Equal(TalkbackState.Closed, _session.State);
    }

    [Fact]
    public async Task A_stream_that_drops_ends_talking()
    {
        await _session.StartAsync("a");

        _factory.Last.RaiseClosed("camera stream failed");

        Assert.Equal(TalkbackState.Closed, _session.State);
        Assert.Null(_session.Talker);
        Assert.Contains("camera stream failed", _session.LastError);
        Assert.True(_factory.Last.Disposed);
    }

    private void TalkFor(TimeSpan duration)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += TimeSpan.FromSeconds(1))
        {
            _session.OnAudio("a", new short[] { 1 });
            _time.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task A_long_talk_extends_the_stream()
    {
        await _session.StartAsync("a");

        TalkFor(TalkbackSession.ExtendInterval + TimeSpan.FromSeconds(1));

        Assert.Contains("extend", _factory.Last.Calls);
        Assert.Equal(TalkbackState.Talking, _session.State);
    }

    [Fact]
    public async Task A_refused_extend_stops_talking_and_closes_the_stream()
    {
        await _session.StartAsync("a");
        var stream = _factory.Last;
        stream.ExtendError = new InvalidOperationException("not extended");

        TalkFor(TalkbackSession.ExtendInterval + TimeSpan.FromSeconds(1));

        Assert.Equal(TalkbackState.Closed, _session.State);
        Assert.Equal(new[] { "start", "extend", "stop" }, stream.Calls);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Prepare_opens_without_talking_and_idles_closed()
    {
        var result = await _session.PrepareAsync();

        Assert.True(result.Success);
        Assert.Equal(TalkbackState.Open, _session.State);
        Assert.Empty(_factory.Last.Calls);

        _time.Advance(TalkbackSession.IdleClose + TimeSpan.FromSeconds(1));
        Assert.Equal(TalkbackState.Closed, _session.State);
    }

    [Fact]
    public async Task State_changes_are_reported()
    {
        var states = new List<TalkbackState>();
        _session.Changed += () => states.Add(_session.State);

        await _session.StartAsync("a");
        await _session.StopAsync("a", "released");

        Assert.Equal(new[] { TalkbackState.Connecting, TalkbackState.Open, TalkbackState.Talking, TalkbackState.Open }, states);
    }
}
