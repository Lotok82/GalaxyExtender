using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GalaxyExtender.Relay.Tests;

public sealed class ChatEndpointValidationTests(RelayTestApp app) : IClassFixture<RelayTestApp>
{
    [Fact]
    public async Task Accepted_count_matches_line_count_and_forwarding_is_advertised_as_disabled()
    {
        var response = await app.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/v1/chat", ChatBatches.Valid("one", "two", "three"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("disabled", response.Headers.GetValues("X-Relay-Forwarding").Single());

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
