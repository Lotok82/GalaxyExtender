using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// Phase 0 smoke tests. These assert the app boots and the host probe reports something usable —
/// nothing about relay behaviour yet, which arrives with Phase 1 onwards.
/// </summary>
public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Health_returns_ok_and_reports_process_identity()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());

        var process = body.GetProperty("process");
        Assert.True(process.GetProperty("id").GetInt32() > 0);
        Assert.True(process.GetProperty("uptimeSeconds").GetInt64() >= 0);
        Assert.StartsWith(".NET", process.GetProperty("framework").GetString());
    }

    [Fact]
    public async Task Health_reports_app_data_as_writable_locally()
    {
        var client = _factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/v1/health");

        var storage = body.GetProperty("storage");
        Assert.True(storage.GetProperty("appDataWritable").GetBoolean(),
            $"App_Data was not writable: {storage.GetProperty("appDataError")}");
        Assert.True(storage.GetProperty("appDataReadBackOk").GetBoolean());
    }

    [Fact]
    public async Task Root_returns_a_plain_text_marker()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("/api/v1/health", text);
    }
}
