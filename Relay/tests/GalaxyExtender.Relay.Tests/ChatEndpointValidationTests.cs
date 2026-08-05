using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GalaxyExtender.Relay.Tests;

public sealed class ChatEndpointValidationTests(RelayTestApp app) : IClassFixture<RelayTestApp>
{
    [Fact]
    public async Task Accepted_count_matches_line_count_and_forwarding_is_advertised_as_enabled()
    {
        var response = await app.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/v1/chat", ChatBatches.Valid("one", "two", "three"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("enabled", response.Headers.GetValues("X-Relay-Forwarding").Single());

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetProperty("accepted").GetInt32());
        Assert.Equal(0, body.GetProperty("deduped").GetInt32());
        Assert.Equal(0, body.GetProperty("queued").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("retryAfterMs").ValueKind);
    }

    [Theory]
    [InlineData("batchId")]
    [InlineData("client")]
    [InlineData("lines")]
    public async Task Missing_required_top_level_field_is_rejected(string omitted)
    {
        var payload = new Dictionary<string, object?>
        {
            ["batchId"] = Guid.NewGuid().ToString(),
            ["client"] = new { id = "kaelen" },
            ["lines"] = new[] { ChatBatches.Line("hello") }
        };
        payload.Remove(omitted);

        var response = await app.CreateAuthenticatedClient().PostAsJsonAsync("/api/v1/chat", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(omitted, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Non_guid_batch_id_is_rejected()
    {
        var payload = new
        {
            batchId = "not-a-guid",
            client = new { id = "kaelen" },
            lines = new[] { ChatBatches.Line("hello") }
        };

        var response = await app.CreateAuthenticatedClient().PostAsJsonAsync("/api/v1/chat", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("batchId", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Empty_line_list_is_rejected()
    {
        var response = await app.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/v1/chat", ChatBatches.WithLines([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Too_many_lines_is_rejected()
    {
        var lines = Enumerable.Range(0, 51).Select(i => ChatBatches.Line($"line {i}")).ToArray();

        var response = await app.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/v1/chat", ChatBatches.WithLines(lines));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("50", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Exactly_the_line_limit_is_accepted()
    {
        var lines = Enumerable.Range(0, 50).Select(i => ChatBatches.Line($"line {i}")).ToArray();

        var response = await app.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/v1/chat", ChatBatches.WithLines(lines));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Over_long_line_is_rejected_with_the_offending_index()
    {
        var lines = new[]
        {
            ChatBatches.Line("fine"),
            ChatBatches.Line(new string('x', 513))
        };

        var response = await app.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/v1/chat", ChatBatches.WithLines(lines));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("lines[1].text", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Blank_line_text_is_rejected()
    {
        var response = await app.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/v1/chat", ChatBatches.WithLines([ChatBatches.Line("   ")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// System.Text.Json deserialises a `null` array element into a null ChatLine. That must be a
    /// 400 naming the element, not a 500 — the contract says 5xx is retried with the same batchId,
    /// which would turn one malformed batch into a poison message.
    /// </summary>
    [Fact]
    public async Task Null_line_element_is_rejected_with_400_not_500()
    {
        var response = await app.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/v1/chat", ChatBatches.WithLines([null!]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("lines[0]", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The extension maps control characters to spaces before sending, so they only ever arrive
    /// from a buggy or hostile client. Rejecting them keeps forged newlines out of the relay's
    /// log lines and out of the Phase 3 Discord messages.
    /// </summary>
    [Theory]
    [InlineData("hello\nworld")]
    [InlineData("hello\u0001world")]
    [InlineData("hello\u007Fworld")]
    public async Task Control_characters_in_line_text_are_rejected(string text)
    {
        var response = await app.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/v1/chat", ChatBatches.WithLines([ChatBatches.Line(text)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("lines[0].text", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("client.id", "kaelen\n[INF] forged log line")]
    [InlineData("client.character", "Kaelen\r\nforged")]
    [InlineData("client.galaxy", "Basilisk\tforged")]
    public async Task Control_characters_in_identity_fields_are_rejected(string field, string value)
    {
        var payload = new
        {
            batchId = Guid.NewGuid().ToString(),
            client = new
            {
                id = field == "client.id" ? value : "kaelen",
                character = field == "client.character" ? value : "Kaelen",
                galaxy = field == "client.galaxy" ? value : "Basilisk"
            },
            lines = new[] { ChatBatches.Line("hello") }
        };

        var response = await app.CreateAuthenticatedClient().PostAsJsonAsync("/api/v1/chat", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// occurrence is 1-based by definition — it counts occurrences including the current one — so 0
    /// signals a client-side bug in the counter and must not be silently accepted.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Occurrence_below_one_is_rejected(int occurrence)
    {
        var response = await app.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/v1/chat",
                ChatBatches.WithLines([ChatBatches.Line("hello", occurrence)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("occurrence", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Missing_occurrence_is_rejected()
    {
        var lines = new[] { new { text = "hello", clientSeq = 1 } };

        var response = await app.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/v1/chat", ChatBatches.WithLines(lines.Cast<object>().ToArray()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("occurrence", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Higher occurrence values are legitimate: a genuine repeat of the same line.</summary>
    [Fact]
    public async Task Repeat_occurrence_is_accepted()
    {
        var lines = new[]
        {
            ChatBatches.Line("lol", occurrence: 1),
            ChatBatches.Line("lol", occurrence: 2)
        };

        var response = await app.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/v1/chat", ChatBatches.WithLines(lines));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("accepted").GetInt32());
    }

    [Fact]
    public async Task Malformed_json_from_an_authenticated_client_is_a_400()
    {
        var content = new StringContent("{ nope", System.Text.Encoding.UTF8, "application/json");

        var response = await app.CreateAuthenticatedClient().PostAsync("/api/v1/chat", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
