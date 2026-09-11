using System.Net;
using System.Text.Json;
using Dota2MmrCollector;
using SteamKit2.GC.Dota.Internal;

var root = Path.Combine(Path.GetTempPath(), "dota2-teammates-test-" + Guid.NewGuid().ToString("N"));
var cache = new TeammateCache(root, 1);
var scope = Enumerable.Range(1, 12).Select(i => new TeammateMatch((ulong)i,
    $"2026-01-{i:00}", i <= 9, i <= 9 ? 25 : -25, i > 10)
    { CurveMmr = 3000 + (i <= 9 ? i : 18 - i) * 25 }).ToArray();
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}

GcMatchDetail Detail(int index)
{
    var players = Enumerable.Range(0, 10).Select(slot => new TeammatePlayer(
        (uint)(1000 + slot + index * 10), (uint)(slot < 5 ? slot : 128 + slot - 5), 1, "random", 2, 3, 4)).ToArray();
    players[0] = players[0] with { AccountId = 1 };
    players[1] = players[1] with { AccountId = 100, Name = index < 12 ? "old name" : "<script>renamed", Deaths = index == 1 ? null : 0 };
    players[2] = players[2] with { AccountId = 200, Name = "<script>renamed" };
    players[index <= 10 ? 3 : 5] = players[index <= 10 ? 3 : 5] with { AccountId = 300 };
    players[9] = players[9] with { AccountId = null };
    return new(1, (ulong)index, 7, DateTimeOffset.UtcNow, players);
}

cache.SaveGc(Detail(1));
Check(TeammateReport.Build(scope, cache, []).Teammates.Count == 0, "one match never enters the candidate list");
cache.SaveGc(Detail(2));
Check(TeammateReport.Build(scope, cache, []).Teammates.All(t => t.Matches == 2)
    && TeammateReport.Build(scope, cache, []).Teammates.Count == 3, "two matches qualify and retain both samples");
foreach (var i in Enumerable.Range(3, 10)) cache.SaveGc(Detail(i));
cache.SaveGc(Detail(99));
foreach (var i in Enumerable.Range(1, 12)) cache.SaveImp(new((ulong)i, DateTimeOffset.UtcNow,
    new() { [100] = i == 1 ? null : i == 2 ? (short)0 : (short)5, [200] = -2, [300] = 1 }));
var report = TeammateReport.Build(scope, cache, ["Synthetic fixture / 合成测试数据"]);
var first = report.Teammates.Single(t => t.AccountId == 100);
Check(report.Teammates.Count == 3 && first.Matches == 12 && report.Teammates.Single(t => t.AccountId == 300).Matches == 10,
    "opponents, anonymous and out-of-scope matches excluded");
Check(first.Name == "<script>renamed" && report.Teammates.Select(t => t.AccountId).Distinct().Count() == 3,
    "rename merges by ID; identical names remain separate accounts");
Check(first.ImpMatches == 11 && first.ImpSum == 50 && Math.Abs(first.AverageImp!.Value - 50d / 11) < 1e-9,
    "IMP zero counts; null does not; equal match weighting");
Check(first.DeathMatches == 11 && first.AverageDeaths == 0 && first.AverageKillsAssists == 6,
    "missing KDA excluded and explicit zero preserved");
Check(first.MmrGain == 225 && first.MmrLoss == 75 && first.NetMmr == 150
    && first.ActualMmr == 200 && first.EstimatedMmr == -50, "joint MMR and actual/estimated split");
Check(report.TogetherMatches == 12 && report.TogetherNetMmr == 150, "multi-teammate overview deduplicates games");
Check(report.CompleteMatches == 11 && report.Partial, "missing ally KDA reports partial coverage; anonymous enemy is acceptable");
var targets = TeammateReport.ImpTargets(scope, cache);
Check(targets.Count == 12 && targets.All(p => p.Value.SequenceEqual(p.Key <= 10 ? new uint[] { 100, 200, 300 } : new uint[] { 100, 200 })),
    "STRATZ targets contain only in-scope same-side selected teammates");
TeammateReport.Write(root, report);
var html = File.ReadAllText(Path.Combine(root, "teammate-report.html"));
Check(html.Contains("\\u003Cscript\\u003Erenamed") && !html.Contains("<script>renamed")
    && html.Contains("id=\"minimum-range\""), "offline interactive report safely embeds player names");
Check(report.Matches.Count == 12 && report.Matches.Sum(m => m.MmrChange) == 150
    && report.Matches.SelectMany(m => m.Players).All(p => p.AccountId is 100 or 200 or 300)
    && report.Matches[0].Players.Single(p => p.AccountId == 100) is { Deaths: null, Imp: null }
    && report.Matches[1].Players.Single(p => p.AccountId == 100) is { Deaths: 0, Imp: 0 }
    && report.Matches[0].CurveMmr == 3025 && report.Matches[11].DateUtc == "2026-01-12",
    "date filtering payload preserves match times, curve, membership, missing values and explicit zeros");

var dire = Detail(1) with { Players = Detail(1).Players.Select(p => p with { Slot = p.Slot ^ 128 }).ToArray() };
Check(dire.Allies().Select(p => p.AccountId).SequenceEqual(Detail(1).Allies().Select(p => p.AccountId)), "Dire and Radiant same-side detection");
Check((Detail(1) with { LobbyType = 0 }).Allies().Count == 4, "normal matchmaking details identify allies");
Check((Detail(1) with { LobbyType = 4 }).Allies().Count == 0, "bot lobby details excluded");
Check((Detail(1) with { Source = "opendota" }).Allies().Count == 0, "research OpenDota samples cannot become GC evidence");
File.WriteAllText(Path.Combine(root, "gc-match-details", "100.json"), "{ broken json");
File.WriteAllText(Path.Combine(root, "gc-match-details", "101.json"), "{\"AccountId\":1,\"MatchId\":101}");
Check(cache.ReadGc(100) is null && cache.ReadGc(101) is null, "corrupt or malformed cache retried safely");

var response = new CMsgGCMatchDetailsResponse { result = 1, match = new() { match_id = 55, lobby_type = 7 } };
response.match.players.Add(new() { account_id = 1, player_slot = 0, hero_id = 1, player_name = "self", kills = 0, assists = 0 });
var mapped = GcMatchDetailsCollector.Map(1, 55, response);
Check(mapped.Players[0].Kills == 0 && mapped.Players[0].Deaths is null && mapped.Players[0].Slot == 0,
    "protobuf presence distinguishes zero from absent fields");
Check(GcMatchDetailsCollector.Map(1, 56, response).Players.Count == 0
    && GcMatchDetailsCollector.Map(1, 55, null).Error == "response_timeout", "GC wrong match and timeout do not fabricate data");

TeammateCache.Write(Path.Combine(root, "mmr-dataset.json"), new
{
    account_id = 1,
    rows = new[] { new { match_id = "123", date_utc = "2026-01-01", result = "Win",
        modeled_rank_change = 25, mmr_fields_visible = false, anchor_jump_before = 1000, curve_mmr_after = 4025 } },
});
Check(TeammateReport.ReadScope(root, 1).Single() is { MmrChange: 25, CurveMmr: 4025 },
    "curve MMR retained while anchor jumps remain excluded from contribution");
try { TeammateReport.ReadScope(root, 2); throw new Exception("wrong account accepted"); }
catch (InvalidDataException) { Check(true, "scope account isolation"); }

var normalRow = new
{
    MatchId = new { Present = true, Value = 201 }, StartTime = new { Present = true, Value = 1700000000 },
    Winner = new { Present = true, Value = false }, LobbyType = new { Present = true, Value = 0 },
    GameMode = new { Present = true, Value = 22 },
};
var historyPath = Path.Combine(root, "gc-collection.json");
TeammateCache.Write(historyPath, new { AccountId = 1, MatchHistory = new { Matches = new[]
{
    normalRow, normalRow,
    normalRow with { MatchId = new { Present = true, Value = 202 }, GameMode = new { Present = true, Value = 23 } },
    normalRow with { MatchId = new { Present = true, Value = 203 }, LobbyType = new { Present = true, Value = 4 } },
    normalRow with { MatchId = new { Present = true, Value = 204 }, GameMode = new { Present = true, Value = 18 } },
    normalRow with { MatchId = new { Present = true, Value = 205 }, Winner = new { Present = false, Value = false } },
    normalRow with { MatchId = new { Present = true, Value = 206 }, GameMode = new { Present = false, Value = 22 } },
    normalRow with { MatchId = new { Present = true, Value = 207 }, LobbyType = new { Present = true, Value = 7 } },
    normalRow with { MatchId = new { Present = true, Value = 208 }, GameMode = new { Present = true, Value = 3 } },
} } });
Check(TeammateReport.ReadScope(root, 1).Select(m => m.MatchId).SequenceEqual(new ulong[] { 123 }),
    "normal option off preserves exact MMR scope");
var combinedScope = TeammateReport.ReadScope(root, 1, historyPath);
Check(combinedScope.Select(m => m.MatchId).SequenceEqual(new ulong[] { 123, 201, 208 })
    && combinedScope.Skip(1).All(m => !m.IsRanked && m.MmrChange is null && !m.Won),
    "normal option adds standard matches once; excludes Turbo, bots, Ability Draft, absent fields and extra ranked games");
TeammateCache.Write(historyPath, new { AccountId = 2 });
try { TeammateReport.ReadScope(root, 1, historyPath); throw new Exception("wrong normal account accepted"); }
catch (InvalidDataException) { Check(true, "normal history account isolation"); }

var normalScope = Enumerable.Range(201, 12).Select(i => new TeammateMatch((ulong)i, "2026-02-01", false, null, false)).ToArray();
foreach (var match in normalScope)
{
    var detail = Detail((int)match.MatchId) with { LobbyType = 0 };
    var players = detail.Players.ToArray();
    players[1] = players[1] with { Deaths = 2 };
    players[3] = players[3] with { AccountId = 400, Name = "normal only" };
    players[4] = players[4] with { AccountId = 300, Name = "ten ranked plus normal" };
    players[5] = players[5] with { AccountId = 500 };
    cache.SaveGc(detail with { Players = players });
    cache.SaveImp(new(match.MatchId, DateTimeOffset.UtcNow, new() { [100] = 10, [200] = -5, [300] = 2, [400] = 3 }));
}
var mixedScope = scope.Concat(normalScope).ToArray();
var mixedReport = TeammateReport.Build(mixedScope, cache, []) with { IncludesNormalMatches = true };
var mixed = mixedReport.Teammates.Single(t => t.AccountId == 100);
Check(mixed.Matches == 24 && mixed.Wins == 9 && mixed.ImpMatches == 23 && mixed.ImpSum == 170
    && mixed.DeathMatches == 23 && mixed.Deaths == 24 && mixed.RankedMatches == 12 && mixed.NormalMatches == 12,
    "combined report includes normal wins, KDA and IMP in the denominator");
Check(mixed.MmrGain == first.MmrGain && mixed.MmrLoss == first.MmrLoss
    && mixed.ActualMatches == first.ActualMatches && mixed.EstimatedMatches == first.EstimatedMatches
    && mixedReport.TogetherNetMmr == report.TogetherNetMmr && mixedReport.TogetherMatches == 24,
    "ordinary games never become actual or estimated MMR samples; combined overview deduplicates");
Check(mixedReport.Teammates.Single(t => t.AccountId == 400).RankedMatches == 0
    && mixedReport.Teammates.Single(t => t.AccountId == 300).RankedMatches == 10
    && mixedReport.Matches.Count(m => m.MmrChange is null) == 12,
    "normal-only and low-ranked-sample teammates retain distinct MMR coverage");
Check(TeammateReport.ImpTargets(mixedScope, cache).Count == 24
    && TeammateReport.ImpTargets(scope, cache).Count == 12
    && TeammateReport.Build(scope, cache, []).Teammates.Count == 3,
    "request scope expands only when enabled; normal cache cannot leak into ranked-only view");
Check(cache.ReadGc(normalScope[0])?.Complete == true
    && cache.ReadGc(normalScope[0] with { MmrChange = 25 }) is null,
    "normal details are reusable and mismatched lobby evidence is rejected");
TeammateReport.Write(Path.Combine(root, "combined"), mixedReport);

using (var doc = JsonDocument.Parse("""{"data":{"match":{"id":1,"players":[{"steamAccountId":100,"imp":0},{"steamAccountId":200,"imp":null},{"steamAccountId":null,"imp":2}]}}}"""))
{
    var imp = StratzClient.ParseMatch(doc.RootElement, 1)!;
    Check(imp.Players[100] == 0 && imp.Players[200] is null && imp.Players.Count == 2, "GraphQL nullable IMP and anonymous IDs");
    try { StratzClient.ParseMatch(doc.RootElement, 2); throw new Exception("wrong match accepted"); }
    catch (StratzException) { Check(true, "STRATZ match ID validation"); }
}
var requests = 0;
cache.SaveImp(new(1, DateTimeOffset.UtcNow, new() { [100] = 1, [200] = -2, [300] = 1 }));
using (var http = new HttpClient(new FakeHandler(async request =>
{
    requests++;
    Check(request.Method == HttpMethod.Post && request.RequestUri == StratzClient.Endpoint
        && request.Headers.Authorization?.Parameter == "fixture-token" && request.Headers.UserAgent.ToString() == "STRATZ_API",
        "STRATZ authenticated POST to official endpoint");
    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
    Check(body.RootElement.GetProperty("variables").GetProperty("id").GetUInt64() == 12, "only missing match requested");
    return new(HttpStatusCode.OK) { Content = new StringContent("""{"data":{"match":{"id":12,"players":[{"steamAccountId":100,"imp":0},{"steamAccountId":200,"imp":-3}]}}}""") };
})))
{
    cache.SaveImp(new(12, DateTimeOffset.UtcNow, new() { [100] = null, [200] = -2 }));
    var client = new StratzClient(http);
    await client.CollectAsync("fixture-token", targets, cache, CancellationToken.None);
    await client.CollectAsync("fixture-token", targets, cache, CancellationToken.None);
    Check(requests == 1 && cache.ReadImp(12)!.Players[100] == 0, "resume reuses IMP zero and complete cache without new requests");
}

foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.OK })
{
    using var http = new HttpClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(status)
        { Content = new StringContent("""{"errors":[{"message":"private-server-details"}]}""") })));
    try { await new StratzClient(http).ValidateAsync("fixture-token", CancellationToken.None); throw new Exception("server failure accepted"); }
    catch (StratzException e)
    {
        Check(e.AuthenticationFailed == (status == HttpStatusCode.Unauthorized) && !e.Message.Contains("private-server-details"),
            $"STRATZ {status} stops safely without leaking server details");
    }
}
Check(StratzLogin.ExtractUserToken(Uri.EscapeDataString("{\"token\":\"fixture-personal-token\"}")) == "fixture-personal-token"
    && StratzLogin.ExtractUserToken(Uri.EscapeDataString("{\"anonymousToken\":\"fixture-anonymous\"}")) is null,
    "OpenID cookie accepts personal token only");
Check(!StratzLogin.AllowedRedirect(new Uri("https://stratz.com.evil.invalid"))
    && !StratzLogin.AllowedRedirect(new Uri("http://stratz.com"))
    && !StratzLogin.AllowedRedirect(new Uri("https://evil@stratz.com")), "SSO host, HTTPS and user-info validation");
var steamPage = new Uri("https://steamcommunity.com/openid/login");
const string formHtml = """<form id="openidForm" action="https://steamcommunity.com/openid/login"><input type="hidden" name="openidparams" value="a&amp;b"><input type="hidden" name="nonce" value="fixture"></form>""";
Check(StratzLogin.ParseOpenIdForm(formHtml, steamPage)?["openidparams"] == "a&b"
    && StratzLogin.ParseOpenIdForm(formHtml.Replace("action=\"https://steamcommunity.com", "action=\"https://evil.invalid"), steamPage) is null,
    "only Steam OpenID form is submitted; HTML entities preserved");
var cookies = new CookieContainer();
var hop = 0;
using (var http = new HttpClient(new FakeHandler(async request =>
{
    hop++;
    if (hop == 1) return new(HttpStatusCode.Found) { Headers = { Location = steamPage } };
    if (hop == 2) return new(HttpStatusCode.OK) { Content = new StringContent(formHtml) };
    if (hop == 3)
    {
        Check(request.Method == HttpMethod.Post && (await request.Content!.ReadAsStringAsync()).Contains("action=steam_openid_login"),
            "SSO submits normal Steam identity authorization");
        return new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://stratz.com/api") } };
    }
    cookies.Add(new Uri("https://stratz.com"), new Cookie("user", Uri.EscapeDataString("{\"token\":\"fixture-personal-token\"}")));
    return new(HttpStatusCode.OK) { Content = new StringContent("signed in") };
})))
    Check(await StratzLogin.FollowOpenIdAsync(http, cookies, CancellationToken.None) == "fixture-personal-token" && hop == 4,
        "SSO redirect/form/cookie integration with simulated HTTP");

Console.WriteLine($"Test artifacts: {root}");

internal sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
}
