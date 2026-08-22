using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Dota2MmrCollector;

internal sealed record CollectorSetupSelection(
    uint? ExpectedAccountId,
    int HistoryMatches,
    string OutputRoot,
    bool GenerateReconstruction,
    SteamLoginSelection Login);

internal sealed class CollectorSetupWindow : Form
{
    private const ulong SteamId64IndividualBase = 76_561_197_960_265_728UL;

    private readonly TextBox accountText = new();
    private readonly RadioButton qrLoginRadio = new();
    private readonly RadioButton credentialLoginRadio = new();
    private readonly TextBox usernameText = new();
    private readonly TextBox passwordText = new();
    private readonly NumericUpDown historyMatches = new();
    private readonly TextBox outputText = new();
    private readonly CheckBox reconstructionCheck = new();

    public CollectorSetupSelection? Selection { get; private set; }

    private CollectorSetupWindow()
    {
        Text = "Dota 2 MMR 曲线生成器";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 720);
        MinimumSize = new Size(720, 680);
        Font = new Font("Microsoft YaHei UI", 10);
        FormBorderStyle = FormBorderStyle.Sizable;

        var heading = new Label
        {
            Text = "下载本人 GC 天梯记录并生成 MMR 曲线",
            Dock = DockStyle.Top,
            Height = 58,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, 16, FontStyle.Bold),
        };

        var explanation = new Label
        {
            Text = "Steam ID 可以留空，登录后会自动识别。填写 ID32 或 SteamID64 可防止登录错账号。\n" +
                   "场数是 GC 全部历史行数（含普通局），下载后才筛出天梯；运行前请完全退出 Dota 2。",
            Dock = DockStyle.Top,
            Height = 78,
            Padding = new Padding(24, 4, 24, 4),
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        accountText.PlaceholderText = "可选，例如 123456789 或 76561198083722517";
        accountText.Dock = DockStyle.Fill;

        qrLoginRadio.Text = "二维码（推荐，不输入密码）";
        qrLoginRadio.Checked = true;
        qrLoginRadio.AutoSize = true;
        credentialLoginRadio.Text = "用户名 + 密码 + Steam Guard 验证码（无扫码兼容）";
        credentialLoginRadio.AutoSize = true;
        var loginMethods = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };
        loginMethods.Controls.Add(qrLoginRadio);
        loginMethods.Controls.Add(credentialLoginRadio);
        qrLoginRadio.CheckedChanged += (_, _) => UpdateCredentialControls();
        credentialLoginRadio.CheckedChanged += (_, _) => UpdateCredentialControls();

        usernameText.PlaceholderText = "Steam 登录账户名（不是个人资料昵称）";
        usernameText.Dock = DockStyle.Fill;
        usernameText.Enabled = false;
        passwordText.PlaceholderText = "Steam 密码（仅保留在本次进程内）";
        passwordText.UseSystemPasswordChar = true;
        passwordText.Dock = DockStyle.Fill;
        passwordText.Enabled = false;

        historyMatches.Minimum = 0;
        historyMatches.Maximum = 10_000;
        historyMatches.Increment = 500;
        historyMatches.Value = 5_000;
        historyMatches.ThousandsSeparator = true;
        historyMatches.Dock = DockStyle.Left;
        historyMatches.Width = 180;

        outputText.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Dota2MmrReconstructor");
        outputText.Dock = DockStyle.Fill;

        var browseButton = new Button
        {
            Text = "选择文件夹…",
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        browseButton.Click += (_, _) => BrowseOutputDirectory();

        reconstructionCheck.Text = "下载完成后生成拟合结果与可交互 HTML（推荐）";
        reconstructionCheck.Checked = true;
        reconstructionCheck.AutoSize = true;
        reconstructionCheck.Dock = DockStyle.Fill;
        historyMatches.ValueChanged += (_, _) =>
        {
            reconstructionCheck.Enabled = historyMatches.Value > 0;
            if (!reconstructionCheck.Enabled)
            {
                reconstructionCheck.Checked = false;
            }
        };

        var formGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 12, 24, 8),
            ColumnCount = 3,
            RowCount = 9,
        };
        formGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        formGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        formGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        formGrid.Controls.Add(FieldLabel("Steam ID"), 0, 0);
        formGrid.Controls.Add(accountText, 1, 0);
        formGrid.SetColumnSpan(accountText, 2);
        formGrid.Controls.Add(FieldLabel("Steam 登录方式"), 0, 1);
        formGrid.Controls.Add(loginMethods, 1, 1);
        formGrid.SetColumnSpan(loginMethods, 2);
        formGrid.Controls.Add(FieldLabel("Steam 用户名"), 0, 2);
        formGrid.Controls.Add(usernameText, 1, 2);
        formGrid.SetColumnSpan(usernameText, 2);
        formGrid.Controls.Add(FieldLabel("Steam 密码"), 0, 3);
        formGrid.Controls.Add(passwordText, 1, 3);
        formGrid.SetColumnSpan(passwordText, 2);
        var credentialSecurityExplanation = new Label
        {
            Text = "密码不写入文件、日志或命令行；Steam 请求验证时再弹出 OTP 输入框。",
            ForeColor = Color.Firebrick,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        formGrid.Controls.Add(credentialSecurityExplanation, 1, 4);
        formGrid.SetColumnSpan(credentialSecurityExplanation, 2);
        formGrid.Controls.Add(FieldLabel("目标 GC 历史行数"), 0, 5);
        formGrid.Controls.Add(historyMatches, 1, 5);
        formGrid.Controls.Add(new Label
        {
            Text = "0 = 只查当前分数",
            ForeColor = Color.DimGray,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 2, 5);
        formGrid.Controls.Add(FieldLabel("输出根目录"), 0, 6);
        formGrid.Controls.Add(outputText, 1, 6);
        formGrid.Controls.Add(browseButton, 2, 6);
        formGrid.Controls.Add(reconstructionCheck, 1, 7);
        formGrid.SetColumnSpan(reconstructionCheck, 2);
        var outputExplanation = new Label
        {
            Text = "建议使用上面的默认目录，并在以后运行时始终保持同一个固定根目录。\n" +
                   "不要进入或选择某个 SteamID 子文件夹：登录后程序会自动创建/识别 <ID32>\\。\n" +
                   "发现既存 gc-match-history-cache.json 时会自动补最新比赛，并从最老断点继续。\n" +
                   "gc-collection.json 是原始输入；模型结果单独写入 mmr-reconstruction\\。",
            ForeColor = Color.DimGray,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
        };
        formGrid.Controls.Add(outputExplanation, 1, 8);
        formGrid.SetColumnSpan(outputExplanation, 2);

        var cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Padding = new Padding(14, 5, 14, 5),
        };
        var startButton = new Button
        {
            Text = "开始",
            AutoSize = true,
            Padding = new Padding(22, 5, 22, 5),
        };
        startButton.Click += (_, _) => AcceptSelection();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 64,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(18, 10, 18, 10),
        };
        buttons.Controls.Add(startButton);
        buttons.Controls.Add(cancelButton);

        Controls.Add(formGrid);
        Controls.Add(buttons);
        Controls.Add(explanation);
        Controls.Add(heading);
        AcceptButton = startButton;
        CancelButton = cancelButton;
    }

    public static CollectorSetupSelection? AskUser()
    {
        using var window = new CollectorSetupWindow();
        return window.ShowDialog() == DialogResult.OK ? window.Selection : null;
    }

    public static uint? ParseAccountId(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (!ulong.TryParse(text, out var value) || value == 0)
        {
            throw new FormatException("Steam ID 必须是正整数。");
        }

        if (value <= uint.MaxValue)
        {
            return (uint)value;
        }

        if (value >= SteamId64IndividualBase
            && value - SteamId64IndividualBase <= uint.MaxValue)
        {
            return (uint)(value - SteamId64IndividualBase);
        }

        throw new FormatException("请输入 Steam ID32 或个人账号 SteamID64。");
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold),
    };

    private void BrowseOutputDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择数据和曲线的输出根目录",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(outputText.Text) ? outputText.Text : AppContext.BaseDirectory,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            outputText.Text = dialog.SelectedPath;
        }
    }

    private void AcceptSelection()
    {
        try
        {
            var accountId = ParseAccountId(accountText.Text);
            if (string.IsNullOrWhiteSpace(outputText.Text))
            {
                throw new FormatException("请选择输出根目录。");
            }

            var outputRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(outputText.Text.Trim()));
            Directory.CreateDirectory(outputRoot);
            SteamLoginSelection login;
            if (credentialLoginRadio.Checked)
            {
                var username = usernameText.Text.Trim();
                var password = passwordText.Text;
                if (username.Length == 0)
                {
                    throw new FormatException("请输入 Steam 登录用户名。用户名不是个人资料昵称。");
                }
                if (password.Length == 0)
                {
                    throw new FormatException("请输入 Steam 密码。");
                }

                login = new SteamLoginSelection(
                    SteamLoginMode.Credentials,
                    new EphemeralSteamCredentials(username, password));
            }
            else
            {
                login = SteamLoginSelection.QrCode;
            }

            Selection = new CollectorSetupSelection(
                accountId,
                Decimal.ToInt32(historyMatches.Value),
                outputRoot,
                reconstructionCheck.Checked && historyMatches.Value > 0,
                login);
            usernameText.Clear();
            passwordText.Clear();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception) when (
            exception is FormatException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "输入有误",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void UpdateCredentialControls()
    {
        var enabled = credentialLoginRadio.Checked;
        usernameText.Enabled = enabled;
        passwordText.Enabled = enabled;
        if (enabled)
        {
            usernameText.Focus();
        }
    }
}

internal static class CompletionDialog
{
    public static void Show(
        bool succeeded,
        string outputDirectory,
        string? error,
        bool generatedReconstruction)
    {
        if (!succeeded)
        {
            MessageBox.Show(
                error ?? "运行失败。原始数据如果已经下载完成，会继续保留在输出目录。",
                "Dota 2 MMR 曲线生成器",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        var choice = MessageBox.Show(
            (generatedReconstruction
                ? "下载和曲线生成已经完成。\n\n"
                : "原始 GC 数据下载已经完成。\n\n") +
            $"输出目录：{outputDirectory}\n\n" +
            (generatedReconstruction
                ? "点击“是”打开输出目录；可查看 PNG、英雄贡献 TXT/MD/XLSX，或双击交互 HTML。"
                : "点击“是”打开输出目录。"),
            "Dota 2 MMR 曲线生成器",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);
        if (choice == DialogResult.Yes)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", outputDirectory)
            {
                UseShellExecute = true,
            });
        }
    }
}
