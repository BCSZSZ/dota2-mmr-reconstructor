using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Dota2MmrCollector;

internal sealed record TeammateMatch(ulong MatchId, string DateUtc, bool Won, int? MmrChange, bool Estimated)
{
    public bool IsRanked => MmrChange.HasValue;
    public int? CurveMmr { get; init; }
}
internal sealed record TeammatePlayer(uint? AccountId, uint? Slot, int? HeroId, string? Name,
    uint? Kills, uint? Deaths, uint? Assists);
internal sealed record GcMatchDetail(uint AccountId, ulong MatchId, uint? LobbyType,
    DateTimeOffset FetchedAtUtc, IReadOnlyList<TeammatePlayer> Players, string? Error = null)
{
    public string Source { get; init; } = "gc";

    public IReadOnlyList<TeammatePlayer> Allies()
    {
        if (Source != "gc" || LobbyType is not (0 or 7) || Players.Count != 10
            || Players.Any(p => p.Slot is null || !(p.Slot <= 4 || p.Slot is >= 128 and <= 132))
            || Players.Select(p => p.Slot).Distinct().Count() != 10)
            return [];
        var self = Players.Where(p => p.AccountId == AccountId).ToArray();
        if (self.Length != 1) return [];
        return Players.Where(p => p.Slot != self[0].Slot && (p.Slot < 128) == (self[0].Slot < 128)).ToArray();
    }

    public bool Complete => Error is null && Allies() is { Count: 4 } allies
        && allies.All(p => p.AccountId is > 0 and < uint.MaxValue)
        && allies.Select(p => p.AccountId).Distinct().Count() == 4
        && allies.All(p => p.HeroId is > 0 && p.Kills.HasValue && p.Deaths.HasValue && p.Assists.HasValue);
}

internal sealed record ImpMatch(ulong MatchId, DateTimeOffset FetchedAtUtc, Dictionary<uint, short?> Players)
{
    public string Source { get; init; } = "stratz";
}

internal sealed class TeammateCache(string accountDirectory, uint accountId)
{
    public string AccountDirectory { get; } = accountDirectory;
    public uint AccountId { get; } = accountId;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public GcMatchDetail? ReadGc(ulong id)
    {
        var value = Read<GcMatchDetail>(Path.Combine(AccountDirectory, "gc-match-details", $"{id}.json"));
        return value?.AccountId == AccountId && value.MatchId == id && value.Source == "gc"
            && value.Players is not null && value.Players.All(p => p is not null) ? value : null;
    }

    public ImpMatch? ReadImp(ulong id)
    {
        var value = Read<ImpMatch>(Path.Combine(AccountDirectory, "stratz-imp", $"{id}.json"));
        return value?.MatchId == id && value.Source == "stratz" && value.Players is not null ? value : null;
    }

    public GcMatchDetail? ReadGc(TeammateMatch match)
    {
        var detail = ReadGc(match.MatchId);
        return detail?.LobbyType == (match.IsRanked ? 7u : 0u) ? detail : null;
    }

    public void SaveGc(GcMatchDetail match) => Write(Path.Combine(AccountDirectory, "gc-match-details", $"{match.MatchId}.json"), match);
    public void SaveImp(ImpMatch match) => Write(Path.Combine(AccountDirectory, "stratz-imp", $"{match.MatchId}.json"), match);

    public static T? Read<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return default; }
    }

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
        File.Move(path + ".tmp", path, true);
    }
}

internal sealed class TeammateStats(uint accountId)
{
    public uint AccountId { get; } = accountId;
    public string Name { get; set; } = $"ID {accountId}";
    public int Matches { get; set; }
    public int RankedMatches => ActualMatches + EstimatedMatches;
    public int NormalMatches => Matches - RankedMatches;
    public int Wins { get; set; }
    public int Losses => Matches - Wins;
    public double WinRate => Matches == 0 ? 0 : Wins / (double)Matches;
    public int DeathMatches { get; set; }
    public long Deaths { get; set; }
    public double? AverageDeaths => DeathMatches == 0 ? null : Deaths / (double)DeathMatches;
    public int KillAssistMatches { get; set; }
    public long KillsAssists { get; set; }
    public double? AverageKillsAssists => KillAssistMatches == 0 ? null : KillsAssists / (double)KillAssistMatches;
    public int ImpMatches { get; set; }
    public long ImpSum { get; set; }
    public double? AverageImp => ImpMatches == 0 ? null : ImpSum / (double)ImpMatches;
    public long MmrGain { get; set; }
    public long MmrLoss { get; set; }
    public long NetMmr => MmrGain - MmrLoss;
    public int ActualMatches { get; set; }
    public long ActualMmr { get; set; }
    public int EstimatedMatches { get; set; }
    public long EstimatedMmr { get; set; }
}

internal sealed record TeammateReportPlayer(uint AccountId, uint? Deaths, long? KillsAssists, short? Imp);
internal sealed record TeammateReportMatch(string MatchId, string DateUtc, bool Won, int? MmrChange,
    bool Estimated, int? CurveMmr, bool Cached, bool Complete, IReadOnlyList<TeammateReportPlayer> Players);

internal sealed record TeammateReportData(uint AccountId, int ScopeMatches, int CachedMatches,
    int CompleteMatches, int TogetherMatches, long TogetherNetMmr, int ScoredPlayerMatches,
    IReadOnlyList<TeammateStats> Teammates, IReadOnlyList<string> Notes)
{
    public IReadOnlyList<TeammateReportMatch> Matches { get; init; } = [];
    public int NormalMatches { get; init; }
    public int RankedMatches => ScopeMatches - NormalMatches;
    public bool IncludesNormalMatches { get; init; }
    public int MinimumMatches => TeammateReport.MinimumMatches;
    public int DefaultMinimumMatches => 11;
    public bool Partial => CompleteMatches < ScopeMatches;
}

internal static class TeammateReport
{
    public const int MinimumMatches = 2;

    public static IReadOnlyList<TeammateMatch> ReadScope(string outputDirectory, uint accountId,
        string? collectionPath = null)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(outputDirectory, "mmr-dataset.json")));
        if (doc.RootElement.GetProperty("account_id").GetUInt32() != accountId)
            throw new InvalidDataException("队友报告与MMR数据的账号不一致。");
        var rows = doc.RootElement.GetProperty("rows").EnumerateArray().Select(r => new TeammateMatch(
            ulong.Parse(r.GetProperty("match_id").GetString()!, CultureInfo.InvariantCulture),
            r.GetProperty("date_utc").GetString()!, r.GetProperty("result").GetString() == "Win",
            r.GetProperty("modeled_rank_change").GetInt32(), !r.GetProperty("mmr_fields_visible").GetBoolean())
            { CurveMmr = r.GetProperty("curve_mmr_after").GetInt32() }).ToArray();
        if (rows.Select(r => r.MatchId).Distinct().Count() != rows.Length)
            throw new InvalidDataException("MMR比赛ID重复，停止队友统计。");
        if (collectionPath is null) return rows;
        var collection = JsonSerializer.Deserialize<CollectorOutput>(File.ReadAllText(collectionPath))
            ?? throw new InvalidDataException("无法读取普通匹配历史。");
        if (collection.AccountId != accountId)
            throw new InvalidDataException("普通匹配历史与队友报告的账号不一致。");
        var ids = rows.Select(r => r.MatchId).ToHashSet();
        var normal = collection.MatchHistory.Matches.Where(m =>
            m.LobbyType is { Present: true, Value: 0 }
            // Standard matchmaking modes; exclude Turbo, Ability Draft, events, custom and bots.
            && m.GameMode is { Present: true, Value: 1 or 2 or 3 or 4 or 5 or 12 or 16 or 22 }
            && m.MatchId is { Present: true, Value: > 0 } && m.StartTime is { Present: true, Value: > 0 }
            && m.Winner is { Present: true } && ids.Add(m.MatchId.Value))
            .Select(m => new TeammateMatch(m.MatchId.Value,
                DateTimeOffset.FromUnixTimeSeconds(m.StartTime.Value).ToString("O", CultureInfo.InvariantCulture),
                m.Winner.Value, null, false));
        return rows.Concat(normal).ToArray();
    }

    public static Dictionary<ulong, uint[]> ImpTargets(IReadOnlyList<TeammateMatch> scope, TeammateCache cache)
    {
        var allies = scope.ToDictionary(m => m.MatchId, m => KnownAllies(cache.ReadGc(m)));
        var selected = allies.Values.SelectMany(p => p).GroupBy(p => p.AccountId!.Value)
            .Where(g => g.Count() >= MinimumMatches).Select(g => g.Key).ToHashSet();
        return allies.Select(pair => (pair.Key, Ids: pair.Value.Select(p => p.AccountId!.Value).Where(selected.Contains).ToArray()))
            .Where(p => p.Ids.Length > 0).ToDictionary(p => p.Key, p => p.Ids);
    }

    private static TeammatePlayer[] KnownAllies(GcMatchDetail? detail) => (detail?.Allies() ?? [])
        .Where(p => p.AccountId is > 0 and < uint.MaxValue && p.AccountId != detail!.AccountId)
        .GroupBy(p => p.AccountId).Where(g => g.Count() == 1).Select(g => g.Single()).ToArray();

    public static TeammateReportData Build(IReadOnlyList<TeammateMatch> scope, TeammateCache cache,
        IReadOnlyList<string> notes)
    {
        var stats = new Dictionary<uint, TeammateStats>();
        var matches = new List<TeammateReportMatch>();
        var details = scope.ToDictionary(m => m.MatchId, m => cache.ReadGc(m));
        foreach (var match in scope.OrderBy(m => m.DateUtc, StringComparer.Ordinal))
        {
            var imp = cache.ReadImp(match.MatchId);
            var allies = KnownAllies(details[match.MatchId]);
            matches.Add(new(match.MatchId.ToString(CultureInfo.InvariantCulture), match.DateUtc, match.Won,
                match.MmrChange, match.Estimated, match.CurveMmr, details[match.MatchId] is not null,
                details[match.MatchId]?.Complete == true, allies.Select(p => new TeammateReportPlayer(
                    p.AccountId!.Value, p.Deaths, p.Kills is { } k && p.Assists is { } a ? (long)k + a : null,
                    imp?.Players.GetValueOrDefault(p.AccountId!.Value))).ToArray()));
            foreach (var p in allies)
            {
                var id = p.AccountId!.Value;
                if (!stats.TryGetValue(id, out var t)) stats[id] = t = new TeammateStats(id);
                if (!string.IsNullOrWhiteSpace(p.Name)) t.Name = p.Name.Trim();
                t.Matches++;
                if (match.Won) t.Wins++;
                if (p.Deaths is { } deaths) { t.DeathMatches++; t.Deaths += deaths; }
                if (p.Kills is { } kills && p.Assists is { } assists)
                { t.KillAssistMatches++; t.KillsAssists += (long)kills + assists; }
                if (imp?.Players.GetValueOrDefault(id) is { } score) { t.ImpMatches++; t.ImpSum += score; }
                if (match.MmrChange is { } delta)
                {
                    t.MmrGain += Math.Max(delta, 0);
                    t.MmrLoss += Math.Max(-(long)delta, 0);
                    if (match.Estimated) { t.EstimatedMatches++; t.EstimatedMmr += delta; }
                    else { t.ActualMatches++; t.ActualMmr += delta; }
                }
            }
        }
        var teammates = stats.Values.Where(t => t.Matches >= MinimumMatches)
            .OrderByDescending(t => t.Matches).ThenBy(t => t.AccountId).ToArray();
        var ids = teammates.Select(t => t.AccountId).ToHashSet();
        var together = scope.Where(m => KnownAllies(details[m.MatchId]).Any(p => ids.Contains(p.AccountId!.Value))).ToArray();
        return new TeammateReportData(cache.AccountId, scope.Count, details.Values.Count(d => d is not null),
            details.Values.Count(d => d?.Complete == true), together.Length, together.Sum(m => (long)(m.MmrChange ?? 0)),
            teammates.Sum(t => t.ImpMatches), teammates, notes)
        {
            NormalMatches = scope.Count(m => !m.IsRanked),
            Matches = matches.Select(m => m with { Players = m.Players.Where(p => ids.Contains(p.AccountId)).ToArray() }).ToArray(),
        };
    }

    public static void Write(string outputDirectory, TeammateReportData report)
    {
        TeammateCache.Write(Path.Combine(outputDirectory, "teammate-report.json"), report);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "Dota2MmrCollector.Assets.teammate-report-viewer.html")
            ?? throw new InvalidOperationException("发布包缺少队友报告查看器。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        // Default JSON escaping keeps player names from terminating the data script element.
        var html = reader.ReadToEnd().Replace("__TEAMMATE_DATA__", JsonSerializer.Serialize(report), StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(outputDirectory, "teammate-report.html"), html, new UTF8Encoding(false));
    }
}
