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
    private readonly Outbox _outbox;

    public OutboxTests()
    {
        var store = new FileStateStore(
            environment: null!, // never dereferenced when StateFilePath is set
            Microsoft.Extensions.Options.Options.Create(new RelayOptions { StateFilePath = _path }),
            NullLogger<FileStateStore>.Instance);

        var publisher = new DiscordPublisher(
            new SingleClientFactory(_handler),
            new StaticMonitor<DiscordOptions>(new DiscordOptions
            {
                WebhookUrl = "https://discord.test/api/webhooks/1/not-a-real-webhook"
            }),
            NullLogger<DiscordPublisher>.Instance);

        _outbox = new Outbox(store, publisher,
            new StaticMonitor<RelayOptions>(new RelayOptions { StateFilePath = _path }),
            NullLogger<Outbox>.Instance);
    }

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

    /// <summary>204s every request; the FIRST request additionally blocks until released, so a
    /// test can hold one drain mid-POST while another runs.</summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        private int _count;

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

            return new HttpResponseMessage(HttpStatusCode.NoContent);
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
