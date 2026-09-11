using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamKit2;

namespace Dota2MmrCollector;

internal static class StratzLogin
{
    private static string TokenPath(uint accountId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Dota2MmrCollector", $"stratz-{accountId}.bin");

    public static string? Load(uint accountId)
    {
        try
        {
            var path = TokenPath(accountId);
            return File.Exists(path) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(
                File.ReadAllBytes(path), BitConverter.GetBytes(accountId), DataProtectionScope.CurrentUser)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException) { return null; }
    }

    public static void Save(uint accountId, string token)
    {
        var path = TokenPath(accountId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".tmp", ProtectedData.Protect(Encoding.UTF8.GetBytes(token),
            BitConverter.GetBytes(accountId), DataProtectionScope.CurrentUser));
        File.Move(path + ".tmp", path, true);
    }

    public static async Task<string?> TrySteamAsync(SteamClient steam, string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var token = await steam.Authentication.GenerateAccessTokenForAppAsync(steam.SteamID!, refreshToken, false)
                .WaitAsync(timeout.Token);
            var cookies = new CookieContainer();
            cookies.Add(new Uri("https://steamcommunity.com"), new Cookie("steamLoginSecure",
                Uri.EscapeDataString($"{steam.SteamID!.ConvertToUInt64()}||{token.AccessToken}"), "/") { Secure = true, HttpOnly = true });
            cookies.Add(new Uri("https://steamcommunity.com"), new Cookie("sessionid", Convert.ToHexString(RandomNumberGenerator.GetBytes(12)), "/") { Secure = true });
            using var handler = new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Dota2MmrReconstructor/0.5.5");
            return await FollowOpenIdAsync(http, cookies, timeout.Token);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException
            or InvalidOperationException or SteamKit2.Authentication.AuthenticationException or UriFormatException)
        {
            // Authentication URLs and exception messages can contain tokens. Only log a fixed status.
            Console.WriteLine("未能自动取得STRATZ Token，请按窗口提示从网页的“我的 Tokens / My Tokens”复制，或暂不获取综合表现评分。");
            return null;
        }
    }

    internal static bool AllowedRedirect(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443
        && uri.UserInfo.Length == 0 && uri.Host is "steamcommunity.com" or "api.stratz.com" or "stratz.com";

    internal static async Task<string?> FollowOpenIdAsync(HttpClient http, CookieContainer cookies, CancellationToken cancellationToken)
    {
        var uri = new Uri("https://api.stratz.com/api/v1/user/steam?returnUrl=https%3A%2F%2Fstratz.com%2Fapi");
        Dictionary<string, string>? form = null;
        var submitted = false;
        for (var hop = 0; hop < 10; hop++)
        {
            if (!AllowedRedirect(uri)) return null;
            using var request = new HttpRequestMessage(form is null ? HttpMethod.Get : HttpMethod.Post, uri);
            if (form is not null) request.Content = new FormUrlEncodedContent(form);
            using var response = await http.SendAsync(request, cancellationToken);
            form = null;
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = new Uri(uri, location);
                continue;
            }
            if (!response.IsSuccessStatusCode) return null;
            foreach (var host in new[] { "https://stratz.com", "https://api.stratz.com" })
                if (ExtractUserToken(cookies.GetCookies(new Uri(host))["user"]?.Value) is { } personalToken) return personalToken;
            if (uri.Host != "steamcommunity.com" || uri.AbsolutePath != "/openid/login" || submitted) return null;
            form = ParseOpenIdForm(await response.Content.ReadAsStringAsync(cancellationToken), uri);
            if (form is null) return null;
            submitted = true;
        }
        return null;
    }

    internal static string? ExtractUserToken(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return null;
        using var doc = JsonDocument.Parse(Uri.UnescapeDataString(cookie));
        // Anonymous website tokens are intentionally excluded.
        return doc.RootElement.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(token.GetString()) ? token.GetString() : null;
    }

    internal static Dictionary<string, string>? ParseOpenIdForm(string html, Uri page)
    {
        foreach (Match form in Regex.Matches(html, @"<form\b([^>]*)>(.*?)</form>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var attrs = Attributes(form.Groups[1].Value);
            if (attrs.GetValueOrDefault("id") != "openidForm" || !attrs.TryGetValue("action", out var action)) continue;
            var target = new Uri(page, action);
            if (target.Scheme != "https" || target.Host != "steamcommunity.com" || target.AbsolutePath != "/openid/login"
                || target.Port != 443 || target.UserInfo.Length != 0) return null;
            var fields = new Dictionary<string, string>();
            foreach (Match input in Regex.Matches(form.Groups[2].Value, @"<input\b([^>]*)>", RegexOptions.IgnoreCase))
            {
                var a = Attributes(input.Groups[1].Value);
                if (a.GetValueOrDefault("type") == "hidden" && a.TryGetValue("name", out var name))
                    fields[name] = a.GetValueOrDefault("value", "");
            }
            if (!fields.ContainsKey("openidparams") || !fields.ContainsKey("nonce")) return null;
            fields["action"] = "steam_openid_login";
            return fields;
        }
        return null;
    }

    private static Dictionary<string, string> Attributes(string text) => Regex.Matches(text, "([\\w-]+)\\s*=\\s*([\"'])(.*?)\\2", RegexOptions.Singleline)
        .Cast<Match>().GroupBy(m => m.Groups[1].Value.ToLowerInvariant())
        .ToDictionary(g => g.Key, g => WebUtility.HtmlDecode(g.Last().Groups[3].Value));

    public static Task<string?> AskTokenAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var window = new Form { Text = "从 STRATZ 的“我的 Tokens”复制 Token", ClientSize = new Size(700, 410),
                StartPosition = FormStartPosition.CenterScreen, Font = new Font("Microsoft YaHei UI", 10),
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
            var label = new Label { Text = "综合表现评分需要 STRATZ Token。自动获取未完成，请按下面操作：\n\n" +
                "1. 点击“打开 STRATZ 网页”，使用 Steam 登录。\n\n" +
                "2. 找到“我的 Tokens / My Tokens”（也可能显示“My Token”）。\n\n" +
                "3. 复制 Default Token（默认 Token）中的整串字符。\n\n" +
                "4. 回到本窗口，在下面按 Ctrl+V 粘贴，再点击“继续获取评分”。",
                Location = new Point(20, 16), Size = new Size(660, 194) };
            var inputLabel = new Label { Text = "粘贴网页里的 Token（一长串字母和数字）：",
                Location = new Point(20, 218), Size = new Size(660, 25) };
            var input = new TextBox { UseSystemPasswordChar = true, Location = new Point(20, 248), Width = 660,
                PlaceholderText = "在此按 Ctrl+V 粘贴 Token" };
            var note = new Label { Text = "Token会加密保存在本机，下次通常不用重复填写。\n“暂不获取评分”会先生成其他六项榜单，已下载的数据会保留。",
                Location = new Point(20, 292), Size = new Size(660, 45), ForeColor = Color.DimGray };
            var open = new Button { Text = "打开 STRATZ 网页", Location = new Point(20, 352), Size = new Size(170, 38) };
            open.Click += (_, _) => Process.Start(new ProcessStartInfo("https://stratz.com/api") { UseShellExecute = true });
            var use = new Button { Text = "继续获取评分", Location = new Point(320, 352), Size = new Size(170, 38), DialogResult = DialogResult.OK, Enabled = false };
            input.TextChanged += (_, _) => use.Enabled = !string.IsNullOrWhiteSpace(input.Text);
            var skip = new Button { Text = "暂不获取评分", Location = new Point(510, 352), Size = new Size(170, 38), DialogResult = DialogResult.Cancel };
            window.Controls.AddRange([label, inputLabel, input, note, open, use, skip]);
            window.AcceptButton = use;
            window.CancelButton = skip;
            using var timer = new System.Windows.Forms.Timer { Interval = 300 };
            timer.Tick += (_, _) => { if (cancellationToken.IsCancellationRequested) window.Close(); };
            timer.Start();
            var result = window.ShowDialog() == DialogResult.OK ? input.Text.Trim() : null;
            input.Clear();
            if (result?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true) result = result[7..].Trim();
            completion.TrySetResult(string.IsNullOrWhiteSpace(result) ? null : result);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
