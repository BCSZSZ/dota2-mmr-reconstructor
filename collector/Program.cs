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
    private static bool suppressOutput;
    private static bool disconnectRequested;
    private static bool reconnectRequested;
    private static int reconnectAttempts;
    private static bool cacheLoaded;
    private static string? loginAccountName;
    private static string? loginRefreshToken;
    private static string? reconstructionError;
    private static ReconstructionOutput? reconstructionOutput;
    private static DateTime rankRequestedAtUtc;
    private static DateTime historyRequestedAtUtc;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (!TryParseArguments(args, out options, out var showHelp, out var cancelled))
        {
            if (showHelp)
            {
                PrintUsage();
                return 0;
            }

            if (cancelled)
            {
                return 0;
            }

            PrintUsage();
            return 1;
        }

        int exitCode;
        try
        {
            exitCode = options.ExistingCollectionPath is null
                ? Run()
                : RunExistingReconstruction();
        }
        finally
        {
            ClearEphemeralCredentials();
            ClearLoginToken();
        }
        if (options.Interactive)
        {
            CompletionDialog.Show(
                exitCode == 0,
                reconstructionOutput?.OutputDirectory
                    ?? Path.GetDirectoryName(options.OutputPath)
                    ?? options.OutputRoot
                    ?? AppContext.BaseDirectory,
                reconstructionError,
                options.GenerateReconstruction
                    && (options.ExistingCollectionPath is not null || options.HistoryMatches > 0));
        }

        return exitCode;
    }

    private static int RunExistingReconstruction()
    {
        try
        {
            var inputPath = Path.GetFullPath(options.ExistingCollectionPath!);
            var outputDirectory = options.OutputRoot is not null
                ? Path.GetFullPath(options.OutputRoot)
                : Path.Combine(Path.GetDirectoryName(inputPath)!, "mmr-reconstruction");
            reconstructionOutput = MmrReconstructor.Run(
                inputPath,
                outputDirectory,
                options.ExpectedAccountId);
            Console.WriteLine(
                $"C# 曲线生成完成：{reconstructionOutput.Matches:N0} 场 " +
                $"({reconstructionOutput.ActualMatches:N0} 真实 / " +
                $"{reconstructionOutput.ModeledMatches:N0} 拟合)。");
            Console.WriteLine($"交互 HTML：{reconstructionOutput.HtmlPath}");
            return 0;
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or InvalidDataException
            or UnauthorizedAccessException or InvalidOperationException
            or System.Runtime.InteropServices.ExternalException)
        {
            reconstructionError = $"C# 曲线生成失败：{exception.Message}";
            Console.Error.WriteLine(reconstructionError);
            return 1;
        }
    }

    private static int Run()
    {
        Console.WriteLine("Dota 2 MMR Reconstructor");
        Console.WriteLine($"目标历史行数：{options.HistoryMatches:N0}");
        Console.WriteLine(options.ResolvePathsAfterLogin
            ? $"输出根目录：{options.OutputRoot}（登录后按账号创建子目录）"
            : $"原始输出：{options.OutputPath}");
        Console.WriteLine(options.GenerateReconstruction
            ? "下载后：使用 C# 生成完整 CSV/JSON/TXT/MD/XLSX/SVG/PNG/HTML。"
            : "下载后：仅保留原始 GC 数据。 ");
        Console.WriteLine();

        if (Process.GetProcessesByName("dota2").Length > 0)
        {
            reconstructionError =
                "Dota 2 正在运行。请先退出游戏；Valve 每个账号只允许一个 Dota GC 会话。";
            Console.Error.WriteLine(reconstructionError);
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
        qrWindow = options.Login.Mode == SteamLoginMode.QrCode
            ? new SteamQrWindow(OnQrWindowClosed)
            : null;

        isRunning = true;
        Console.WriteLine(options.Login.Mode == SteamLoginMode.Credentials
            ? "正在连接 Steam；将使用一次性用户名/密码登录，随后弹出 Steam Guard 验证码窗口。"
            : "正在连接 Steam；二维码会在独立窗口显示，不会保存密码或登录令牌。");
        steamClient.Connect();
        while (isRunning)
        {
            manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            CheckResponseTimeouts();
        }

        if (!outputWritten && !suppressOutput && accountId != 0)
        {
            WriteOutput("connection_ended_before_collection_completed");
        }

        qrWindow?.Close();
        return outputWritten
            && matchHistoryError is null
            && currentRank is not null
            && (!options.GenerateReconstruction || reconstructionError is null)
                ? 0
                : 1;
    }

    private static bool TryParseArguments(
        string[] args,
        out CollectorOptions parsedOptions,
        out bool showHelp,
        out bool cancelled)
    {
        showHelp = false;
        cancelled = false;
        var interactive = args.Length == 0;
        if (interactive)
        {
            var selection = CollectorSetupWindow.AskUser();
            if (selection is null)
            {
                parsedOptions = default!;
                cancelled = true;
                return false;
            }

            var placeholderDirectory = Path.Combine(selection.OutputRoot, "_account_pending");
            parsedOptions = new CollectorOptions(
                Path.Combine(placeholderDirectory, "gc-collection.json"),
                selection.ExpectedAccountId,
                selection.HistoryMatches,
                Path.Combine(placeholderDirectory, "gc-match-history-cache.json"),
                true,
                selection.GenerateReconstruction,
                selection.OutputRoot,
                true,
                null,
                selection.Login);
            return true;
        }

        var defaultDirectory = Environment.CurrentDirectory;
        var outputPath = Path.GetFullPath(Path.Combine(defaultDirectory, "gc-collection.json"));
        var cachePath = Path.GetFullPath(Path.Combine(defaultDirectory, "gc-match-history-cache.json"));
        uint? expectedAccountId = null;
        var historyMatches = DefaultHistoryMatches;
        var generateReconstruction = true;
        var explicitOutputPath = false;
        var explicitCachePath = false;
        string? outputRoot = null;
        var resolvePathsAfterLogin = false;
        string? existingCollectionPath = null;

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
                explicitOutputPath = true;
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
                explicitCachePath = true;
                continue;
            }

            if (argument == "--account-id")
            {
                if (!TryReadValue(args, ref index, argument, out var value))
                {
                    parsedOptions = default!;
                    return false;
                }

                try
                {
                    expectedAccountId = CollectorSetupWindow.ParseAccountId(value);
                }
                catch (FormatException exception)
                {
                    Console.Error.WriteLine($"--account-id：{exception.Message}");
                    parsedOptions = default!;
                    return false;
                }

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

            if (argument == "--output-dir")
            {
                if (!TryReadValue(args, ref index, argument, out var value))
                {
                    parsedOptions = default!;
                    return false;
                }
                outputRoot = Path.GetFullPath(value);
                resolvePathsAfterLogin = existingCollectionPath is null;
                continue;
            }

            if (argument == "--raw-only")
            {
                generateReconstruction = false;
                continue;
            }

            if (argument == "--reconstruct-existing")
            {
                if (!TryReadValue(args, ref index, argument, out var value))
                {
                    parsedOptions = default!;
                    return false;
                }
                existingCollectionPath = Path.GetFullPath(value);
                generateReconstruction = true;
                resolvePathsAfterLogin = false;
                continue;
            }

            Console.Error.WriteLine($"未知参数：{argument}");
            parsedOptions = default!;
            return false;
        }

        if (resolvePathsAfterLogin && (explicitOutputPath || explicitCachePath))
        {
            Console.Error.WriteLine("--output-dir 不能与 --output 或 --history-cache 同时使用。");
            parsedOptions = default!;
            return false;
        }

        parsedOptions = new CollectorOptions(
            outputPath,
            expectedAccountId,
            historyMatches,
            cachePath,
            interactive,
            generateReconstruction,
            outputRoot,
            resolvePathsAfterLogin,
            existingCollectionPath,
            SteamLoginSelection.QrCode);
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
            "Dota2MmrReconstructor [--account-id <ID32|SteamID64>] " +
            "[--history-matches <count>] " +
            "[--output-dir <directory>] [--raw-only]");
        Console.WriteLine(
            "Dota2MmrReconstructor --reconstruct-existing <gc-collection.json> " +
            "[--output-dir <directory>]");
        Console.WriteLine("双击运行会显示设置窗口；默认下载 5,000 行并生成完整曲线和 HTML。");
        Console.WriteLine("history-matches 是断点缓存最终保留的目标总行数；重复运行会先复用缓存。");
        Console.WriteLine("account-id 同时接受 ID32 和个人账号 SteamID64，也可以在 GUI 中留空自动识别。");
        Console.WriteLine("用户名/密码/Steam Guard 验证码登录仅在 GUI 提供，敏感信息不会放入命令行。");
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

            if (options.Login.Mode == SteamLoginMode.Credentials)
            {
                await AuthenticateWithCredentialsAsync();
                return;
            }

            var authSession = await steamClient.Authentication.BeginAuthSessionViaQRAsync(
                new AuthSessionDetails { IsPersistentSession = false });
            authSession.ChallengeURLChanged = () => DrawQrCode(authSession);
            DrawQrCode(authSession);

            var pollResponse = await authSession.PollingWaitForResultAsync();
            CompleteAuthentication(pollResponse, "Steam 已批准本次二维码登录。");
        }
        catch (TaskCanceledException) when (loginRefreshToken is not null)
        {
            Console.WriteLine("Steam CM 切换取消了旧二维码轮询；继续使用已批准的内存会话。");
        }
        catch (OperationCanceledException)
        {
            qrWindow?.Close();
            reconstructionError = "Steam 登录已取消。";
            Console.Error.WriteLine(reconstructionError);
            ClearEphemeralCredentials();
            isRunning = false;
            steamClient.Disconnect();
        }
        catch (AuthenticationException exception)
        {
            qrWindow?.Close();
            reconstructionError = CredentialAuthenticationError(exception);
            Console.Error.WriteLine(reconstructionError);
            ClearEphemeralCredentials();
            isRunning = false;
            steamClient.Disconnect();
        }
        catch (Exception exception)
        {
            qrWindow?.Close();
            reconstructionError = $"Steam 认证失败：{exception.GetType().Name}";
            Console.Error.WriteLine(reconstructionError);
            ClearEphemeralCredentials();
            isRunning = false;
            steamClient.Disconnect();
        }
    }

    private static async Task AuthenticateWithCredentialsAsync()
    {
        if (steamClient is null || steamUser is null)
        {
            return;
        }

        var credentials = options.Login.Credentials
            ?? throw new InvalidOperationException("凭据登录已选择，但用户名或密码已不可用。");
        if (credentials.IsCleared)
        {
            throw new InvalidOperationException("一次性用户名和密码已经清除，请重新启动登录。");
        }

        Console.WriteLine("正在提交加密后的 Steam 登录凭据……");
        var details = new AuthSessionDetails
        {
            Username = credentials.Username,
            Password = credentials.Password,
            IsPersistentSession = false,
            Authenticator = new InteractiveSteamAuthenticator(),
        };

        try
        {
            var authSession = await steamClient.Authentication
                .BeginAuthSessionViaCredentialsAsync(details);
            details.Password = string.Empty;
            var pollResponse = await authSession.PollingWaitForResultAsync();
            CompleteAuthentication(pollResponse, "Steam 用户名/密码和验证码认证成功。");
        }
        finally
        {
            details.Username = string.Empty;
            details.Password = string.Empty;
        }
    }

    private static void CompleteAuthentication(AuthPollResult pollResponse, string message)
    {
        if (steamUser is null)
        {
            return;
        }

        qrWindow?.Close();
        loginAccountName = pollResponse.AccountName;
        loginRefreshToken = pollResponse.RefreshToken;
        ClearEphemeralCredentials();
        Console.WriteLine(message);
        steamUser.LogOn(new SteamUser.LogOnDetails
        {
            Username = loginAccountName,
            AccessToken = loginRefreshToken,
        });
    }

    private static string CredentialAuthenticationError(AuthenticationException exception)
    {
        if (options.Login.Mode != SteamLoginMode.Credentials)
        {
            return $"Steam 二维码认证失败：{exception.Result}";
        }

        return exception.Result switch
        {
            EResult.InvalidPassword => "Steam 登录失败：用户名或密码不正确。",
            EResult.TwoFactorCodeMismatch => "Steam 登录失败：Steam Guard 验证码不正确。",
            EResult.RateLimitExceeded => "Steam 登录尝试过多，请稍后再试。",
            _ => $"Steam 用户名/密码认证失败：{exception.Result}。",
        };
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
        reconstructionError = "二维码窗口已关闭，本次登录已取消。";
        Console.Error.WriteLine(reconstructionError);
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

            reconstructionError = $"Steam 登录失败：{callback.Result} / {callback.ExtendedResult}";
            Console.Error.WriteLine(reconstructionError);
            ClearLoginToken();
            isRunning = false;
            steamClient.Disconnect();
            return;
        }

        var steamId = steamUser.SteamID;
        if (steamId is null)
        {
            reconstructionError = "Steam 登录成功，但响应中没有 SteamID。";
            Console.Error.WriteLine(reconstructionError);
            isRunning = false;
            steamClient.Disconnect();
            return;
        }

        accountId = steamId.AccountID;
        if (options.ExpectedAccountId is not null && accountId != options.ExpectedAccountId)
        {
            reconstructionError =
                $"登录账号 ID32 {accountId} 与请求账号 {options.ExpectedAccountId} 不一致；不会写入输出。";
            Console.Error.WriteLine(reconstructionError);
            suppressOutput = true;
            isRunning = false;
            steamClient.Disconnect();
            return;
        }

        if (options.ResolvePathsAfterLogin)
        {
            var accountDirectory = Path.Combine(options.OutputRoot!, accountId.ToString());
            options = options with
            {
                OutputPath = Path.Combine(accountDirectory, "gc-collection.json"),
                HistoryCachePath = Path.Combine(accountDirectory, "gc-match-history-cache.json"),
            };
        }

        if (!cacheLoaded)
        {
            Console.WriteLine($"原始输出：{options.OutputPath}");
            Console.WriteLine($"断点缓存：{options.HistoryCachePath}");
            if (!LoadMatchHistoryCache())
            {
                reconstructionError = "无法载入现有 Match History 缓存。";
                suppressOutput = true;
                isRunning = false;
                steamClient.Disconnect();
                return;
            }
            cacheLoaded = true;
        }

        if (cachedAccountId != 0 && cachedAccountId != accountId)
        {
            reconstructionError =
                $"缓存属于账号 {cachedAccountId}，登录账号是 {accountId}；不会写入输出。";
            Console.Error.WriteLine(reconstructionError);
            suppressOutput = true;
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
            reconstructionError ??= "原始数据下载完成前与 Steam 断开连接。";
            Console.Error.WriteLine(reconstructionError);
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
        if (outputWritten || suppressOutput || accountId == 0)
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
                "This file contains raw authenticated-account GC data. Any reconstruction output is written separately and never replaces this file.",
                "Present=false is different from a protobuf field explicitly returned with value 0.",
                "rank_data1/rank_data2/rank_data3 are preserved without interpreting them in the collector.",
                "Match History preserves Winner, lobby type, rank values, and protobuf presence for calibrated and uncalibrated matches.",
                "No Steam password or refresh token is written to disk.",
            ]);
        EnsureParentDirectory(options.OutputPath);
        WriteJsonAtomically(options.OutputPath, output);
        outputWritten = true;
        Console.WriteLine($"原始 GC 数据已写入 {options.OutputPath}");

        if (options.GenerateReconstruction
            && options.HistoryMatches > 0
            && completionError is null
            && matchHistoryError is null)
        {
            try
            {
                var accountDirectory = Path.GetDirectoryName(options.OutputPath)
                    ?? throw new InvalidOperationException("原始输出路径没有父目录。");
                var reconstructionDirectory = Path.Combine(accountDirectory, "mmr-reconstruction");
                reconstructionOutput = MmrReconstructor.Run(
                    options.OutputPath,
                    reconstructionDirectory,
                    accountId);
                Console.WriteLine(
                    $"C# 曲线生成完成：{reconstructionOutput.Matches:N0} 场 " +
                    $"({reconstructionOutput.ActualMatches:N0} 真实 / " +
                    $"{reconstructionOutput.ModeledMatches:N0} 拟合)。");
                Console.WriteLine($"交互 HTML：{reconstructionOutput.HtmlPath}");
            }
            catch (Exception exception) when (
                exception is IOException or JsonException or InvalidDataException
                or UnauthorizedAccessException or InvalidOperationException
                or System.Runtime.InteropServices.ExternalException)
            {
                reconstructionError =
                    $"原始 GC 数据已经安全保存，但 C# 曲线生成失败：{exception.Message}";
                Console.Error.WriteLine(reconstructionError);
            }
        }

        ClearLoginToken();
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

    private static void ClearEphemeralCredentials() =>
        options.Login?.Credentials?.Clear();

    private static FieldObservation Observe(bool present, object value) =>
        new(present, value);
}

internal sealed record CollectorOptions(
    string OutputPath,
    uint? ExpectedAccountId,
    int HistoryMatches,
    string HistoryCachePath,
    bool Interactive,
    bool GenerateReconstruction,
    string? OutputRoot,
    bool ResolvePathsAfterLogin,
    string? ExistingCollectionPath,
    SteamLoginSelection Login);

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
