using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// The R1 Stage 2 stub: GET /api/v1/messages must behave exactly like /chat for authentication
/// and rate limiting, validate the `client` parameter, and answer an empty queue with
/// X-Relay-Stage2: disabled — the contract the extension's poll path is built against.
/// </summary>
public sealed class MessagesEndpointTests(RelayTestApp app) : IClassFixture<RelayTestApp>
{
    private const string Endpoint = "/api/v1/messages?client=kaelen";

    [Fact]
    public async Task Valid_key_gets_empty_queue_with_stage2_disabled_header()
    {
        var response = await app.CreateAuthenticatedClient().GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("disabled", Assert.Single(response.Headers.GetValues("X-Relay-Stage2")));
    }

    /// <summary>
    /// Pins the wire shape, not the DTO: the C++ extension parses these exact property names, so
    /// the assertion goes through raw JSON rather than round-tripping a type that cannot be wrong.
    /// </summary>
    [Fact]
    public async Task Response_wire_shape_is_messages_array_plus_dropped_count()
    {
        var response = await app.CreateAuthenticatedClient().GetAsync(Endpoint);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(JsonValueKind.Array, body.RootElement.GetProperty("messages").ValueKind);
        Assert.Empty(body.RootElement.GetProperty("messages").EnumerateArray());
        Assert.Equal(0, body.RootElement.GetProperty("dropped").GetInt32());
    }

    [Fact]
    public async Task Missing_key_is_rejected()
    {
        var response = await app.CreateClient().GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unrecognised_key_is_rejected()
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Relay-Key", "not-the-configured-key");

        var response = await client.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Authentication must win over validation: an unauthenticated caller learns nothing about
    /// the contract, mirroring /chat's 401-before-400 behaviour.
    /// </summary>
    [Fact]
    public async Task Unauthenticated_malformed_request_returns_401_not_400()
    {
        var response = await app.CreateClient().GetAsync("/api/v1/messages");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Missing_client_parameter_is_a_400_naming_the_field()
    {
        var response = await app.CreateAuthenticatedClient().GetAsync("/api/v1/messages");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("client", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Blank_client_parameter_is_rejected()
    {
        var response = await app.CreateAuthenticatedClient().GetAsync("/api/v1/messages?client=%20%20");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Overlong_client_parameter_is_rejected()
    {
        var overlong = new string('k', 65);

        var response = await app.CreateAuthenticatedClient().GetAsync($"/api/v1/messages?client={overlong}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The client id reaches log lines and, later, claim records — forged control characters are
    /// rejected here for the same reason /chat rejects them everywhere.
    /// </summary>
    [Fact]
    public async Task Client_parameter_with_control_characters_is_rejected()
    {
        var response = await app.CreateAuthenticatedClient().GetAsync("/api/v1/messages?client=kae%0Alen");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The contract has no cursor: the relay owns queue position under claim semantics. A client
    /// still sending `after` (or anything else) must be ignored, not rejected, so dropping the
    /// parameter later is never a breaking change.
    /// </summary>
    [Fact]
    public async Task Unknown_query_parameters_are_ignored()
    {
        var response = await app.CreateAuthenticatedClient()
            .GetAsync("/api/v1/messages?client=kaelen&after=1234&limit=50");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary>
/// Rate-limit behaviour for the poll endpoint: same policy, same per-key partition as /chat, so
/// one client's polls and posts draw from a single permit budget.
/// </summary>
public sealed class MessagesRateLimitTests
{
    private const int PermitLimit = 3;

    private sealed class ThrottledApp : RelayTestApp
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Relay:RateLimitPermitsPerMinute"] = PermitLimit.ToString()
                });
            });
        }
    }

    [Fact]
    public async Task Exceeding_the_limit_returns_429_with_retry_after()
    {
        using var app = new ThrottledApp();
        var client = app.CreateAuthenticatedClient();

        for (var i = 0; i < PermitLimit; i++)
        {
            var allowed = await client.GetAsync("/api/v1/messages?client=kaelen");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        var throttled = await client.GetAsync("/api/v1/messages?client=kaelen");

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.NotNull(throttled.Headers.RetryAfter);
    }

    /// <summary>
    /// Polls and chat POSTs must share one bucket per key — the 120/min budget in RelayOptions is
    /// sized for both together, and separate buckets would double a client's effective allowance.
    /// </summary>
    [Fact]
    public async Task Polls_and_chat_posts_share_the_same_permit_bucket()
    {
        using var app = new ThrottledApp();
        var client = app.CreateAuthenticatedClient();

        for (var i = 0; i < PermitLimit; i++)
        {
            var allowed = await client.PostAsJsonAsync("/api/v1/chat", ChatBatches.Valid($"line {i}"));
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        var throttled = await client.GetAsync("/api/v1/messages?client=kaelen");

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
    }
}
