using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GalaxyExtender.Relay.Contracts;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// The presence ping (R11): the only signal that answers "who has the extension running", since a
/// chat batch only arrives when somebody talks and the Stage 2 poll is gated client-side on things
/// that have nothing to do with being online.
/// </summary>
public sealed class PresenceTests
{
    private static PresenceRequest Ping(
        string id, string? character = null, string? galaxy = null) =>
        new() { Client = new ChatClient { Id = id, Character = character, Galaxy = galaxy } };

    private static async Task<PresenceResponse> PingAsync(HttpClient client, PresenceRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/v1/presence", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<PresenceResponse>())!;
    }

    [Fact]
    public async Task A_ping_reports_the_client_as_online()
    {
        using var app = new RelayTestApp();
        var client = app.CreateAuthenticatedClient();

        var body = await PingAsync(client, Ping("kaelen", "Kaelen", "Basilisk"));

        Assert.Equal(1, body.Online);
        Assert.Equal(1, body.Known);
        Assert.Equal(180, body.OnlineWindowSeconds);
    }

    [Fact]
    public async Task Distinct_clients_are_counted_separately_and_repeats_are_not()
    {
        using var app = new RelayTestApp();
        var client = app.CreateAuthenticatedClient();

        await PingAsync(client, Ping("kaelen", "Kaelen"));
        await PingAsync(client, Ping("tarn", "Tarn"));
        var body = await PingAsync(client, Ping("kaelen", "Kaelen"));

        Assert.Equal(2, body.Online);
        Assert.Equal(2, body.Known);
    }

    [Fact]
    public async Task Client_ids_differing_only_in_case_are_the_same_client()
    {
        using var app = new RelayTestApp();
        var client = app.CreateAuthenticatedClient();

        await PingAsync(client, Ping("Kaelen-PC"));
        var body = await PingAsync(client, Ping("kaelen-pc"));

        Assert.Equal(1, body.Known);
    }

    [Fact]
    public async Task A_chat_batch_registers_presence_without_a_ping()
    {
        // Mixed-version safety: a client on a build with no presence ping must still show up.
        using var app = new RelayTestApp();
        var client = app.CreateAuthenticatedClient();

        var chat = await client.PostAsJsonAsync("/api/v1/chat",
            ChatBatches.WithLines([ChatBatches.Line("[GuildChat] Kaelen: evening all")]));
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);

        // A second client's ping reports both.
        var body = await PingAsync(client, Ping("tarn"));

        Assert.Equal(2, body.Online);
    }

    [Fact]
    public async Task A_stage2_poll_registers_presence_even_while_stage2_is_off()
    {
        // The disabled poll cadence is 60 s, so this is the one signal an idle client on an
        // un-configured bridge still sends. It must count.
        using var app = new RelayTestApp();
        var client = app.CreateAuthenticatedClient();

        var poll = await client.GetAsync("/api/v1/messages?client=kaelen");
        Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        Assert.Equal("disabled", poll.Headers.GetValues("X-Relay-Stage2").Single());

        var body = await PingAsync(client, Ping("tarn"));

        Assert.Equal(2, body.Online);
    }

    [Fact]
    public async Task A_ping_without_a_client_id_is_a_400_naming_the_field()
    {
        using var app = new RelayTestApp();
        var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/v1/presence",
            new PresenceRequest { Client = new ChatClient { Character = "Kaelen" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("client.id", out _));
    }

    [Fact]
    public async Task A_ping_with_no_body_is_a_400_rather_than_a_500()
    {
        using var app = new RelayTestApp();
        var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync<PresenceRequest?>("/api/v1/presence", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Presence_requires_the_relay_key()
    {
        using var app = new RelayTestApp();
        var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/presence", Ping("kaelen"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Presence_survives_a_restart_because_it_is_durable_state()
    {
        // Same state file, fresh host: an app pool that idle-stops overnight must not report an
        // empty guild the next morning.
        var statePath = Path.Combine(Path.GetTempPath(), $"relay-presence-{Guid.NewGuid():N}.json");

        try
        {
            using (var first = new ConfiguredRelayTestApp(new Dictionary<string, string?>
                   {
                       ["Relay:StateFilePath"] = statePath
                   }))
            {
                await PingAsync(first.CreateAuthenticatedClient(), Ping("kaelen", "Kaelen"));
            }

            using var second = new ConfiguredRelayTestApp(new Dictionary<string, string?>
            {
                ["Relay:StateFilePath"] = statePath
            });

            var body = await PingAsync(second.CreateAuthenticatedClient(), Ping("tarn"));

            Assert.Equal(2, body.Known);
        }
        finally
        {
            File.Delete(statePath);
        }
    }

    [Fact]
    public async Task An_upgraded_client_replaces_its_old_entry_rather_than_adding_one()
    {
        // Extension rollouts are manual and staggered, so the relay meets both id forms: the old
        // one registers first, and the machine switches to "<label>-<fingerprint>" whenever its
        // owner takes the new DLL. Counting both would inflate the denominator in "1 of 2
        // connected" by one per upgraded install, for the whole retention window.
        using var app = new RelayTestApp();
        var client = app.CreateAuthenticatedClient();

        await PingAsync(client, Ping("kaelen", "Kaelen"));
        var body = await PingAsync(client, Ping("kaelen-a1b2c3d4e5f60718", "Kaelen"));

        Assert.Equal(1, body.Known);
        Assert.Equal(1, body.Online);
    }

    [Fact]
    public async Task An_id_that_merely_starts_with_another_is_still_a_separate_client()
    {
        // The supersede rule matches on the "-" boundary the fingerprint suffix always introduces,
        // so two people who happened to pick similar ini labels stay two clients.
        using var app = new RelayTestApp();
        var client = app.CreateAuthenticatedClient();

        await PingAsync(client, Ping("kaelen"));
        var body = await PingAsync(client, Ping("kaelenpc"));

        Assert.Equal(2, body.Known);
    }

    [Fact]
    public async Task A_client_outside_the_window_is_known_but_not_online()
    {
        using var app = new ConfiguredRelayTestApp(new Dictionary<string, string?>
        {
            // Everything is instantly stale: the ping registers, then counts as gone.
            ["Relay:PresenceOnlineWindowSeconds"] = "0"
        });

        var client = app.CreateAuthenticatedClient();

        var body = await PingAsync(client, Ping("kaelen", "Kaelen"));

        Assert.Equal(0, body.Online);
        Assert.Equal(1, body.Known);
    }
}
