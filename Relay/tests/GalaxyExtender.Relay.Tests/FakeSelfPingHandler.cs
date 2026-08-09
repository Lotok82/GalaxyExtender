using System.Net;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// Stands in for the relay's own <c>/health</c> on the far end of the background ticker's self-ping.
///
/// Stubbed rather than left to reach the network for two reasons. The obvious one is that a test
/// asserting the ticker survives a failed ping must not depend on how the machine's firewall
/// happens to treat a dead port — refuse and the failure is instant, drop and it costs the client's
/// full timeout. The one that matters more is that the interesting failures cannot be produced over
/// a socket at all: the exception that could take the whole host down is thrown by HttpClient
/// BEFORE any connection is attempted, so scripting it is the only way to test it.
/// </summary>
public sealed class FakeSelfPingHandler : HttpMessageHandler
{
    private readonly object _lock = new();
    private readonly List<string> _requestUris = [];
    private Func<HttpResponseMessage>? _behaviour;

    public IReadOnlyList<string> RequestUris
    {
        get
        {
            lock (_lock)
            {
                return _requestUris.ToArray();
            }
        }
    }

    public int RequestCount
    {
        get
        {
            lock (_lock)
            {
                return _requestUris.Count;
            }
        }
    }

    /// <summary>Answers every ping with this status until told otherwise.</summary>
    public void AlwaysRespond(HttpStatusCode statusCode)
    {
        lock (_lock)
        {
            _behaviour = () => new HttpResponseMessage(statusCode);
        }
    }

    /// <summary>
    /// Throws this on every ping. The factory runs per request because an exception instance
    /// carries a stack trace once thrown.
    /// </summary>
    public void AlwaysThrow(Func<Exception> exception)
    {
        lock (_lock)
        {
            _behaviour = () => throw exception();
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Func<HttpResponseMessage> behaviour;

        lock (_lock)
        {
            _requestUris.Add(request.RequestUri?.ToString() ?? string.Empty);
            behaviour = _behaviour ?? (() => new HttpResponseMessage(HttpStatusCode.OK));
        }

        // Outside the lock: a scripted throw must not leave it held.
        return Task.FromResult(behaviour());
    }
}
