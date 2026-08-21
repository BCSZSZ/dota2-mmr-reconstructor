using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Discovery;
using SteamKit2.GC;
using SteamKit2.GC.Dota.Internal;
using SteamKit2.Internal;

namespace Dota2MmrCollector;

internal static class Program
{
    private const int AppId = 570;
    private const int DefaultHistoryMatches = 5_000;
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);

    private static SteamClient? steamClient;
    private static SteamUser? steamUser;
    private static SteamGameCoordinator? coordinator;
    private static CallbackManager? manager;
    private static SteamQrWindow? qrWindow;

    private static readonly Dictionary<ulong, MatchHistoryGameObservation> matchHistoryById = [];
    private static readonly List<CurrentRankObservation> rankUpdates = [];
    private static CurrentRankObservation? currentRank;
    private static CollectorOptions options = default!;
    private static uint accountId;
    private static uint cachedAccountId;
    private static int cachedHistoryMatchesAtStart;
    private static int matchHistoryPagesRequested;
    private static ulong cachedNewestMatchId;
    private static ulong cachedOldestMatchId;
    private static ulong matchHistoryCursor;
    private static bool matchHistoryRequestPending;
    private static bool matchHistoryCaughtUpWithCache;
    private static bool matchHistoryFinished;
    private static string? matchHistoryError;
    private static bool rankRequestFinished;
    private static bool gcReady;
    private static volatile bool isRunning;
    private static bool outputWritten;
    private static bool disconnectRequested;
    private static bool reconnectRequested;
    private static int reconnectAttempts;
    private static string? loginAccountName;
    private static string? loginRefreshToken;
    private static DateTime rankRequestedAtUtc;
    private static DateTime historyRequestedAtUtc;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (!TryParseArguments(args, out options, out var showHelp))
        {
            if (showHelp)
            {
                PrintUsage();
                return 0;
            }

            PrintUsage();
            return 1;
        }

        var exitCode = Run();
        if (options.Interactive)
        {
            Console.WriteLine();
            Console.Write("按回车关闭窗口……");
            Console.ReadLine();
        }

        return exitCode;
    }

    private static int Run()
    {
        Console.WriteLine("Dota 2 MMR Collector");
        Console.WriteLine($"目标历史行数：{options.HistoryMatches:N0}");
        Console.WriteLine($"原始输出：{options.OutputPath}");
        Console.WriteLine($"断点缓存：{options.HistoryCachePath}");
        Console.WriteLine();

        if (!LoadMatchHistoryCache())
        {
            return 1;
        }

        if (Process.GetProcessesByName("dota2").Length > 0)
        {
            Console.Error.WriteLine(
                "Dota 2 正在运行。请先退出游戏；Valve 每个账号只允许一个 Dota GC 会话。");
            return 2;
        }

        matchHistoryFinished = options.HistoryMatches == 0;
        var stateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Dota2MmrCollector");
        Directory.CreateDirectory(stateDirectory);
        var cellIdPath = Path.Combine(stateDirectory, "cellid.txt");
        var serverListPath = Path.Combine(stateDirectory, "servers_list.bin");
        var cellId = File.Exists(cellIdPath)
            && uint.TryParse(File.ReadAllText(cellIdPath), out var savedCellId)
                ? savedCellId
                : 0;
        var configuration = SteamConfiguration.Create(builder =>
            builder.WithCellID(cellId)
                .WithServerListProvider(new FileStorageServerListProvider(serverListPath)));

        steamClient = new SteamClient(configuration);
        steamUser = steamClient.GetHandler<SteamUser>();
        coordinator = steamClient.GetHandler<SteamGameCoordinator>();
        manager = new CallbackManager(steamClient);
        manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        manager.Subscribe<SteamUser.LoggedOnCallback>(callback => OnLoggedOn(callback, cellIdPath));
        manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
        manager.Subscribe<SteamGameCoordinator.MessageCallback>(OnGcMessage);
        qrWindow = new SteamQrWindow(OnQrWindowClosed);

        isRunning = true;
        Console.WriteLine("正在连接 Steam；二维码会在独立窗口显示，不会保存密码或登录令牌。");
        steamClient.Connect();
        while (isRunning)
        {
            manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            CheckResponseTimeouts();
        }

        if (!outputWritten && accountId != 0)
        {
            WriteOutput("connection_ended_before_collection_completed");
        }

        qrWindow.Close();
        return outputWritten && matchHistoryError is null && currentRank is not null ? 0 : 1;
    }

    private static bool TryParseArguments(
        string[] args,
        out CollectorOptions parsedOptions,
        out bool showHelp)
    {
        showHelp = false;
        var interactive = args.Length == 0;
        var defaultDirectory = interactive ? AppContext.BaseDirectory : Environment.CurrentDirectory;
        var outputPath = Path.GetFullPath(Path.Combine(defaultDirectory, "gc-collection.json"));
        var cachePath = Path.GetFullPath(Path.Combine(defaultDirectory, "gc-match-history-cache.json"));
        uint? expectedAccountId = null;
        var historyMatches = DefaultHistoryMatches;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "-h" or "--help")
            {
                parsedOptions = default!;
                showHelp = true;
                return false;
            }

            if (argument == "--output")
            {
                if (!TryReadValue(args, ref index, argument, out var value))
                {
                    parsedOptions = default!;
                    return false;
                }

                outputPath = Path.GetFullPath(value);
                continue;
            }

            if (argument == "--history-cache")
            {
                if (!TryReadValue(args, ref index, argument, out var value))
                {
                    parsedOptions = default!;
                    return false;
                }

                cachePath = Path.GetFullPath(value);
                continue;
            }

            if (argument == "--account-id")
            {
                if (!TryReadValue(args, ref index, argument, out var value)
                    || !uint.TryParse(value, out var parsedAccountId)
                    || parsedAccountId == 0)
                {
                    Console.Error.WriteLine("--account-id 需要有效的 Steam ID32。");
                    parsedOptions = default!;
                    return false;
                }

                expectedAccountId = parsedAccountId;
                continue;
            }

            if (argument == "--history-matches")
            {
                if (!TryReadValue(args, ref index, argument, out var value)
                    || !int.TryParse(value, out historyMatches)
                    || historyMatches < 0)
                {
                    Console.Error.WriteLine("--history-matches 需要非负整数。");
                    parsedOptions = default!;
                    return false;
                }

                continue;
            }

            Console.Error.WriteLine($"未知参数：{argument}");
            parsedOptions = default!;
            return false;
        }

        parsedOptions = new CollectorOptions(
            outputPath,
            expectedAccountId,
            historyMatches,
            cachePath,
            interactive);
        return true;
    }

    private static bool TryReadValue(
        string[] args,
        ref int index,
        string argument,
        out string value)
    {
        if (++index < args.Length)
        {
            value = args[index];
            return true;
        }

        Console.Error.WriteLine($"{argument} 缺少参数值。");
        value = string.Empty;
        return false;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "Dota2MmrCollector [--account-id <ID32>] [--history-matches <count>] " +
            "[--output <json-path>] [--history-cache <json-path>]");
        Console.WriteLine("双击运行时默认下载 5,000 行，并把输出写在 EXE 所在目录。");
        Console.WriteLine("history-matches 是断点缓存最终保留的目标总行数；重复运行会先复用缓存。");
    }

    private static bool LoadMatchHistoryCache()
    {
        if (options.HistoryMatches == 0 || !File.Exists(options.HistoryCachePath))
        {
            matchHistoryCaughtUpWithCache = true;
            return true;
        }

        try
        {
            var cache = JsonSerializer.Deserialize<MatchHistoryCache>(
                File.ReadAllText(options.HistoryCachePath));
            if (cache is null || cache.SchemaVersion != 1)
            {
                Console.Error.WriteLine($"不支持的缓存格式：{options.HistoryCachePath}");
                return false;
            }

            if (options.ExpectedAccountId is not null
                && cache.AccountId != options.ExpectedAccountId)
            {
                Console.Error.WriteLine(
                    $"缓存属于账号 {cache.AccountId}，不是请求的账号 {options.ExpectedAccountId}。");
                return false;
            }

            cachedAccountId = cache.AccountId;
            foreach (var match in cache.Matches)
            {
                if (match.MatchId.Present && match.MatchId.Value != 0)
                {
                    matchHistoryById[match.MatchId.Value] = match;
                }
            }

            cachedHistoryMatchesAtStart = matchHistoryById.Count;
            if (cachedHistoryMatchesAtStart == 0)
            {
                matchHistoryCaughtUpWithCache = true;
            }
            else
            {
                var ordered = OrderedMatchHistory().ToArray();
                cachedNewestMatchId = ordered[0].MatchId.Value;
                cachedOldestMatchId = ordered[^1].MatchId.Value;
            }

            Console.WriteLine(
                $"已从 {options.HistoryCachePath} 载入 {cachedHistoryMatchesAtStart:N0} 行缓存。");
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"无法读取缓存 {options.HistoryCachePath}：{exception.GetType().Name}");
            return false;
        }
    }

    private static void SaveMatchHistoryCache()
    {
        if (options.HistoryMatches == 0 || accountId == 0)
        {
            return;
        }

        EnsureParentDirectory(options.HistoryCachePath);
        var cache = new MatchHistoryCache(
            1,
            accountId,
            DateTimeOffset.UtcNow,
            OrderedMatchHistory().ToList());
        WriteJsonAtomically(options.HistoryCachePath, cache);
    }

    private static IEnumerable<MatchHistoryGameObservation> OrderedMatchHistory() =>
        matchHistoryById.Values
            .OrderByDescending(match => match.StartTime.Present ? match.StartTime.Value : 0)
            .ThenByDescending(match => match.MatchId.Value);

    private static async void OnConnected(SteamClient.ConnectedCallback callback)
    {
        if (steamClient is null || steamUser is null)
        {
            return;
        }

        try
        {
            if (loginAccountName is not null && loginRefreshToken is not null)
            {
                Console.WriteLine("Steam 连接已切换；使用内存中的本次会话令牌重连。");
                steamUser.LogOn(new SteamUser.LogOnDetails
                {
                    Username = loginAccountName,
                    AccessToken = loginRefreshToken,
                });
                return;
            }

            var authSession = await steamClient.Authentication.BeginAuthSessionViaQRAsync(
                new AuthSessionDetails { IsPersistentSession = false });
            authSession.ChallengeURLChanged = () => DrawQrCode(authSession);
            DrawQrCode(authSession);

            var pollResponse = await authSession.PollingWaitForResultAsync();
            qrWindow?.Close();
            Console.WriteLine($"Steam 已批准账号 '{pollResponse.AccountName}' 的本次登录。");
            loginAccountName = pollResponse.AccountName;
            loginRefreshToken = pollResponse.RefreshToken;
            steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = loginAccountName,
                AccessToken = loginRefreshToken,
            });
        }
        catch (TaskCanceledException) when (loginRefreshToken is not null)
        {
            Console.WriteLine("Steam CM 切换取消了旧二维码轮询；继续使用已批准的内存会话。");
        }
        catch (Exception exception)
        {
            qrWindow?.Close();
            Console.Error.WriteLine($"Steam 二维码认证失败：{exception.GetType().Name}");
            isRunning = false;
            steamClient.Disconnect();
        }
    }

    private static void DrawQrCode(QrAuthSession authSession)
    {
        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(
            authSession.ChallengeURL,
            QRCodeGenerator.ECCLevel.L);
        using var qrCode = new PngByteQRCode(qrData);
        qrWindow?.ShowQr(qrCode.GetGraphic(12));
        Console.WriteLine("Steam 二维码窗口已打开或刷新。");
    }

    private static void OnQrWindowClosed()
    {
        Console.Error.WriteLine("二维码窗口已关闭，取消本次登录。");
        isRunning = false;
        steamClient?.Disconnect();
    }

    private static async void OnLoggedOn(
        SteamUser.LoggedOnCallback callback,
        string cellIdPath)
    {
        if (steamClient is null || steamUser is null || coordinator is null)
        {
            return;
        }

        if (callback.Result != EResult.OK)
        {
            if ((callback.Result is EResult.TryAnotherCM or EResult.ServiceUnavailable)
                && reconnectAttempts < 3)
            {
                reconnectAttempts++;
                reconnectRequested = true;
                Console.WriteLine($"Steam 请求切换 CM（{callback.Result}），重试 {reconnectAttempts}/3。");
                steamClient.Disconnect();
                return;
            }

            Console.Error.WriteLine($"Steam 登录失败：{callback.Result} / {callback.ExtendedResult}");
            ClearLoginToken();
            isRunning = false;
            steamClient.Disconnect();
            return;
        }

        var steamId = steamUser.SteamID;
        if (steamId is null)
        {
            Console.Error.WriteLine("Steam 登录成功，但响应中没有 SteamID。");
            isRunning = false;
            steamClient.Disconnect();
            return;
        }

        accountId = steamId.AccountID;
        if (options.ExpectedAccountId is not null && accountId != options.ExpectedAccountId)
        {
            Console.Error.WriteLine(
                $"扫码账号 ID32 {accountId} 与请求账号 {options.ExpectedAccountId} 不一致；不会写入输出。");
            isRunning = false;
            steamClient.Disconnect();
            return;
        }

        if (cachedAccountId != 0 && cachedAccountId != accountId)
        {
            Console.Error.WriteLine(
                $"缓存属于账号 {cachedAccountId}，扫码账号是 {accountId}；不会写入输出。");
            isRunning = false;
            steamClient.Disconnect();
            return;
        }

        File.WriteAllText(cellIdPath, callback.CellID.ToString());
        Console.WriteLine($"已登录 ID32 {accountId}，正在启动 Dota GC 会话……");
        var playGame = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
        playGame.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
        {
            game_id = new GameID(AppId),
        });
        steamClient.Send(playGame);

        await Task.Delay(TimeSpan.FromSeconds(5));
        var hello = new ClientGCMsgProtobuf<SteamKit2.GC.Dota.Internal.CMsgClientHello>(
            (uint)EGCBaseClientMsg.k_EMsgGCClientHello);
        hello.Body.engine = ESourceEngine.k_ESE_Source2;
        coordinator.Send(hello, AppId);
    }

    private static void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        if (reconnectRequested && steamClient is not null)
        {
            reconnectRequested = false;
            Console.WriteLine("正在切换 Steam 连接管理器……");
            Thread.Sleep(TimeSpan.FromSeconds(1));
            steamClient.Connect();
            return;
        }

        if (!disconnectRequested)
        {
            Console.Error.WriteLine("原始数据下载完成前与 Steam 断开连接。");
        }

        isRunning = false;
    }

    private static void OnLoggedOff(SteamUser.LoggedOffCallback callback)
    {
        Console.WriteLine($"Steam 已登出：{callback.Result}");
        steamClient?.Disconnect();
    }

    private static void OnGcMessage(SteamGameCoordinator.MessageCallback callback)
    {
        switch (callback.EMsg)
        {
            case (uint)EGCBaseClientMsg.k_EMsgGCClientWelcome:
                OnClientWelcome(callback.Message);
                break;
            case (uint)EDOTAGCMsg.k_EMsgGCToClientRankResponse:
                OnRankResponse(callback.Message, "request_response");
                break;
            case (uint)EDOTAGCMsg.k_EMsgGCToClientRankUpdate:
                OnRankUpdate(callback.Message);
                break;
            case (uint)EDOTAGCMsg.k_EMsgDOTAGetPlayerMatchHistoryResponse:
                OnMatchHistory(callback.Message);
                break;
        }
    }

    private static void OnClientWelcome(IPacketGCMsg packet)
    {
        var welcome = new ClientGCMsgProtobuf<CMsgClientWelcome>(packet);
        Console.WriteLine($"已连接 Dota GC（版本 {welcome.Body.version}）。");
        if (gcReady)
        {
            return;
        }

        gcReady = true;
        SendRankRequest();
        SendMatchHistoryRequest();
    }

    private static void SendRankRequest()
    {
        if (coordinator is null)
        {
            return;
        }

        var request = new ClientGCMsgProtobuf<CMsgClientToGCRankRequest>(
            (uint)EDOTAGCMsg.k_EMsgClientToGCRankRequest);
        request.Body.rank_type = ERankType.k_ERankType_RankedGlicko;
        coordinator.Send(request, AppId);
        rankRequestedAtUtc = DateTime.UtcNow;
        Console.WriteLine("已请求当前 RankedGlicko 原始 Rank payload。");
    }

    private static void OnRankResponse(IPacketGCMsg packet, string source)
    {
        var message = new ClientGCMsgProtobuf<CMsgGCToClientRankResponse>(packet);
        var observation = ObserveRank(message.Body, source);
        if (source == "request_response")
        {
            currentRank = observation;
            rankRequestFinished = true;
            Console.WriteLine(
                $"Current Rank：result={observation.Result.Value}, " +
                $"rank_value={observation.RankValue.Value}, " +
                $"rank_data1={observation.RankData1.Value}, " +
                $"rank_data2={observation.RankData2.Value}, " +
                $"rank_data3={observation.RankData3.Value}。");
        }
        else
        {
            rankUpdates.Add(observation);
        }

        FinishIfComplete();
    }

    private static void OnRankUpdate(IPacketGCMsg packet)
    {
        var message = new ClientGCMsgProtobuf<CMsgGCToClientRankUpdate>(packet);
        if (message.Body.rank_info is null)
        {
            return;
        }

        var observation = ObserveRank(message.Body.rank_info, "unsolicited_update") with
        {
            RankType = Observe(message.Body.ShouldSerializerank_type(), (int)message.Body.rank_type),
        };
        rankUpdates.Add(observation);
    }

    private static CurrentRankObservation ObserveRank(
        CMsgGCToClientRankResponse rank,
        string source) => new(
        source,
        Observe(true, (int)ERankType.k_ERankType_RankedGlicko),
        Observe(rank.ShouldSerializeresult(), (int)rank.result),
        rank.result.ToString(),
        Observe(rank.ShouldSerializerank_value(), rank.rank_value),
        Observe(rank.ShouldSerializerank_data1(), rank.rank_data1),
        Observe(rank.ShouldSerializerank_data2(), rank.rank_data2),
        Observe(rank.ShouldSerializerank_data3(), rank.rank_data3));

    private static void SendMatchHistoryRequest()
    {
        if (coordinator is null
            || options.HistoryMatches == 0
            || matchHistoryFinished
            || matchHistoryRequestPending)
        {
            FinishIfComplete();
            return;
        }

        var request = new ClientGCMsgProtobuf<CMsgDOTAGetPlayerMatchHistory>(
            (uint)EDOTAGCMsg.k_EMsgDOTAGetPlayerMatchHistory);
        request.Body.account_id = accountId;
        request.Body.matches_requested = 20;
        if (matchHistoryCursor != 0)
        {
            request.Body.start_at_match_id = matchHistoryCursor;
        }

        coordinator.Send(request, AppId);
        matchHistoryRequestPending = true;
        matchHistoryPagesRequested++;
        historyRequestedAtUtc = DateTime.UtcNow;
        Console.WriteLine(
            $"Match History 第 {matchHistoryPagesRequested} 页 " +
            $"(cursor={matchHistoryCursor}, cached={matchHistoryById.Count}, " +
            $"target={options.HistoryMatches})。");
    }

    private static void OnMatchHistory(IPacketGCMsg packet)
    {
        if (!matchHistoryRequestPending || options.HistoryMatches == 0)
        {
            return;
        }

        var message = new ClientGCMsgProtobuf<CMsgDOTAGetPlayerMatchHistoryResponse>(packet);
        matchHistoryRequestPending = false;
        var responseMatches = message.Body.matches;
        var sawCachedNewest = cachedNewestMatchId != 0
            && responseMatches.Any(match =>
                match.ShouldSerializematch_id() && match.match_id == cachedNewestMatchId);

        foreach (var match in responseMatches)
        {
            var observation = ObserveMatchHistory(match);
            if (observation.MatchId.Present && observation.MatchId.Value != 0)
            {
                matchHistoryById[observation.MatchId.Value] = observation;
            }
        }

        if (matchHistoryPagesRequested % 10 == 0)
        {
            SaveMatchHistoryCache();
        }

        if (responseMatches.Count == 0)
        {
            CompleteMatchHistory(null);
            return;
        }

        if (!matchHistoryCaughtUpWithCache && sawCachedNewest)
        {
            matchHistoryCaughtUpWithCache = true;
            Console.WriteLine("已追到缓存中最新的一场比赛。");
        }

        if (matchHistoryCaughtUpWithCache
            && matchHistoryById.Count >= options.HistoryMatches)
        {
            CompleteMatchHistory(null);
            return;
        }

        if (responseMatches.Count < 20)
        {
            CompleteMatchHistory(null);
            return;
        }

        ulong nextCursor;
        if (matchHistoryCaughtUpWithCache
            && cachedOldestMatchId != 0
            && matchHistoryById.Count < options.HistoryMatches
            && sawCachedNewest)
        {
            nextCursor = cachedOldestMatchId;
            Console.WriteLine($"跳到缓存最老断点 {cachedOldestMatchId}，继续向前扩展。");
        }
        else
        {
            nextCursor = responseMatches
                .LastOrDefault(match => match.ShouldSerializematch_id() && match.match_id != 0)
                ?.match_id ?? 0;
        }

        if (nextCursor == 0 || nextCursor == matchHistoryCursor)
        {
            CompleteMatchHistory("pagination_cursor_did_not_advance");
            return;
        }

        matchHistoryCursor = nextCursor;
        Thread.Sleep(TimeSpan.FromSeconds(1));
        SendMatchHistoryRequest();
    }

    private static MatchHistoryGameObservation ObserveMatchHistory(
        CMsgDOTAGetPlayerMatchHistoryResponse.Match match) => new(
        new HistoryField<ulong>(match.ShouldSerializematch_id(), match.match_id),
        new HistoryField<uint>(match.ShouldSerializestart_time(), match.start_time),
        new HistoryField<int>(match.ShouldSerializehero_id(), match.hero_id),
        new HistoryField<bool>(match.ShouldSerializewinner(), match.winner),
        new HistoryField<uint>(match.ShouldSerializegame_mode(), match.game_mode),
        new HistoryField<int>(match.ShouldSerializerank_change(), match.rank_change),
        new HistoryField<uint>(match.ShouldSerializeprevious_rank(), match.previous_rank),
        new HistoryField<uint>(match.ShouldSerializelobby_type(), match.lobby_type),
        new HistoryField<bool>(match.ShouldSerializesolo_rank(), match.solo_rank),
        new HistoryField<bool>(match.ShouldSerializeabandon(), match.abandon),
        new HistoryField<uint>(match.ShouldSerializeduration(), match.duration));

    private static void CompleteMatchHistory(string? error)
    {
        matchHistoryRequestPending = false;
        matchHistoryFinished = true;
        matchHistoryError = error;
        SaveMatchHistoryCache();
        Console.WriteLine(
            $"Match History 完成：缓存 {matchHistoryById.Count:N0} 行，" +
            $"本次请求 {matchHistoryPagesRequested:N0} 页" +
            (error is null ? "。" : $"，错误={error}。"));
        FinishIfComplete();
    }

    private static void CheckResponseTimeouts()
    {
        if (!gcReady)
        {
            return;
        }

        if (!rankRequestFinished
            && rankRequestedAtUtc != default
            && DateTime.UtcNow - rankRequestedAtUtc >= ResponseTimeout)
        {
            currentRank = CurrentRankObservation.Timeout();
            rankRequestFinished = true;
            Console.Error.WriteLine("等待 Current Rank 响应超时。");
            FinishIfComplete();
        }

        if (matchHistoryRequestPending
            && historyRequestedAtUtc != default
            && DateTime.UtcNow - historyRequestedAtUtc >= ResponseTimeout)
        {
            CompleteMatchHistory("response_timeout");
        }
    }

    private static void FinishIfComplete()
    {
        if (!rankRequestFinished || !matchHistoryFinished)
        {
            return;
        }

        WriteOutput(null);
        disconnectRequested = true;
        steamClient?.Disconnect();
    }

    private static void WriteOutput(string? completionError)
    {
        if (outputWritten || accountId == 0)
        {
            return;
        }

        SaveMatchHistoryCache();
        var history = new MatchHistoryCollectionObservation(
            options.HistoryMatches,
            options.HistoryCachePath,
            cachedHistoryMatchesAtStart,
            matchHistoryPagesRequested,
            matchHistoryFinished,
            matchHistoryError,
            OrderedMatchHistory().ToArray());
        var output = new CollectorOutput(
            4,
            DateTimeOffset.UtcNow,
            accountId,
            ERankType.k_ERankType_RankedGlicko.ToString(),
            currentRank,
            rankUpdates,
            history,
            completionError,
            [
                "This file contains raw authenticated-account GC data; no low-Confidence MMR reconstruction was run by the collector.",
                "Present=false is different from a protobuf field explicitly returned with value 0.",
                "rank_data1/rank_data2/rank_data3 are preserved without interpreting them in the collector.",
                "Match History preserves Winner, lobby type, rank values, and protobuf presence for calibrated and uncalibrated matches.",
                "No Steam password or refresh token is written to disk.",
            ]);
        EnsureParentDirectory(options.OutputPath);
        WriteJsonAtomically(options.OutputPath, output);
        outputWritten = true;
        ClearLoginToken();
        Console.WriteLine($"原始 GC 数据已写入 {options.OutputPath}");
    }

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            }));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void ClearLoginToken()
    {
        loginAccountName = null;
        loginRefreshToken = null;
    }

    private static FieldObservation Observe(bool present, object value) =>
        new(present, value);
}

internal sealed record CollectorOptions(
    string OutputPath,
    uint? ExpectedAccountId,
    int HistoryMatches,
    string HistoryCachePath,
    bool Interactive);

internal sealed record FieldObservation(bool Present, object Value);

internal sealed record HistoryField<T>(bool Present, T Value);

internal sealed record MatchHistoryGameObservation(
    HistoryField<ulong> MatchId,
    HistoryField<uint> StartTime,
    HistoryField<int> HeroId,
    HistoryField<bool> Winner,
    HistoryField<uint> GameMode,
    HistoryField<int> RankChange,
    HistoryField<uint> PreviousRank,
    HistoryField<uint> LobbyType,
    HistoryField<bool> SoloRank,
    HistoryField<bool> Abandon,
    HistoryField<uint> Duration);

internal sealed record MatchHistoryCache(
    int SchemaVersion,
    uint AccountId,
    DateTimeOffset CapturedAtUtc,
    List<MatchHistoryGameObservation> Matches);

internal sealed record MatchHistoryCollectionObservation(
    int RequestedTarget,
    string CachePath,
    int CachedRowsAtStart,
    int PagesRequested,
    bool Finished,
    string? Error,
    IReadOnlyList<MatchHistoryGameObservation> Matches);

internal sealed record CurrentRankObservation(
    string Source,
    FieldObservation RankType,
    FieldObservation Result,
    string ResultName,
    FieldObservation RankValue,
    FieldObservation RankData1,
    FieldObservation RankData2,
    FieldObservation RankData3)
{
    public static CurrentRankObservation Timeout() => new(
        "request_timeout",
        new FieldObservation(true, (int)ERankType.k_ERankType_RankedGlicko),
        new FieldObservation(false, 0),
        "timeout",
        new FieldObservation(false, 0),
        new FieldObservation(false, 0),
        new FieldObservation(false, 0),
        new FieldObservation(false, 0));
}

internal sealed record CollectorOutput(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    uint AccountId,
    string RequestedRankType,
    CurrentRankObservation? CurrentRank,
    IReadOnlyList<CurrentRankObservation> RankUpdates,
    MatchHistoryCollectionObservation MatchHistory,
    string? CompletionError,
    IReadOnlyList<string> Notes);
