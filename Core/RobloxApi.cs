using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ServerHop.Core;

// ── models ──────────────────────────────────────────────────────────────────

public sealed record GameInfo(long UniverseId, long PlaceId, string Name, int Playing, string? IconUrl);

public sealed record ServerInfo(string Id, int Playing, int MaxPlayers, int Ping)
{
    public double Fill => MaxPlayers <= 0 ? 0 : (double)Playing / MaxPlayers;
}

public sealed record ServerPage(IReadOnlyList<ServerInfo> Servers, string? NextCursor);

// ── api ─────────────────────────────────────────────────────────────────────

/// <summary>Thin client for the public Roblox endpoints SERVERHOP needs:
/// place→universe resolve, game metadata, icon and the public server list.
/// All calls are anonymous (no cookies/tokens) and defensively retried,
/// because games.roblox.com rate-limits bursts from a single IP.</summary>
public static class RobloxApi
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    /// <summary>Exposed for --selftest: the identity we present to Roblox.</summary>
    public static string HttpUserAgent { get; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    public static string HttpReferrer { get; } = "https://www.roblox.com/";

    static RobloxApi()
    {
        // games.roblox.com 400s on generic clients; these mirror a normal browser.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(HttpUserAgent);
        Http.DefaultRequestHeaders.Referrer = new Uri(HttpReferrer);
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
    }

    /// <summary>Extracts a place id from whatever the user pasted:
    /// full game URL, share link, query string or a bare number.
    /// Returns null when nothing usable was found.</summary>
    public static long? ParsePlaceId(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return null;

        // bare number
        if (long.TryParse(input, out var bare) && bare > 0) return bare;

        // /games/123456789/Slug  (also matches /games?placeId= via query branch below)
        var m = System.Text.RegularExpressions.Regex.Match(
            input, @"(?:roblox\.com)?/games/(\d{6,})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && long.TryParse(m.Groups[1].Value, out var fromPath) && fromPath > 0) return fromPath;

        // ?placeId=123456789 (any URL, incl. roblox://experiences/start?placeId=)
        m = System.Text.RegularExpressions.Regex.Match(
            input, @"[?&]placeId=(\d{6,})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && long.TryParse(m.Groups[1].Value, out var fromQuery) && fromQuery > 0) return fromQuery;

        return null;
    }

    /// <summary>placeId → universeId (games.roblox.com addresses universes).</summary>
    public static async Task<long> ResolveUniverseAsync(long placeId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var json = await GetStringAsync(
            $"https://apis.roblox.com/universes/v1/places/{placeId}/universe", cts.Token).ConfigureAwait(false);
        var node = JsonNode(json);
        if (node is null || !node.Value.TryGetProperty("universeId", out var uid) ||
            uid.ValueKind != JsonValueKind.Number)
            throw new ApiException($"place {placeId} did not resolve to a universe (bad or expired id?)");
        return uid.GetInt64();
    }

    /// <summary>universeId → display name + live player count (icon optional).</summary>
    public static async Task<GameInfo> GetGameAsync(long universeId, long placeId, bool fetchIcon = true)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var json = await GetStringAsync(
            $"https://games.roblox.com/v1/games?universeIds={universeId}", cts.Token).ConfigureAwait(false);
        var root = JsonNode(json);
        JsonElement? first = null;
        if (root is not null && root.Value.TryGetProperty("data", out var arr) &&
            arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            first = arr[0];
        if (first is null)
            throw new ApiException($"no game metadata for universe {universeId}");

        var name = first.Value.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
        var playing = first.Value.TryGetProperty("playing", out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32() : 0;

        string? icon = null;
        if (fetchIcon)
        {
            try
            {
                using var iconCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var ij = await GetStringAsync(
                    $"https://thumbnails.roblox.com/v1/games/icons?universeIds={universeId}" +
                    "&size=256x256&format=Png&isCircular=false", iconCts.Token).ConfigureAwait(false);
                var iroot = JsonNode(ij);
                if (iroot is not null && iroot.Value.TryGetProperty("data", out var iarr) &&
                    iarr.ValueKind == JsonValueKind.Array && iarr.GetArrayLength() > 0)
                {
                    var item = iarr[0];
                    if (item.TryGetProperty("state", out var st) && st.GetString() == "Completed" &&
                        item.TryGetProperty("imageUrl", out var url))
                        icon = url.GetString();
                }
            }
            catch { /* icon is cosmetic — never fail the load over it */ }
        }

        return new GameInfo(universeId, placeId, name, playing, icon);
    }

    /// <summary>One page (≤100) of public servers, cheapest sort first.</summary>
    public static async Task<ServerPage> GetServersAsync(long placeId, string? cursor = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var url = $"https://games.roblox.com/v1/games/{placeId}/servers/Public" +
                  $"?sortOrder=Asc&limit=100";
        if (!string.IsNullOrEmpty(cursor))
            url += $"&cursor={Uri.EscapeDataString(cursor)}";

        var json = await GetStringAsync(url, cts.Token).ConfigureAwait(false);
        var root = JsonNode(json);
        if (root is null) throw new ApiException("server list: empty response");

        var list = new List<ServerInfo>();
        if (root.Value.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in arr.EnumerateArray())
            {
                var id = s.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                list.Add(new ServerInfo(
                    Id: id,
                    Playing: s.TryGetProperty("playing", out var pl) && pl.ValueKind == JsonValueKind.Number ? pl.GetInt32() : 0,
                    MaxPlayers: s.TryGetProperty("maxPlayers", out var mp) && mp.ValueKind == JsonValueKind.Number ? mp.GetInt32() : 0,
                    Ping: s.TryGetProperty("ping", out var pg) && pg.ValueKind == JsonValueKind.Number ? pg.GetInt32() : 0));
            }
        }

        var next = root.Value.TryGetProperty("nextPageCursor", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() : null;

        return new ServerPage(list, next);
    }

    /// <summary>The documented deep link that launches the client straight into a specific instance.</summary>
    public static string JoinUrl(long placeId, string gameIdInstance) =>
        $"roblox://experiences/start?placeId={placeId}&gameInstanceId={gameIdInstance}";

    // ── plumbing ─────────────────────────────────────────────────────────────

    public sealed class ApiException : Exception
    {
        public ApiException(string message) : base(message) { }
    }

    private static async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        // games.roblox.com answers 400 with an empty error body during rate limiting,
        // so retry transient failures with growing pauses before surfacing anything.
        // (ConfigureAwait(false) throughout: --selftest blocks the caller on these tasks.)
        string? lastProblem = null;
        var attempts = 0;

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            attempts = attempt;
            ct.ThrowIfCancellationRequested();
            var retryable = true;

            try
            {
                using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode && body.Length > 0)
                    return body;

                lastProblem = resp.StatusCode switch
                {
                    HttpStatusCode.BadRequest when body.Length == 0 => "rate limited (burst)",
                    HttpStatusCode.TooManyRequests => "rate limited (429)",
                    HttpStatusCode.NotFound => "not found (404)",
                    _ when (int)resp.StatusCode >= 500 => $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}",
                    _ when resp.IsSuccessStatusCode => "empty response",
                    _ => $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}"
                };

                // only throttling and server-side errors are worth another attempt
                retryable = resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.TooManyRequests
                            || (int)resp.StatusCode >= 500;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                lastProblem = "timed out"; // HttpClient's own 20s limit
            }
            catch (HttpRequestException ex)
            {
                lastProblem = ex.Message;
            }

            if (!retryable) break;
            if (attempt < 4)
                await Task.Delay(TimeSpan.FromSeconds(attempt * 1.5), ct).ConfigureAwait(false);
        }

        throw new ApiException(attempts > 1
            ? $"{lastProblem} — after {attempts} attempts"
            : $"{lastProblem}");
    }

    /// <summary>Raw bytes for a cosmetic asset (game icon). Returns null on any failure.</summary>
    public static async Task<byte[]?> DownloadAsync(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            return null; // icons are never worth an error dialog
        }
    }

    private static JsonElement? JsonNode(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
