using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Dota2MmrCollector;

internal sealed record PracticeOpenDotaEnrichment(
    IReadOnlyDictionary<ulong, PracticeMatchData> Matches,
    IReadOnlyList<ulong> RelevantMatchIds,
    IReadOnlyList<uint> KnownTeammateAccountIds,
    PracticeOpenDotaSummary Summary);

internal static class PracticeOpenDotaApiKey
{
    public static string? Resolve(EphemeralSecret? explicitSecret)
    {
        if (explicitSecret is { IsCleared: false } && !string.IsNullOrWhiteSpace(explicitSecret.Value))
        {
            return explicitSecret.Value.Trim();
        }

        var environmentValue = Environment.GetEnvironmentVariable("OPENDOTA_API_KEY");
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue.Trim();
        }

        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, ".env"),
            Path.Combine(AppContext.BaseDirectory, ".env"),
        }.Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var path in candidates)
        {
            var value = TryReadDotEnv(path, "OPENDOTA_API_KEY");
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return PracticeOpenDotaApiKeyStore.TryLoad();
    }

    private static string? TryReadDotEnv(string path, string name)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0
                    || !string.Equals(line[..separator].Trim(), name, StringComparison.Ordinal))
                {
                    continue;
                }

                var value = line[(separator + 1)..].Trim();
                if (value.Length >= 2
                    && ((value[0] == '"' && value[^1] == '"')
                        || (value[0] == '\'' && value[^1] == '\'')))
                {
                    value = value[1..^1];
                }
                return value;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
}

internal sealed class PracticeOpenDotaClient : IDisposable
{
    private const int HistoryPageLimit = 10_000;
    private const int MaxRetries = 3;
    private readonly HttpClient httpClient;
    private readonly string? apiKey;
    private readonly Action<string> progress;
    private DateTime lastRequestAtUtc;

    public PracticeOpenDotaClient(string? apiKey, Action<string>? progress = null)
    {
        this.apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        this.progress = progress ?? (_ => { });
        httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.opendota.com/api/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(60),
        };
        httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Dota2MmrReconstructor", "0.5"));
    }

    public bool ApiKeyUsed => apiKey is not null;

    public async Task<PracticeOpenDotaEnrichment> EnrichAsync(
        uint authenticatedAccountId,
        PracticeReportSettings settings,
        IReadOnlyCollection<ulong> gcSeedMatchIds,
        IReadOnlyDictionary<ulong, PracticeMatchData> cachedMatches,
        CancellationToken cancellationToken = default)
    {
        var matches = cachedMatches.ToDictionary(pair => pair.Key, pair => pair.Value);
        var errors = new List<string>();
        var detailsRequested = 0;
        var matchesAdded = 0;

        foreach (var matchId in gcSeedMatchIds.OrderBy(id => id))
        {
            if (matches.TryGetValue(matchId, out var cached)
                && cached.HasUsableRoster
                && cached.WinnerTeam is 2 or 3)
            {
                continue;
            }

            detailsRequested++;
            progress($"OpenDota 正在补 GC 种子比赛详情：{matchId}。");
            var fetched = await TryGetMatchAsync(matchId, errors, cancellationToken);
            if (fetched is null)
            {
                continue;
            }

            var wasKnown = matches.ContainsKey(matchId);
            Upsert(matches, fetched);
            if (!wasKnown)
            {
                matchesAdded++;
            }
        }

        var teammates = DiscoverKnownTeammates(
            authenticatedAccountId,
            gcSeedMatchIds,
            matches);
        var queriedAccounts = teammates
            .Append(authenticatedAccountId)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var candidateMatchIds = new HashSet<ulong>(gcSeedMatchIds);

        foreach (var playerAccountId in queriedAccounts)
        {
            foreach (var cached in matches.Values.Where(match =>
                match.LobbyType == PracticeMatchNormalizer.PracticeLobbyType
                && settings.Contains(match.StartTimeUnix)
                && match.Players.Any(player => player.AccountId == playerAccountId)))
            {
                candidateMatchIds.Add(cached.MatchId);
            }
            progress(
                $"OpenDota 正在查询账号 {playerAccountId} 的同时间段练习比赛 " +
                $"({Array.IndexOf(queriedAccounts, playerAccountId) + 1}/{queriedAccounts.Length})。");
            var playerMatches = await TryGetPlayerPracticeMatchesAsync(
                playerAccountId,
                settings,
                errors,
                cancellationToken);
            foreach (var matchId in playerMatches)
            {
                candidateMatchIds.Add(matchId);
            }
        }

        var detailsCandidates = candidateMatchIds
            .Where(matchId =>
                !matches.TryGetValue(matchId, out var cached)
                || !cached.HasUsableRoster
                || cached.WinnerTeam is not (2 or 3))
            .OrderBy(matchId => matchId)
            .ToArray();
        for (var index = 0; index < detailsCandidates.Length; index++)
        {
            var matchId = detailsCandidates[index];
            detailsRequested++;
            progress(
                $"OpenDota 正在下载补充比赛详情 {index + 1}/{detailsCandidates.Length}：{matchId}。");
            var fetched = await TryGetMatchAsync(matchId, errors, cancellationToken);
            if (fetched is null)
            {
                continue;
            }

            var wasKnown = matches.ContainsKey(matchId);
            Upsert(matches, fetched);
            if (!wasKnown)
            {
                matchesAdded++;
            }
        }

        var summary = new PracticeOpenDotaSummary(
            ApiKeyUsed,
            errors.Count == 0,
            queriedAccounts.Length,
            candidateMatchIds.Count,
            detailsRequested,
            matchesAdded,
            errors);
        return new PracticeOpenDotaEnrichment(
            matches,
            candidateMatchIds.OrderBy(id => id).ToArray(),
            teammates,
            summary);
    }

    public void Dispose() => httpClient.Dispose();

    internal static IReadOnlyList<uint> DiscoverKnownTeammates(
        uint authenticatedAccountId,
        IReadOnlyCollection<ulong> gcSeedMatchIds,
        IReadOnlyDictionary<ulong, PracticeMatchData> matches)
    {
        var teammates = new HashSet<uint>();
        foreach (var matchId in gcSeedMatchIds)
        {
            if (!matches.TryGetValue(matchId, out var match))
            {
                continue;
            }

            var authenticatedPlayer = match.Players.FirstOrDefault(player =>
                player.AccountId == authenticatedAccountId && player.Team is 2 or 3);
            if (authenticatedPlayer?.Team is not int team)
            {
                continue;
            }

            foreach (var teammate in match.Players.Where(player =>
                player.Team == team
                && player.AccountId is not null
                && player.AccountId != authenticatedAccountId
                && !player.IsBot))
            {
                teammates.Add(teammate.AccountId!.Value);
            }
        }

        return teammates.OrderBy(id => id).ToArray();
    }

    internal static PracticeMatchData ParseMatch(JsonElement root)
    {
        var matchId = RequiredUInt64(root, "match_id");
        var startTime = RequiredInt64(root, "start_time");
        var lobbyType = TryUInt32(root, "lobby_type") ?? 0;
        int? winnerTeam = null;
        var outcome = "unknown";
        if (TryBoolean(root, "radiant_win") is bool radiantWon)
        {
            winnerTeam = radiantWon
                ? PracticeMatchNormalizer.RadiantTeam
                : PracticeMatchNormalizer.DireTeam;
            outcome = radiantWon ? "radiant_win" : "dire_win";
        }

        var players = new List<PracticePlayerData>();
        if (root.TryGetProperty("players", out var playerArray)
            && playerArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var player in playerArray.EnumerateArray())
            {
                var accountId = TryUInt32(player, "account_id");
                if (accountId == 0)
                {
                    accountId = null;
                }
                var slot = TryInt32(player, "player_slot");
                int? team;
                if (TryBoolean(player, "isRadiant") is bool isRadiant)
                {
                    team = isRadiant
                        ? PracticeMatchNormalizer.RadiantTeam
                        : PracticeMatchNormalizer.DireTeam;
                }
                else
                {
                    team = PracticeMatchNormalizer.ResolveTeam(null, slot);
                }

                players.Add(new PracticePlayerData(
                    accountId,
                    TryString(player, "personaname"),
                    team,
                    slot,
                    TryInt32(player, "hero_id") ?? 0,
                    TryBoolean(player, "is_bot") == true));
            }
        }

        return new PracticeMatchData(
            matchId,
            startTime,
            lobbyType,
            winnerTeam,
            outcome,
            players,
            ["OpenDota"]);
    }

    private async Task<IReadOnlyList<ulong>> TryGetPlayerPracticeMatchesAsync(
        uint accountId,
        PracticeReportSettings settings,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetPlayerPracticeMatchesAsync(accountId, settings, cancellationToken);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException
            or InvalidDataException)
        {
            errors.Add(
                $"账号 {accountId} 的 OpenDota 比赛列表请求失败（{exception.GetType().Name}）。");
            return [];
        }
    }

    private async Task<IReadOnlyList<ulong>> GetPlayerPracticeMatchesAsync(
        uint accountId,
        PracticeReportSettings settings,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<ulong>();
        ulong? lessThanMatchId = null;
        var days = Math.Max(
            1,
            (int)Math.Ceiling((DateTimeOffset.UtcNow - settings.StartUtc).TotalDays) + 1);

        while (true)
        {
            var parameters = new Dictionary<string, string>
            {
                ["lobby_type"] = PracticeMatchNormalizer.PracticeLobbyType.ToString(
                    CultureInfo.InvariantCulture),
                ["significant"] = "0",
                ["limit"] = HistoryPageLimit.ToString(CultureInfo.InvariantCulture),
                ["date"] = days.ToString(CultureInfo.InvariantCulture),
                ["project"] = "match_id,start_time,lobby_type",
            };
            if (lessThanMatchId is not null)
            {
                parameters["less_than_match_id"] = lessThanMatchId.Value.ToString(
                    CultureInfo.InvariantCulture);
            }

            using var document = await GetJsonAsync(
                $"players/{accountId.ToString(CultureInfo.InvariantCulture)}/matches",
                parameters,
                $"player {accountId} match history",
                cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("OpenDota player match history was not an array.");
            }

            var page = document.RootElement.EnumerateArray().ToArray();
            var oldestStart = long.MaxValue;
            ulong? oldestMatchId = null;
            foreach (var match in page)
            {
                var matchId = TryUInt64(match, "match_id");
                var startTime = TryInt64(match, "start_time");
                var lobbyType = TryUInt32(match, "lobby_type");
                if (matchId is null || startTime is null)
                {
                    continue;
                }

                oldestStart = Math.Min(oldestStart, startTime.Value);
                oldestMatchId = oldestMatchId is null
                    ? matchId
                    : Math.Min(oldestMatchId.Value, matchId.Value);
                if (lobbyType == PracticeMatchNormalizer.PracticeLobbyType
                    && settings.Contains(startTime.Value))
                {
                    result.Add(matchId.Value);
                }
            }

            if (page.Length < HistoryPageLimit
                || oldestStart < settings.StartUtc.ToUnixTimeSeconds()
                || oldestMatchId is null
                || oldestMatchId == lessThanMatchId)
            {
                break;
            }

            lessThanMatchId = oldestMatchId;
        }

        return result.OrderBy(id => id).ToArray();
    }

    private async Task<PracticeMatchData?> TryGetMatchAsync(
        ulong matchId,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await GetJsonAsync(
                $"matches/{matchId.ToString(CultureInfo.InvariantCulture)}",
                new Dictionary<string, string>(),
                $"match {matchId}",
                cancellationToken);
            var parsed = ParseMatch(document.RootElement);
            if (parsed.LobbyType != PracticeMatchNormalizer.PracticeLobbyType)
            {
                errors.Add(
                    $"比赛 {matchId} 的 OpenDota 详情 lobby_type={parsed.LobbyType}，不是练习比赛。");
                return null;
            }
            if (!parsed.HasUsableRoster || parsed.WinnerTeam is not (2 or 3))
            {
                errors.Add($"比赛 {matchId} 的 OpenDota 详情缺少可统计的阵容或胜负。");
            }
            return parsed;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException
            or InvalidDataException)
        {
            errors.Add(
                $"比赛 {matchId} 的 OpenDota 详情请求失败（{exception.GetType().Name}）。");
            return null;
        }
    }

    private async Task<JsonDocument> GetJsonAsync(
        string path,
        IReadOnlyDictionary<string, string> parameters,
        string safeLabel,
        CancellationToken cancellationToken)
    {
        var query = parameters
            .Select(pair =>
                Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))
            .ToList();
        if (apiKey is not null)
        {
            query.Add("api_key=" + Uri.EscapeDataString(apiKey));
        }
        var requestTarget = query.Count == 0 ? path : path + "?" + string.Join('&', query);

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            await RespectRequestIntervalAsync(cancellationToken);
            HttpResponseMessage response;
            try
            {
                response = await httpClient.GetAsync(requestTarget, cancellationToken);
            }
            catch (Exception exception) when (
                attempt < MaxRetries
                && exception is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
                continue;
            }
            using (response)
            {
                lastRequestAtUtc = DateTime.UtcNow;
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                }

                if ((response.StatusCode == HttpStatusCode.TooManyRequests
                        || (int)response.StatusCode >= 500)
                    && attempt < MaxRetries)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta
                        ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    await Task.Delay(
                        retryAfter > TimeSpan.FromSeconds(30)
                            ? TimeSpan.FromSeconds(30)
                            : retryAfter,
                        cancellationToken);
                    continue;
                }

                throw new HttpRequestException(
                    $"OpenDota {safeLabel} returned HTTP {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
            }
        }

        throw new HttpRequestException($"OpenDota {safeLabel} exhausted retries.");
    }

    private async Task RespectRequestIntervalAsync(CancellationToken cancellationToken)
    {
        var minimumInterval = apiKey is null
            ? TimeSpan.FromSeconds(1)
            : TimeSpan.FromMilliseconds(100);
        var remaining = minimumInterval - (DateTime.UtcNow - lastRequestAtUtc);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }

    private static void Upsert(
        IDictionary<ulong, PracticeMatchData> matches,
        PracticeMatchData incoming)
    {
        if (incoming.MatchId == 0)
        {
            return;
        }

        matches[incoming.MatchId] = matches.TryGetValue(incoming.MatchId, out var existing)
            ? PracticeMatchNormalizer.Merge(existing, incoming)
            : incoming;
    }

    private static ulong RequiredUInt64(JsonElement element, string property) =>
        TryUInt64(element, property)
        ?? throw new InvalidDataException($"OpenDota match is missing {property}.");

    private static long RequiredInt64(JsonElement element, string property) =>
        TryInt64(element, property)
        ?? throw new InvalidDataException($"OpenDota match is missing {property}.");

    private static ulong? TryUInt64(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number))
        {
            return number;
        }
        return value.ValueKind == JsonValueKind.String
            && ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }

    private static long? TryInt64(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }
        return value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }

    private static uint? TryUInt32(JsonElement element, string property)
    {
        var value = TryUInt64(element, property);
        return value is <= uint.MaxValue ? (uint)value.Value : null;
    }

    private static int? TryInt32(JsonElement element, string property)
    {
        var value = TryInt64(element, property);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }

    private static bool? TryBoolean(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static string? TryString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            return null;
        }
        return value.GetString()!.Trim();
    }
}
