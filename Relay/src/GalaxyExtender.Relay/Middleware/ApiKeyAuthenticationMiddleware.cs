using GalaxyExtender.Relay.Services;

namespace GalaxyExtender.Relay.Middleware;

/// <summary>
/// Requires a valid <c>X-Relay-Key</c> on everything under <c>/api/</c> except the base health
/// document. Health sub-endpoints (<c>/health/outbound</c>) stay behind the key: they perform
/// outbound network calls on the caller's behalf, which makes them operator tools, not
/// uptime-ping targets.
///
/// Implemented as path-prefix middleware rather than a per-endpoint filter on purpose: it
/// **fails closed**. Any endpoint added under the prefix later is protected automatically, whereas a
/// per-endpoint filter is protected only if somebody remembers to attach it. It also runs before
/// model binding, so unauthenticated callers cannot make us deserialise their JSON.
/// </summary>
public sealed class ApiKeyAuthenticationMiddleware(
    RequestDelegate next,
    ApiKeyValidator validator,
    ILogger<ApiKeyAuthenticationMiddleware> logger)
{
    /// <summary>Key under which the matched label is published for downstream logging.</summary>
    public const string KeyLabelItem = "RelayKeyLabel";

    private static readonly PathString ProtectedPrefix = new("/api");
    private static readonly PathString HealthPrefix = new("/api/v1/health");

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        var requiresKey = path.StartsWithSegments(ProtectedPrefix) && !IsPublicHealthPath(path);

        if (!requiresKey)
        {
            await next(context);
            return;
        }

        var result = validator.Validate(context.Request);

        if (!result.IsValid)
        {
            var presented = context.Request.Headers[ApiKeyValidator.HeaderName].ToString();

            logger.LogWarning(
                "Rejected {Method} {Path}: {Reason}. keyFingerprint={Fingerprint}",
                context.Request.Method,
                SanitizeForLog(path),
                string.IsNullOrEmpty(presented) ? "no key presented" : "unrecognised key",
                string.IsNullOrEmpty(presented) ? "none" : ApiKeyValidator.Fingerprint(presented));

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        context.Items[KeyLabelItem] = result.Label;

        await next(context);
    }

    /// <summary>
    /// Only the base health document is public — no secrets, and it is what an uptime pinger hits.
    /// A trailing slash is tolerated; anything deeper requires the key.
    /// </summary>
    private static bool IsPublicHealthPath(PathString path) =>
        path.StartsWithSegments(HealthPrefix, out var remaining) &&
        (!remaining.HasValue || remaining.Value == "/");

    /// <summary>
    /// The request path is attacker-controlled and the file sink renders it verbatim, so strip
    /// anything that could forge a log line before it is recorded.
    /// </summary>
    private static string SanitizeForLog(PathString path)
    {
        var value = path.Value ?? string.Empty;

        return value.Any(c => c < 0x20 || c == 0x7F)
            ? string.Concat(value.Select(c => c < 0x20 || c == 0x7F ? '?' : c))
            : value;
    }
}
