using SteamKit2;
using SteamKit2.GC;
using SteamKit2.GC.Dota.Internal;

namespace Dota2MmrCollector;

internal sealed class GcMatchDetailsCollector(SteamClient client, SteamGameCoordinator coordinator)
{
    private readonly object pendingLock = new();
    private TaskCompletionSource<CMsgGCMatchDetailsResponse>? pending;
    private ulong pendingMatch;
    private JobID? pendingJob;

    public void OnResponse(IPacketGCMsg packet)
    {
        var response = new ClientGCMsgProtobuf<CMsgGCMatchDetailsResponse>(packet).Body;
        lock (pendingLock)
        {
            // Match ID also handles GC servers which omit the job header on successful replies.
            if (response.match?.match_id == pendingMatch
                || (pendingJob is not null && packet.TargetJobID == pendingJob))
                pending?.TrySetResult(response);
        }
    }

    public async Task CollectAsync(IReadOnlyList<TeammateMatch> scope, TeammateCache cache,
        List<string> notes, CancellationToken cancellationToken)
    {
        var missing = scope.Where(m => cache.ReadGc(m)?.Complete != true).ToArray();
        Console.WriteLine($"GC 比赛详情：复用 {scope.Count - missing.Length} 场，待补 {missing.Length} 场（同一次登录）。");
        var consecutiveTimeouts = 0;
        for (var index = 0; index < missing.Length; index++)
        {
            var id = missing[index].MatchId;
            CMsgGCMatchDetailsResponse? response = null;
            for (var attempt = 0; attempt < 2 && response is null; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var completion = new TaskCompletionSource<CMsgGCMatchDetailsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                var request = new ClientGCMsgProtobuf<CMsgGCMatchDetailsRequest>((uint)EDOTAGCMsg.k_EMsgGCMatchDetailsRequest);
                request.Body.match_id = id;
                request.SourceJobID = client.GetNextJobID();
                lock (pendingLock) { pending = completion; pendingMatch = id; pendingJob = request.SourceJobID; }
                try
                {
                    coordinator.Send(request, 570);
                    response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
                }
                catch (TimeoutException) { Console.WriteLine($"GC 详情 {id} 超时（{attempt + 1}/2）。"); }
                finally { lock (pendingLock) { pending = null; pendingJob = null; } }
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            var detail = Map(cache.AccountId, id, response);
            // A failed refresh must not destroy usable fields from an earlier response.
            if (detail.Allies().Count > 0 || cache.ReadGc(id) is null) cache.SaveGc(detail);
            consecutiveTimeouts = response is null ? consecutiveTimeouts + 1 : 0;
            if (index % 10 == 0 || index + 1 == missing.Length)
                Console.WriteLine($"GC 详情 {index + 1}/{missing.Length}：{id}，{(detail.Complete ? "完整" : detail.Error ?? "字段不全")}。");
            if (consecutiveTimeouts >= 3)
            {
                notes.Add("GC连续3场无响应，已保存断点。再次运行会继续补齐缺失详情。");
                break;
            }
        }
    }

    internal static GcMatchDetail Map(uint accountId, ulong matchId, CMsgGCMatchDetailsResponse? response)
    {
        if (response is null || response.result != 1 || response.match?.match_id != matchId)
            return new(accountId, matchId, null, DateTimeOffset.UtcNow, [],
                response is null ? "response_timeout" : $"gc_result_{response.result}");
        var match = response.match;
        return new(accountId, matchId, match.ShouldSerializelobby_type() ? match.lobby_type : null,
            DateTimeOffset.UtcNow, match.players.Select(p => new TeammatePlayer(
                p.ShouldSerializeaccount_id() ? p.account_id : null,
                p.ShouldSerializeplayer_slot() ? p.player_slot : null,
                p.ShouldSerializehero_id() ? p.hero_id : null,
                p.ShouldSerializeplayer_name() ? p.player_name : null,
                p.ShouldSerializekills() ? p.kills : null,
                p.ShouldSerializedeaths() ? p.deaths : null,
                p.ShouldSerializeassists() ? p.assists : null)).ToArray());
    }
}
