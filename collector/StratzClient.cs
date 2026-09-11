using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Dota2MmrCollector;

internal sealed class StratzClient(HttpClient http)
{
    internal static readonly Uri Endpoint = new("https://api.stratz.com/graphql");

    private async Task<JsonDocument> QueryAsync(string token, string query, object? variables, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("STRATZ_API");
        request.Content = JsonContent.Create(new { query, variables });
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new StratzException("STRATZ令牌无效或接口拒绝访问，请重新填写个人API令牌。", true);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new StratzException("STRATZ请求额度或频率受限，已保存进度；下次运行会继续补缺。");
        if (!response.IsSuccessStatusCode)
            throw new StratzException($"STRATZ暂不可用（HTTP {(int)response.StatusCode}），已保留其他报告。");
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
        {
            var authFailure = errors.EnumerateArray().Any(error => error.TryGetProperty("extensions", out var extensions)
                && extensions.TryGetProperty("code", out var code)
                && code.GetString() is "UNAUTHENTICATED" or "UNAUTHORIZED" or "FORBIDDEN");
            doc.Dispose();
            // Server error text may contain request details; never write it to logs or reports.
            throw new StratzException("STRATZ返回GraphQL错误，综合表现评分暂缺；已保留其他报告。", authFailure);
        }
        return doc;
    }

    public async Task ValidateAsync(string token, CancellationToken cancellationToken)
    {
        using var doc = await QueryAsync(token, "{ __typename }", null, cancellationToken);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new StratzException("STRATZ未返回有效数据。", true);
    }

    public async Task CollectAsync(string token, Dictionary<ulong, uint[]> targets, TeammateCache cache,
        CancellationToken cancellationToken)
    {
        var missing = targets.Where(p => p.Value.Any(id => cache.ReadImp(p.Key)?.Players.GetValueOrDefault(id) is null)).ToArray();
        Console.WriteLine($"STRATZ 综合表现评分：有队友的比赛 {targets.Count} 场，需请求 {missing.Length} 场。");
        for (var index = 0; index < missing.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = missing[index].Key;
            using var doc = await QueryAsync(token,
                "query($id: Long!) { match(id: $id) { id players { steamAccountId imp } } }", new { id }, cancellationToken);
            var match = ParseMatch(doc.RootElement, id);
            if (match is not null)
            {
                var previous = cache.ReadImp(id);
                if (previous is not null)
                    foreach (var score in previous.Players.Where(p => p.Value.HasValue))
                        if (match.Players.GetValueOrDefault(score.Key) is null) match.Players[score.Key] = score.Value;
                cache.SaveImp(match);
            }
            if (index % 10 == 0 || index + 1 == missing.Length)
                Console.WriteLine($"STRATZ 综合表现评分 {index + 1}/{missing.Length}：{id}。");
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    internal static ImpMatch? ParseMatch(JsonElement root, ulong id)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("match", out var match) || match.ValueKind != JsonValueKind.Object) return null;
        if (!match.TryGetProperty("id", out var returnedId) || returnedId.ValueKind != JsonValueKind.Number
            || !returnedId.TryGetUInt64(out var actualId) || actualId != id)
            throw new StratzException("STRATZ返回了不匹配的比赛ID，停止写入评分缓存。");
        var scores = new Dictionary<uint, short?>();
        if (match.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Array)
            foreach (var player in players.EnumerateArray())
                if (player.TryGetProperty("steamAccountId", out var account) && account.ValueKind == JsonValueKind.Number
                    && account.TryGetUInt32(out var accountId)
                    && accountId is > 0 and < uint.MaxValue)
                    scores[accountId] = player.TryGetProperty("imp", out var imp) && imp.ValueKind == JsonValueKind.Number
                        && imp.TryGetInt16(out var score) ? score : null;
        return new(id, DateTimeOffset.UtcNow, scores);
    }
}

internal sealed class StratzException(string message, bool authenticationFailed = false) : Exception(message)
{
    public bool AuthenticationFailed { get; } = authenticationFailed;
}
