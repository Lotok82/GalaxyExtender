using GalaxyExtender.Relay.Contracts;
using GalaxyExtender.Relay.Services;

namespace GalaxyExtender.Relay.Endpoints;

/// <summary>
/// Stage 2 (Discord → game) work queue. <c>GET /api/v1/messages</c> is a CONSUME: a 200 claims
/// the returned messages for the polling key+client until the redelivery timeout — see the
/// "Stage 2" section of README.md for the pinned contract.
///
/// Currently the R1 stub: authenticates and rate-limits exactly like /chat, validates the
/// contract, and always answers an empty queue with <c>X-Relay-Stage2: disabled</c> — so the
/// extension's whole poll path can be built and harness-tested before the Discord read (R3)
/// exists. Nothing here may change /chat behaviour.
/// </summary>
public static class MessagesEndpoints
{
    /// <summary>
    /// Advertises whether the Discord read path is live. A header rather than a response field
    /// for the same reason as X-Relay-Forwarding: flipping it on must not be a breaking change
    /// to the payload the extension parses.
    /// </summary>
    private const string Stage2Header = "X-Relay-Stage2";

    public static void MapMessagesEndpoints(this IEndpointRouteBuilder app)
    {
        // Authentication comes from ApiKeyAuthenticationMiddleware on the /api prefix, like /chat.
        app.MapGet("/api/v1/messages", (string? client, HttpContext http) =>
            {
                if (!TryValidateClient(client, out var errors))
                {
                    return Results.ValidationProblem(errors);
                }

                // Contract: an unconfigured Stage 2 is the ordinary idle case, not an error —
                // 200 + empty + "disabled", never 503, so the poll loop needs no special casing.
                http.Response.Headers[Stage2Header] = "disabled";

                return Results.Ok(new MessagesResponse([], Dropped: 0));
            })
            .RequireRateLimiting(ChatEndpoints.RateLimitPolicy);
    }

    /// <summary>
    /// `client` attributes claims for redelivery accounting and logging (the GET has no body to
    /// carry /chat's client.id, so it travels as a query parameter). Same rules as client.id;
    /// it is not authentication.
    /// </summary>
    private static bool TryValidateClient(string? client, out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(client))
        {
            errors["client"] = ["Required."];
        }
        else if (client.Length > ChatBatchValidator.MaxIdentifierLength)
        {
            errors["client"] = [$"Must be {ChatBatchValidator.MaxIdentifierLength} characters or fewer."];
        }
        else if (ChatBatchValidator.ContainsControlCharacters(client))
        {
            errors["client"] = [ChatBatchValidator.ControlCharacterError];
        }

        return errors.Count == 0;
    }
}
