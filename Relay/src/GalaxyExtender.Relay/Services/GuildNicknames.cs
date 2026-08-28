using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using GalaxyExtender.Relay.Options;
using Microsoft.Extensions.Options;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// Discord user id -> the name that user goes by IN THIS GUILD (their per-server nickname), for
/// the author prefix on injected lines and for <c>&lt;@id&gt;</c> mentions inside them.
///
/// The nickname is not in the payload the relay already reads. A message's <c>author</c> object
/// carries only account-level identity (<c>global_name</c>, <c>username</c>); the <c>member</c>
/// object that holds <c>nick</c> rides along with GATEWAY events, and the relay polls REST. So the
/// nickname costs a separate <c>GET /guilds/{guild}/members/{user}</c> per author — which is why
/// everything here exists to make that call rare:
///
/// <list type="bullet">
/// <item>results are cached in memory for <see cref="RelayOptions.NicknameCacheMinutes"/>, and a
/// user with NO nickname is cached just as firmly as one with a nickname — otherwise the common
/// case would pay for a lookup on every message;</item>
/// <item>one round of lookups is capped at <see cref="MaxLookupsPerRound"/>, so a burst from many
/// distinct authors cannot turn one fetch into fifty Discord calls;</item>
/// <item>a failure that is not "this user is not a member" suppresses lookups entirely for
/// <see cref="FailureBackoff"/> — a bot without the permission, or a rate limit, must not produce
/// one warning per poll.</item>
/// </list>
///
/// Every failure path degrades to the SAME thing: no nickname for that id, and the caller falls
/// back to <c>global_name</c> exactly as it did before this class existed. Nothing here can stop a
/// Discord message reaching the guild room.
/// </summary>
public sealed class GuildNicknames(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<DiscordOptions> options,
    IOptionsMonitor<RelayOptions> relayOptions,
    IStateStore store,
    ILogger<GuildNicknames> logger)
{
    /// <summary>Most member lookups one round may spend. Ids past it keep their account name.</summary>
    private const int MaxLookupsPerRound = 10;

    /// <summary>
    /// How long a non-404 failure (missing permission, rate limit, Discord down) stops lookups.
    /// Long enough that a bot invited without access costs four calls an hour rather than one per
    /// poll, short enough that fixing the invite takes effect without a redeploy.
    /// </summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Cached entries before a prune. Bounds a cache whose keys are guild members — thousands of
    /// them over a long uptime, all of them tiny, none of them worth a real eviction policy.
    /// </summary>
    private const int MaxCacheEntries = 512;

    /// <summary>The empty answer, for callers with nothing to resolve.</summary>
    public static readonly IReadOnlyDictionary<string, string> None =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// id -> what Discord last said, with when that expires. A null <see cref="CacheEntry.Nick"/>
    /// is a real answer ("this member has no nickname"), not a miss.
    /// </summary>
    private sealed record CacheEntry(string? Nick, DateTimeOffset ExpiresUtc);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    private readonly object _gate = new();
    private DateTimeOffset _suppressedUntilUtc = DateTimeOffset.MinValue;

    /// <summary>
    /// Nicknames for the ids that have one. Ids absent from the result — no nickname, uncacheable,
    /// past the round's lookup cap, or looked up while suppressed — are simply not in it, and the
    /// caller keeps using the account name it already has. Never throws.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(IEnumerable<string?> userIds)
    {
        var discord = options.CurrentValue;

        // The channel id is what the guild is discovered FROM, so an unconfigured relay has no
        // question to ask here — the same shape of guard the reader and the scan open with.
        if (!discord.NicknamesEnabled ||
            string.IsNullOrWhiteSpace(discord.BotToken) ||
            string.IsNullOrWhiteSpace(discord.ChannelId))
        {
            return None;
        }

        var wanted = Distinct(userIds);

        if (wanted.Count == 0)
        {
            return None;
        }

        var now = DateTimeOffset.UtcNow;
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var id in wanted)
        {
            if (_cache.TryGetValue(id, out var cached) && cached.ExpiresUtc > now)
            {
                if (cached.Nick is { Length: > 0 } nick)
                {
                    resolved[id] = nick;
                }
            }
            else
            {
                missing.Add(id);
            }
        }

        if (missing.Count == 0 || Suppressed(now))
        {
            return resolved;
        }

        var guildId = await ResolveGuildIdAsync();

        if (guildId is null)
        {
            return resolved;
        }

        if (missing.Count > MaxLookupsPerRound)
        {
            // Said out loud rather than trimmed quietly: the visible effect is some authors
            // showing their account name in a busy minute, and that should be explicable.
            logger.LogInformation(
                "Nickname lookups capped at {Cap} this round; {Skipped} author(s) keep their Discord account name",
                MaxLookupsPerRound, missing.Count - MaxLookupsPerRound);

            missing.RemoveRange(MaxLookupsPerRound, missing.Count - MaxLookupsPerRound);
        }

        var ttl = TimeSpan.FromMinutes(Math.Max(1, relayOptions.CurrentValue.NicknameCacheMinutes));

        foreach (var id in missing)
        {
            var (answered, nick) = await ReadMemberAsync(guildId, id);

            if (!answered)
            {
                // Suppressed by the failure itself; the rest of this round would fail the same way.
                break;
            }

            Cache(id, nick, DateTimeOffset.UtcNow + ttl);

            if (nick is { Length: > 0 })
            {
                resolved[id] = nick;
            }
        }

        return resolved;
    }

    /// <summary>
    /// <paramref name="mentionNames"/> with every id that has a nickname replaced by it, so a
    /// <c>&lt;@id&gt;</c> inside a message reads the same as that person's own author prefix. The
    /// original map is returned untouched when there is nothing to replace.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> mentionNames,
        IReadOnlyDictionary<string, string> nicknames)
    {
        if (nicknames.Count == 0 || mentionNames.Count == 0 ||
            !mentionNames.Keys.Any(nicknames.ContainsKey))
        {
            return mentionNames;
        }

        var merged = new Dictionary<string, string>(mentionNames, StringComparer.Ordinal);

        foreach (var (id, nick) in nicknames)
        {
            if (merged.ContainsKey(id))
            {
                merged[id] = nick;
            }
        }

        return merged;
    }

    /// <summary>Every id one message can want a guild name for: its author, and everyone it mentions.</summary>
    public static IEnumerable<string?> IdsIn(DiscordMessage message) =>
        message.MentionNames.Keys.Prepend(message.AuthorId);

    /// <summary>Every id a page of messages wants, authors and mentions alike, in one sequence.</summary>
    public static IEnumerable<string?> IdsIn(IEnumerable<DiscordMessage> messages) =>
        messages.SelectMany(IdsIn);

    /// <summary>
    /// The guild the bridge channel belongs to: the operator's override, then the durable value
    /// discovered once from <c>GET /channels/{id}</c>, then a discovery attempt. Deliberately the
    /// same shape as the bot-identity discovery in <see cref="BotCommandScanner"/> — one call, in
    /// state forever after — because the answer cannot change without the channel id changing.
    /// </summary>
    private async Task<string?> ResolveGuildIdAsync()
    {
        var discord = options.CurrentValue;

        if (discord.ConfiguredGuildId is { } configured)
        {
            return configured;
        }

        if (store.Read(state => state.GuildId) is { Length: > 0 } known)
        {
            return known;
        }

        var (notFound, body) = await GetAsync($"channels/{discord.ChannelId}", "bridge channel read");

        if (body is null)
        {
            if (notFound)
            {
                // The channel id does not name anything the bot can see: a misconfiguration, not
                // a transient. Back off from it like any other failure rather than asking again
                // on every poll.
                logger.LogWarning("Bridge channel read returned 404; server nicknames stay off");
                Suppress();
            }

            return null;
        }

        string? guildId;

        try
        {
            using var document = JsonDocument.Parse(body);

            guildId = document.RootElement.ValueKind == JsonValueKind.Object
                ? DiscordMessageParser.ReadString(document.RootElement, "guild_id")
                : null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Bridge channel read returned unparseable JSON: {Error}", ex.Message);
            Suppress();
            return null;
        }

        if (string.IsNullOrWhiteSpace(guildId))
        {
            // A DM channel, or a shape we do not understand. Either way there are no nicknames to
            // read, and no amount of retrying will produce one.
            logger.LogWarning("Bridge channel read carried no guild id; server nicknames stay off");
            Suppress();
            return null;
        }

        store.Mutate<object?>(state =>
        {
            state.GuildId = guildId;
            return null;
        });

        logger.LogInformation("Bridge channel's guild id discovered: {GuildId}", guildId);

        return guildId;
    }

    /// <summary>
    /// One member read. Returns <c>(answered: true, nick)</c> when the question has an answer —
    /// <c>nick</c> null meaning "no nickname", which is cached as firmly as one that does, and a
    /// 404 counting as exactly that: the user is not a member of this guild any more, so asking
    /// again on their next message would buy nothing. Returns <c>(answered: false, null)</c> only
    /// when the lookup could not be made, having already suppressed the rest of the round.
    /// </summary>
    private async Task<(bool Answered, string? Nick)> ReadMemberAsync(string guildId, string userId)
    {
        var (notFound, body) = await GetAsync($"guilds/{guildId}/members/{userId}", "guild member read");

        if (body is null)
        {
            return (notFound, null);
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            return document.RootElement.ValueKind == JsonValueKind.Object
                ? (true, DiscordMessageParser.ReadString(document.RootElement, "nick"))
                : (true, null);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Guild member read returned unparseable JSON: {Error}", ex.Message);
            return (true, null);
        }
    }

    /// <summary>
    /// Bot-authenticated GET against the shared Discord client, as <c>(notFound, body)</c> — a null
    /// body IS the failure, and <c>notFound</c> says which kind it was. A 404 is reported separately and does NOT suppress, because it
    /// is the one status that is about the thing asked for rather than about the relay's access:
    /// on a member read it means that user is not in the guild, and the caller records that as
    /// "no nickname". Every other failure — and any transport error — suppresses the feature for
    /// <see cref="FailureBackoff"/>, having logged once.
    /// </summary>
    private async Task<(bool NotFound, string? Body)> GetAsync(string url, string what)
    {
        var client = httpClientFactory.CreateClient(DiscordReader.HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bot {options.CurrentValue.BotToken}");

        try
        {
            // Deliberately not the request token, exactly as the reader's own fetch: a caller
            // going away must not leave half a round of lookups behind.
            using var response = await client.SendAsync(request, CancellationToken.None);

            if (response.IsSuccessStatusCode)
            {
                return (false, await response.Content.ReadAsStringAsync(CancellationToken.None));
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogDebug("Discord {What} returned 404", what);
                return (true, null);
            }

            // 401/403 = bad token or a bot without access to the guild; 429 = rate limited. None
            // of them is about the id being looked up, so back off from all of them at once.
            logger.LogWarning(
                "Discord {What} failed with HTTP {Status}; server nicknames paused for {Minutes} minutes",
                what, (int)response.StatusCode, FailureBackoff.TotalMinutes);

            Suppress();

            return (false, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Discord {What} failed: {Error}", what, ex.Message);
            Suppress();
            return (false, null);
        }
    }

    private bool Suppressed(DateTimeOffset now)
    {
        lock (_gate)
        {
            return now < _suppressedUntilUtc;
        }
    }

    private void Suppress()
    {
        lock (_gate)
        {
            _suppressedUntilUtc = DateTimeOffset.UtcNow + FailureBackoff;
        }
    }

    /// <summary>
    /// Stores one answer, pruning first if the cache has grown past its bound: expired entries go,
    /// and if that was not enough the whole thing goes. Dropping a live entry costs one lookup.
    /// </summary>
    private void Cache(string id, string? nick, DateTimeOffset expiresUtc)
    {
        if (_cache.Count >= MaxCacheEntries)
        {
            var now = DateTimeOffset.UtcNow;

            foreach (var (key, entry) in _cache)
            {
                if (entry.ExpiresUtc <= now)
                {
                    _cache.TryRemove(key, out _);
                }
            }

            if (_cache.Count >= MaxCacheEntries)
            {
                _cache.Clear();
            }
        }

        _cache[id] = new CacheEntry(nick, expiresUtc);
    }

    /// <summary>Ids worth asking about: non-blank, deduplicated, order preserved.</summary>
    private static List<string> Distinct(IEnumerable<string?> userIds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ids = new List<string>();

        foreach (var id in userIds)
        {
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}
