using System.Net;
using GalaxyExtender.Relay.Options;
using GalaxyExtender.Relay.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// The outbox's claim discipline, tested directly: drains run at the start of every request, so
/// two concurrent requests over a non-empty outbox is the NORMAL case right after a 429 burst —
/// exactly when a double-post would be most visible in the channel.
/// </summary>
public sealed class OutboxTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"relay-outbox-test-{Guid.NewGuid():N}.json");

    private readonly GatedHandler _handler = new();
    private readonly IStateStore _store;
    private readonly DiscordPublisher _publisher;
    private readonly Outbox _outbox;

    public OutboxTests()
    {
        _store = new FileStateStore(
            environment: null!, // never dereferenced when StateFilePath is set
            Microsoft.Extensions.Options.Options.Create(new RelayOptions { StateFilePath = _path }),
            NullLogger<FileStateStore>.Instance);

        _publisher = new DiscordPublisher(
            new SingleClientFactory(_handler),
            new StaticMonitor<DiscordOptions>(new DiscordOptions
            {
                WebhookUrl = "https://discord.test/api/webhooks/1/not-a-real-webhook"
            }),
            NullLogger<DiscordPublisher>.Instance);

        _outbox = OutboxWith(new RelayOptions { StateFilePath = _path });
    }

    /// <summary>Another outbox over the SAME store and webhook, with its own tunables.</summary>
    private Outbox OutboxWith(RelayOptions options) =>
        new(_store, _publisher, new StaticMonitor<RelayOptions>(options), NullLogger<Outbox>.Instance);

    [Fact]
    public async Task Concurrent_drains_do_not_double_post_the_same_entry()
    {
        _outbox.Park("""{"probe":1}""", 1, TimeSpan.Zero);

        // First drain claims the entry and is now held mid-POST.
        var first = _outbox.DrainAsync(CancellationToken.None);
        await _handler.FirstRequestArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // A concurrent drain must see the claim and leave the entry alone.
        await _outbox.DrainAsync(CancellationToken.None);
        Assert.Equal(1, _handler.Count);

        _handler.Release.TrySetResult();
        await first;

        Assert.Equal(1, _handler.Count);
        Assert.Equal(0, _outbox.Depth);
    }

    [Fact]
    public async Task A_cancelled_caller_neither_posts_nor_burns_an_attempt()
    {
        _outbox.Park("""{"probe":1}""", 1, TimeSpan.Zero);
        _handler.Release.TrySetResult();

        await _outbox.DrainAsync(new CancellationToken(canceled: true));

        Assert.Equal(0, _handler.Count);
        Assert.Equal(1, _outbox.Depth);
    }

    /// <summary>
    /// An entry the outbox GIVES UP on hands back the alert ping window it was carrying.
    ///
    /// Claiming the window when the payload is built is right while the payload is still on its way
    /// — a mention parked by a 429 arrives late, and a late ping beats no ping. It stops being right
    /// the moment the entry is discarded: nobody was notified, so keeping the window spent would
    /// silence the next alert, which is the one that still has an audience.
    /// </summary>
    [Fact]
    public async Task A_dropped_entry_hands_back_the_alert_ping_window()
    {
        var stamp = DateTimeOffset.UtcNow;
        Stamp(stamp);

        _handler.Status = HttpStatusCode.BadRequest;
        _handler.Release.TrySetResult();

        var outbox = OutboxWith(new RelayOptions { StateFilePath = _path, OutboxMaxAttempts = 1 });
        outbox.Park("""{"probe":1}""", 1, TimeSpan.Zero, stamp);
        await outbox.DrainAsync(CancellationToken.None);

        Assert.Equal(0, outbox.Depth);
        Assert.Null(_store.Read(state => state.LastAlertPingUtc));
    }

    /// <summary>
    /// ...but only the window it actually claimed. If a later alert has since pinged successfully,
    /// that ping WAS heard and it owns the window now; releasing it would notify the role twice
    /// inside one interval, which is the thing the throttle exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_dropped_entry_leaves_a_newer_ping_window_alone()
    {
        var newer = DateTimeOffset.UtcNow;
        Stamp(newer);

        _handler.Status = HttpStatusCode.BadRequest;
        _handler.Release.TrySetResult();

        var outbox = OutboxWith(new RelayOptions { StateFilePath = _path, OutboxMaxAttempts = 1 });
        outbox.Park("""{"probe":1}""", 1, TimeSpan.Zero, newer.AddMinutes(-5));
        await outbox.DrainAsync(CancellationToken.None);

        Assert.Equal(0, outbox.Depth);
        Assert.Equal(newer, _store.Read(state => state.LastAlertPingUtc));
    }

    /// <summary>
    /// The other way an entry dies undelivered: shoved out of a full outbox by newer traffic. Same
    /// reasoning, and easy to miss because this drop happens on the PARK path rather than the drain.
    /// </summary>
    [Fact]
    public void An_entry_pushed_out_of_a_full_outbox_hands_back_its_ping_window()
    {
        var stamp = DateTimeOffset.UtcNow;
        Stamp(stamp);

        var outbox = OutboxWith(new RelayOptions { StateFilePath = _path, OutboxMaxEntries = 1 });
        outbox.Park("""{"alert":1}""", 1, TimeSpan.Zero, stamp);
        outbox.Park("""{"chat":1}""", 1, TimeSpan.Zero);

        Assert.Equal(1, outbox.Depth);
        Assert.Null(_store.Read(state => state.LastAlertPingUtc));
    }

    private void Stamp(DateTimeOffset when) => _store.Mutate<object?>(state =>
    {
        state.LastAlertPingUtc = when;
        return null;
    });

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Answers every request with <see cref="Status"/> (204 by default); the FIRST request
    /// additionally blocks until released, so a test can hold one drain mid-POST while another
    /// runs.</summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        private int _count;

        /// <summary>Set to a failure status to exercise the retry and drop paths.</summary>
        public HttpStatusCode Status { get; set; } = HttpStatusCode.NoContent;

        public TaskCompletionSource FirstRequestArrived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Count => Volatile.Read(ref _count);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _count) == 1)
            {
                FirstRequestArrived.TrySetResult();
                await Release.Task;
            }

            return new HttpResponseMessage(Status);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StaticMonitor<T>(T value) : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
