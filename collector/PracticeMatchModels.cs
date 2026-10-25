using System.Globalization;
using System.Text.Json;
using SteamKit2.GC.Dota.Internal;

namespace Dota2MmrCollector;

internal enum CollectorTaskKind
{
    MmrReconstruction,
    PracticeMatchReport,
}

internal sealed record PracticeReportSettings(
    DateOnly FromDate,
    DateOnly ToDateInclusive,
    string TimeZoneId,
    EphemeralSecret? OpenDotaApiKey)
{
    public TimeZoneInfo TimeZone => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    public DateTimeOffset StartUtc => ToUtc(FromDate);

    public DateTimeOffset EndUtcExclusive => ToUtc(ToDateInclusive.AddDays(1));

    public bool Contains(long startTimeUnix) =>
        startTimeUnix >= StartUtc.ToUnixTimeSeconds()
        && startTimeUnix < EndUtcExclusive.ToUnixTimeSeconds();

    private DateTimeOffset ToUtc(DateOnly date)
    {
        var unspecified = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, TimeZone), TimeSpan.Zero);
    }
}

internal sealed class EphemeralSecret : IDisposable
{
    public EphemeralSecret(string value) => Value = value;

    public string Value { get; private set; }

    public bool IsCleared => Value.Length == 0;

    public void Clear() => Value = string.Empty;

    public void Dispose() => Clear();
}

internal sealed record PracticePlayerData(
    uint? AccountId,
    string? Name,
    int? Team,
    int? PlayerSlot,
    int HeroId,
    bool IsBot);

internal sealed record PracticeMatchData(
    ulong MatchId,
    long StartTimeUnix,
    uint LobbyType,
    int? WinnerTeam,
    string Outcome,
    IReadOnlyList<PracticePlayerData> Players,
    IReadOnlyList<string> Sources)
{
    public bool HasUsableRoster => Players.Any(player =>
        !player.IsBot && player.HeroId > 0 && player.Team is 2 or 3);

    public bool HasSource(string source) =>
        Sources.Any(item => string.Equals(item, source, StringComparison.OrdinalIgnoreCase));
}

internal sealed record PracticeMatchCache(
    int SchemaVersion,
    uint AccountId,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<PracticeMatchData> Matches);

internal sealed record PracticeGcCollectionSummary(
    int HistoryPagesRequested,
    int HistoryRowsScanned,
    int SeedMatches,
    int MatchDetailsRequested,
    int MatchDetailsSucceeded,
    IReadOnlyList<string> Errors);

internal sealed record PracticeOpenDotaSummary(
    bool ApiKeyUsed,
    bool Complete,
    int AccountsQueried,
    int CandidateMatches,
    int MatchDetailsRequested,
    int MatchesAdded,
    IReadOnlyList<string> Errors);

internal sealed record PracticePeriodSummary(
    DateOnly FromDate,
    DateOnly ToDateInclusive,
    string TimeZoneId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtcExclusive);

internal sealed record PracticeCollectionOutput(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    uint AccountId,
    PracticePeriodSummary Period,
    PracticeGcCollectionSummary Gc,
    PracticeOpenDotaSummary OpenDota,
    IReadOnlyList<uint> KnownTeammateAccountIds,
    IReadOnlyList<PracticeMatchData> Matches,
    IReadOnlyList<string> Notes);

internal sealed record PracticePlayerSummary(
    string PlayerKey,
    uint? AccountId,
    string PlayerName,
    int Wins,
    int Losses)
{
    public int TotalMatches => Wins + Losses;

    public double? WinRate => TotalMatches == 0 ? null : Wins / (double)TotalMatches;
}

internal sealed record PracticePairSummary(
    string PlayerAKey,
    uint? PlayerAAccountId,
    string PlayerAName,
    string PlayerBKey,
    uint? PlayerBAccountId,
    string PlayerBName,
    int Wins,
    int Losses)
{
    public int TotalMatches => Wins + Losses;

    public double? WinRate => TotalMatches == 0 ? null : Wins / (double)TotalMatches;
}

internal sealed record PracticeAggregation(
    IReadOnlyList<PracticePlayerSummary> Players,
    IReadOnlyList<PracticePairSummary> Pairs,
    int MatchesInPeriod,
    int ScoredMatches,
    int UnscoredMatches,
    int AnonymousPlayerRows,
    int ExcludedPlayerRows);

internal static class PracticeMatchNormalizer
{
    public const uint PracticeLobbyType = 1;
    public const int RadiantTeam = 2;
    public const int DireTeam = 3;

    public static PracticeMatchData FromGc(
        CMsgDOTAMatch match,
        uint authenticatedAccountId,
        bool? authenticatedPlayerWon)
    {
        var players = match.players.Select(player =>
        {
            var team = ResolveTeam(
                player.ShouldSerializeteam_number() ? (int)player.team_number : null,
                player.ShouldSerializeplayer_slot() ? (int)player.player_slot : null);
            var account = player.ShouldSerializeaccount_id() && player.account_id != 0
                ? player.account_id
                : (uint?)null;
            return new PracticePlayerData(
                account,
                player.ShouldSerializeplayer_name() && !string.IsNullOrWhiteSpace(player.player_name)
                    ? player.player_name.Trim()
                    : null,
                team,
                player.ShouldSerializeplayer_slot() ? (int)player.player_slot : null,
                player.ShouldSerializehero_id() ? player.hero_id : 0,
                account is null && player.ShouldSerializebot_difficulty());
        }).ToArray();

        int? winnerTeam = match.ShouldSerializematch_outcome()
            ? WinnerTeamFromOutcome((int)match.match_outcome)
            : null;
        if (winnerTeam is null && authenticatedPlayerWon is not null)
        {
            var authenticatedPlayer = players.FirstOrDefault(player =>
                player.AccountId == authenticatedAccountId && player.Team is RadiantTeam or DireTeam);
            if (authenticatedPlayer?.Team is int authenticatedTeam)
            {
                winnerTeam = authenticatedPlayerWon.Value
                    ? authenticatedTeam
                    : OpposingTeam(authenticatedTeam);
            }
        }

        return new PracticeMatchData(
            match.ShouldSerializematch_id() ? match.match_id : 0,
            match.ShouldSerializestarttime() ? match.starttime : 0,
            match.ShouldSerializelobby_type() ? match.lobby_type : 0,
            winnerTeam,
            match.ShouldSerializematch_outcome()
                ? match.match_outcome.ToString()
                : "unknown",
            players,
            ["GC"]);
    }

    public static PracticeMatchData Merge(PracticeMatchData existing, PracticeMatchData incoming)
    {
        if (existing.MatchId != incoming.MatchId)
        {
            throw new InvalidOperationException("Cannot merge different match IDs.");
        }

        var players = ChooseRoster(existing, incoming);
        var sources = existing.Sources
            .Concat(incoming.Sources)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(source => source, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new PracticeMatchData(
            existing.MatchId,
            incoming.StartTimeUnix != 0 ? incoming.StartTimeUnix : existing.StartTimeUnix,
            incoming.LobbyType != 0 ? incoming.LobbyType : existing.LobbyType,
            incoming.WinnerTeam ?? existing.WinnerTeam,
            incoming.Outcome != "unknown" ? incoming.Outcome : existing.Outcome,
            players,
            sources);
    }

    public static int? ResolveTeam(int? teamNumber, int? playerSlot)
    {
        if (teamNumber is RadiantTeam or DireTeam)
        {
            return teamNumber;
        }

        if (playerSlot is >= 0 and <= 4)
        {
            return RadiantTeam;
        }

        if (playerSlot is >= 128 and <= 132)
        {
            return DireTeam;
        }

        return null;
    }

    public static int? WinnerTeamFromOutcome(int outcome) => outcome switch
    {
        2 => RadiantTeam,
        3 => DireTeam,
        _ => null,
    };

    private static int OpposingTeam(int team) => team == RadiantTeam ? DireTeam : RadiantTeam;

    private static IReadOnlyList<PracticePlayerData> ChooseRoster(
        PracticeMatchData existing,
        PracticeMatchData incoming)
    {
        var existingScore = RosterScore(existing.Players);
        var incomingScore = RosterScore(incoming.Players);
        var primary = incomingScore > existingScore ? incoming.Players : existing.Players;
        var secondary = ReferenceEquals(primary, incoming.Players)
            ? existing.Players
            : incoming.Players;

        var secondaryNames = secondary
            .Where(player => player.AccountId is not null && !string.IsNullOrWhiteSpace(player.Name))
            .GroupBy(player => player.AccountId!.Value)
            .ToDictionary(group => group.Key, group => group.First().Name!);
        return primary.Select(player =>
            player.AccountId is uint accountId
            && string.IsNullOrWhiteSpace(player.Name)
            && secondaryNames.TryGetValue(accountId, out var name)
                ? player with { Name = name }
                : player).ToArray();
    }

    private static int RosterScore(IReadOnlyList<PracticePlayerData> players) =>
        players.Count * 10
        + players.Count(player => player.Team is RadiantTeam or DireTeam) * 5
        + players.Count(player => player.HeroId > 0) * 2
        + players.Count(player => player.AccountId is not null) * 3
        + players.Count(player => !string.IsNullOrWhiteSpace(player.Name));
}

internal static class PracticeMatchAggregator
{
    private sealed class PlayerAccumulator
    {
        public required string Key { get; init; }
        public uint? AccountId { get; init; }
        public string Name { get; set; } = string.Empty;
        public long LatestNameAt { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
    }

    private sealed class PairAccumulator
    {
        public required string AKey { get; init; }
        public required string BKey { get; init; }
        public int Wins { get; set; }
        public int Losses { get; set; }
    }

    private sealed record Participant(
        string Key,
        uint? AccountId,
        string Name,
        int Team);

    public static PracticeAggregation Build(
        IReadOnlyList<PracticeMatchData> matches,
        PracticeReportSettings settings)
    {
        var periodMatches = matches
            .Where(match => match.LobbyType == PracticeMatchNormalizer.PracticeLobbyType)
            .Where(match => settings.Contains(match.StartTimeUnix))
            .GroupBy(match => match.MatchId)
            .Select(group => group.First())
            .OrderBy(match => match.StartTimeUnix)
            .ThenBy(match => match.MatchId)
            .ToArray();
        var players = new Dictionary<string, PlayerAccumulator>(StringComparer.Ordinal);
        var pairs = new Dictionary<string, PairAccumulator>(StringComparer.Ordinal);
        var scoredMatches = 0;
        var unscoredMatches = 0;
        var anonymousRows = 0;
        var excludedRows = 0;

        foreach (var match in periodMatches)
        {
            if (match.WinnerTeam is not (PracticeMatchNormalizer.RadiantTeam
                or PracticeMatchNormalizer.DireTeam))
            {
                unscoredMatches++;
                continue;
            }

            var participants = new List<Participant>();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var player in match.Players)
            {
                if (player.IsBot || player.HeroId <= 0 || player.Team is not (2 or 3))
                {
                    excludedRows++;
                    continue;
                }

                string key;
                string name;
                if (player.AccountId is uint accountId)
                {
                    key = $"id:{accountId.ToString(CultureInfo.InvariantCulture)}";
                    name = string.IsNullOrWhiteSpace(player.Name)
                        ? $"玩家 {accountId.ToString(CultureInfo.InvariantCulture)}"
                        : player.Name.Trim();
                }
                else
                {
                    var slot = player.PlayerSlot?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
                    key = $"anonymous:{match.MatchId.ToString(CultureInfo.InvariantCulture)}:{slot}";
                    name = string.IsNullOrWhiteSpace(player.Name)
                        ? $"匿名玩家（{match.MatchId}/{slot}）"
                        : $"{player.Name.Trim()}（匿名）";
                    anonymousRows++;
                }

                if (!seenKeys.Add(key))
                {
                    excludedRows++;
                    continue;
                }

                var participant = new Participant(key, player.AccountId, name, player.Team.Value);
                participants.Add(participant);
                if (!players.TryGetValue(key, out var playerSummary))
                {
                    playerSummary = new PlayerAccumulator
                    {
                        Key = key,
                        AccountId = player.AccountId,
                        Name = name,
                        LatestNameAt = match.StartTimeUnix,
                    };
                    players.Add(key, playerSummary);
                }
                else if (match.StartTimeUnix >= playerSummary.LatestNameAt
                    && !string.IsNullOrWhiteSpace(player.Name))
                {
                    playerSummary.Name = name;
                    playerSummary.LatestNameAt = match.StartTimeUnix;
                }

                if (player.Team == match.WinnerTeam)
                {
                    playerSummary.Wins++;
                }
                else
                {
                    playerSummary.Losses++;
                }
            }

            foreach (var teamGroup in participants.GroupBy(player => player.Team))
            {
                var teammates = teamGroup.OrderBy(player => player.Key, StringComparer.Ordinal).ToArray();
                var teamWon = teamGroup.Key == match.WinnerTeam;
                for (var first = 0; first < teammates.Length; first++)
                {
                    for (var second = first + 1; second < teammates.Length; second++)
                    {
                        var key = teammates[first].Key + "\u001f" + teammates[second].Key;
                        if (!pairs.TryGetValue(key, out var pair))
                        {
                            pair = new PairAccumulator
                            {
                                AKey = teammates[first].Key,
                                BKey = teammates[second].Key,
                            };
                            pairs.Add(key, pair);
                        }

                        if (teamWon)
                        {
                            pair.Wins++;
                        }
                        else
                        {
                            pair.Losses++;
                        }
                    }
                }
            }

            scoredMatches++;
        }

        var playerResults = players.Values
            .Select(player => new PracticePlayerSummary(
                player.Key,
                player.AccountId,
                player.Name,
                player.Wins,
                player.Losses))
            .OrderByDescending(player => player.TotalMatches)
            .ThenByDescending(player => player.Wins)
            .ThenBy(player => player.PlayerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(player => player.PlayerKey, StringComparer.Ordinal)
            .ToArray();
        var byKey = playerResults.ToDictionary(player => player.PlayerKey, StringComparer.Ordinal);
        var pairResults = pairs.Values
            .Select(pair =>
            {
                var a = byKey[pair.AKey];
                var b = byKey[pair.BKey];
                return new PracticePairSummary(
                    a.PlayerKey,
                    a.AccountId,
                    a.PlayerName,
                    b.PlayerKey,
                    b.AccountId,
                    b.PlayerName,
                    pair.Wins,
                    pair.Losses);
            })
            .OrderByDescending(pair => pair.TotalMatches)
            .ThenByDescending(pair => pair.WinRate)
            .ThenBy(pair => pair.PlayerAName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.PlayerBName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new PracticeAggregation(
            playerResults,
            pairResults,
            periodMatches.Length,
            scoredMatches,
            unscoredMatches,
            anonymousRows,
            excludedRows);
    }
}

internal static class PracticeJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
    };
}
