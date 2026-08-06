using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace GalaxyExtender.Relay.Tests;

/// <summary>
/// Production runs with <c>Relay:RequireHttps = true</c>; nothing else in the suite exercised
/// that middleware, so a regression would only have shown up on the live host.
/// </summary>
public sealed class RequireHttpsTests
{
    private sealed class HttpsRequiredApp : RelayTestApp
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Relay:RequireHttps"] = "true"
                });
            });
        }
    }

    [Fact]
    public async Task Plain_http_is_rejected_with_403()
    {
        using var app = new HttpsRequiredApp();

        // CreateClient defaults to an http:// base address.
        var response = await app.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/v1/chat", ChatBatches.Valid());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Https_request_passes_through()
    {
        using var app = new HttpsRequiredApp();

        var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        client.DefaultRequestHeaders.Add("X-Relay-Key", RelayTestApp.ValidKey);

        var response = await client.PostAsJsonAsync("/api/v1/chat", ChatBatches.Valid());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
