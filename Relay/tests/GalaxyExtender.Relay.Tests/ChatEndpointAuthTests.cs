using System.Net;
using System.Net.Http.Json;

namespace GalaxyExtender.Relay.Tests;

public sealed class ChatEndpointAuthTests(RelayTestApp app) : IClassFixture<RelayTestApp>
{
    [Fact]
    public async Task Valid_key_is_accepted()
    {
        var response = await app.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/v1/chat", ChatBatches.Valid());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Missing_key_is_rejected()
    {
        var response = await app.CreateClient()
            .PostAsJsonAsync("/api/v1/chat", ChatBatches.Valid());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unrecognised_key_is_rejected()
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Relay-Key", "not-the-configured-key");

        var response = await client.PostAsJsonAsync("/api/v1/chat", ChatBatches.Valid());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A key that is a prefix of the real one must fail. Guards against a comparison that stops at
    /// the shorter length.
    /// </summary>
    [Fact]
    public async Task Key_prefix_is_rejected()
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Relay-Key", RelayTestApp.ValidKey[..10]);

        var response = await client.PostAsJsonAsync("/api/v1/chat", ChatBatches.Valid());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Key_is_case_sensitive()
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Relay-Key", RelayTestApp.ValidKey.ToUpperInvariant());

        var response = await client.PostAsJsonAsync("/api/v1/chat", ChatBatches.Valid());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Unauthenticated callers must not reach model binding — a malformed body behind a bad key
    /// should still be a 401, not a 400. Proves authentication runs before deserialisation.
    /// </summary>
    [Fact]
    public async Task Unauthenticated_malformed_body_returns_401_not_400()
    {
        var content = new StringContent("{ this is not json", System.Text.Encoding.UTF8, "application/json");

        var response = await app.CreateClient().PostAsync("/api/v1/chat", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Base_health_document_and_root_remain_unauthenticated()
    {
        var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }

    /// <summary>
    /// The outbound probe makes the shared host's IP call discord.com; anonymous hammering could
    /// get that IP rate-limited or banned by Discord. It is an operator tool, so it demands the
    /// key like everything else under /api.
    /// </summary>
    [Fact]
    public async Task Outbound_health_probe_requires_a_key()
    {
        var response = await app.CreateClient().GetAsync("/api/v1/health/outbound");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Fails closed: an unknown endpoint under /api must demand a key rather than 404-ing, so that
    /// adding an endpoint later cannot accidentally ship unauthenticated.
    /// </summary>
    [Fact]
    public async Task Unknown_api_route_requires_a_key()
    {
        var response = await app.CreateClient().GetAsync("/api/v1/something-new");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
