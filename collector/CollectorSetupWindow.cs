using System.Diagnostics;
using System.Drawing;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace Dota2MmrCollector;

internal sealed record CollectorSetupSelection(
    CollectorTaskKind Task,
    uint? ExpectedAccountId,
    int HistoryMatches,
    string OutputRoot,
    bool GenerateReconstruction,
    PracticeReportSettings? Practice,
    SteamLoginSelection Login,
    bool GenerateTeammates,
    bool IncludeNormalMatches);

internal sealed class CollectorSetupWindow : Form
{
    private const ulong SteamId64IndividualBase = 76_561_197_960_265_728UL;

    private readonly TextBox accountText = new();
    private readonly ComboBox taskCombo = new();
    private readonly RadioButton qrLoginRadio = new();
    private readonly RadioButton credentialLoginRadio = new();
    private readonly TextBox usernameText = new();
    private readonly TextBox passwordText = new();
    private readonly NumericUpDown historyMatches = new();
    private readonly DateTimePicker practiceFrom = new();
    private readonly DateTimePicker practiceTo = new();
    private readonly TextBox openDotaApiKeyText = new();
    private readonly CheckBox saveOpenDotaApiKeyCheck = new();
    private readonly TextBox outputText = new();
    private readonly CheckBox reconstructionCheck = new();
    private readonly CheckBox teammatesCheck = new();
    private readonly CheckBox normalMatchesCheck = new();

    public CollectorSetupSelection? Selection { get; private set; }

    private CollectorSetupWindow()
    {
        Text = "Dota 2 GC 数据采集器";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(820, 900);
        MinimumSize = new Size(760, 820);
        Font = new Font("Microsoft YaHei UI", 10);
        FormBorderStyle = FormBorderStyle.Sizable;

        var heading = new Label
        {
            Text = "Dota 2 GC 数据采集与报表",
            Dock = DockStyle.Top,
            Height = 58,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, 16, FontStyle.Bold),
        };

        var explanation = new Label
        {
            Text = "Steam ID 可以留空，登录后会自动识别。选择任务后只运行对应采集流程。\n" +
                   "练习比赛任务按日期扫描 GC，再用 OpenDota 补充已知队友的同时间段比赛；运行前请完全退出 Dota 2。",
            Dock = DockStyle.Top,
            Height = 78,
            Padding = new Padding(24, 4, 24, 4),
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        accountText.PlaceholderText = "可选，例如 123456789 或 76561198083722517";
        accountText.Dock = DockStyle.Fill;

        taskCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        taskCombo.Items.AddRange([
            "MMR 曲线（天梯历史）",
            "练习比赛统计（GC + OpenDota）",
        ]);
        taskCombo.SelectedIndex = 0;
        taskCombo.Dock = DockStyle.Fill;
        taskCombo.SelectedIndexChanged += (_, _) => UpdateTaskControls();

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

        var today = DateTime.Today;
        practiceFrom.Format = DateTimePickerFormat.Custom;
        practiceFrom.CustomFormat = "yyyy-MM-dd";
        practiceFrom.Value = new DateTime(today.Year, today.Month, 1);
        practiceFrom.Width = 145;
        practiceTo.Format = DateTimePickerFormat.Custom;
        practiceTo.CustomFormat = "yyyy-MM-dd";
        practiceTo.Value = today;
        practiceTo.Width = 145;
        var practicePeriodPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        practicePeriodPanel.Controls.Add(practiceFrom);
        practicePeriodPanel.Controls.Add(new Label
        {
            Text = "至（首尾日期均包含）",
            AutoSize = true,
            Padding = new Padding(8, 8, 8, 0),
        });
        practicePeriodPanel.Controls.Add(practiceTo);

        openDotaApiKeyText.PlaceholderText =
            "可留空：自动读取本机加密存储、OPENDOTA_API_KEY 或 EXE 同目录 .env";
        openDotaApiKeyText.UseSystemPasswordChar = true;
        openDotaApiKeyText.Dock = DockStyle.Fill;
        openDotaApiKeyText.Text = PracticeOpenDotaApiKeyStore.TryLoad() ?? string.Empty;
        saveOpenDotaApiKeyCheck.Text = "保存到本机（Windows 当前用户加密）";
        saveOpenDotaApiKeyCheck.Checked = true;
        saveOpenDotaApiKeyCheck.AutoSize = true;
        saveOpenDotaApiKeyCheck.Dock = DockStyle.Fill;
        var openDotaApiKeyPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        openDotaApiKeyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        openDotaApiKeyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        openDotaApiKeyPanel.Controls.Add(openDotaApiKeyText, 0, 0);
        openDotaApiKeyPanel.Controls.Add(saveOpenDotaApiKeyCheck, 1, 0);

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
        teammatesCheck.Text = "生成队友报告（首次需补下载比赛详情）";
        teammatesCheck.Checked = true;
        teammatesCheck.AutoSize = true;
        teammatesCheck.Dock = DockStyle.Fill;
        normalMatchesCheck.Text = "队友报告包含普通匹配（MMR曲线仍仅含天梯）";
        normalMatchesCheck.AutoSize = true;
        normalMatchesCheck.Dock = DockStyle.Fill;
        reconstructionCheck.CheckedChanged += (_, _) => UpdateTaskControls();
        teammatesCheck.CheckedChanged += (_, _) => UpdateTaskControls();
        historyMatches.ValueChanged += (_, _) => UpdateTaskControls();

        var formGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 12, 24, 8),
            AutoScroll = true,
            ColumnCount = 3,
            RowCount = 14,
        };
        formGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        formGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        formGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        formGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));

        formGrid.Controls.Add(FieldLabel("Steam ID"), 0, 0);
        formGrid.Controls.Add(accountText, 1, 0);
        formGrid.SetColumnSpan(accountText, 2);
        formGrid.Controls.Add(FieldLabel("采集任务"), 0, 1);
        formGrid.Controls.Add(taskCombo, 1, 1);
        formGrid.SetColumnSpan(taskCombo, 2);
        formGrid.Controls.Add(FieldLabel("Steam 登录方式"), 0, 2);
        formGrid.Controls.Add(loginMethods, 1, 2);
        formGrid.SetColumnSpan(loginMethods, 2);
        formGrid.Controls.Add(FieldLabel("Steam 用户名"), 0, 3);
        formGrid.Controls.Add(usernameText, 1, 3);
        formGrid.SetColumnSpan(usernameText, 2);
        formGrid.Controls.Add(FieldLabel("Steam 密码"), 0, 4);
        formGrid.Controls.Add(passwordText, 1, 4);
        formGrid.SetColumnSpan(passwordText, 2);
        var credentialSecurityExplanation = new Label
        {
            Text = "密码不写入文件、日志或命令行；Steam 请求验证时再弹出 OTP 输入框。",
            ForeColor = Color.Firebrick,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        formGrid.Controls.Add(credentialSecurityExplanation, 1, 5);
        formGrid.SetColumnSpan(credentialSecurityExplanation, 2);
        formGrid.Controls.Add(FieldLabel("目标 GC 历史行数"), 0, 6);
        formGrid.Controls.Add(historyMatches, 1, 6);
        formGrid.Controls.Add(new Label
        {
            Text = "0 = 只查当前分数",
            ForeColor = Color.DimGray,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 2, 6);
        formGrid.Controls.Add(FieldLabel("练习比赛日期"), 0, 7);
        formGrid.Controls.Add(practicePeriodPanel, 1, 7);
        formGrid.SetColumnSpan(practicePeriodPanel, 2);
        formGrid.Controls.Add(FieldLabel("OpenDota API key"), 0, 8);
        formGrid.Controls.Add(openDotaApiKeyPanel, 1, 8);
        formGrid.SetColumnSpan(openDotaApiKeyPanel, 2);
        formGrid.Controls.Add(FieldLabel("输出根目录"), 0, 9);
        formGrid.Controls.Add(outputText, 1, 9);
        formGrid.Controls.Add(browseButton, 2, 9);
        formGrid.Controls.Add(reconstructionCheck, 1, 10);
        formGrid.SetColumnSpan(reconstructionCheck, 2);
        formGrid.Controls.Add(teammatesCheck, 1, 11);
        formGrid.SetColumnSpan(teammatesCheck, 2);
        formGrid.Controls.Add(normalMatchesCheck, 1, 12);
        formGrid.SetColumnSpan(normalMatchesCheck, 2);
        var outputExplanation = new Label
        {
            Text = "建议使用上面的默认目录，并在以后运行时始终保持同一个固定根目录。\n" +
                   "不要进入或选择某个 SteamID 子文件夹：登录后程序会自动创建/识别 <ID32>\\。\n" +
                   "MMR 任务复用 gc-match-history-cache.json；练习比赛任务使用独立缓存。\n" +
                   "练习比赛最终生成 practice-match-report.xlsx 和可审计的 JSON。",
            ForeColor = Color.DimGray,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
        };
        formGrid.Controls.Add(outputExplanation, 1, 13);
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
        UpdateTaskControls();
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
            Description = "选择数据和报表的输出根目录",
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

            var task = taskCombo.SelectedIndex == 1
                ? CollectorTaskKind.PracticeMatchReport
                : CollectorTaskKind.MmrReconstruction;
            PracticeReportSettings? practice = null;
            if (task == CollectorTaskKind.PracticeMatchReport)
            {
                var fromDate = DateOnly.FromDateTime(practiceFrom.Value);
                var toDate = DateOnly.FromDateTime(practiceTo.Value);
                if (fromDate > toDate)
                {
                    throw new FormatException("练习比赛开始日期不能晚于结束日期。");
                }

                var apiKey = openDotaApiKeyText.Text.Trim();
                if (saveOpenDotaApiKeyCheck.Checked)
                {
                    PracticeOpenDotaApiKeyStore.Save(apiKey);
                }
                else
                {
                    PracticeOpenDotaApiKeyStore.Delete();
                }
                practice = new PracticeReportSettings(
                    fromDate,
                    toDate,
                    TimeZoneInfo.Local.Id,
                    apiKey.Length == 0 ? null : new EphemeralSecret(apiKey));
            }

            Selection = new CollectorSetupSelection(
                task,
                accountId,
                Decimal.ToInt32(historyMatches.Value),
                outputRoot,
                task == CollectorTaskKind.MmrReconstruction
                    && reconstructionCheck.Checked
                    && historyMatches.Value > 0,
                practice,
                login,
                task == CollectorTaskKind.MmrReconstruction
                    && teammatesCheck.Checked
                    && reconstructionCheck.Checked
                    && historyMatches.Value > 0,
                task == CollectorTaskKind.MmrReconstruction
                    && normalMatchesCheck.Checked
                    && teammatesCheck.Checked
                    && reconstructionCheck.Checked
                    && historyMatches.Value > 0);
            usernameText.Clear();
            passwordText.Clear();
            openDotaApiKeyText.Clear();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception) when (
            exception is FormatException
                or IOException
                or UnauthorizedAccessException
                or CryptographicException)
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

    private void UpdateTaskControls()
    {
        var isPractice = taskCombo.SelectedIndex == 1;
        historyMatches.Enabled = !isPractice;
        practiceFrom.Enabled = isPractice;
        practiceTo.Enabled = isPractice;
        openDotaApiKeyText.Enabled = isPractice;
        saveOpenDotaApiKeyCheck.Enabled = isPractice;
        reconstructionCheck.Enabled = !isPractice && historyMatches.Value > 0;
        if (isPractice)
        {
            reconstructionCheck.Checked = false;
        }
        teammatesCheck.Enabled = !isPractice
            && reconstructionCheck.Checked
            && historyMatches.Value > 0;
        normalMatchesCheck.Enabled = teammatesCheck.Enabled && teammatesCheck.Checked;
    }
}

internal static class CompletionDialog
{
    public static void Show(
        bool succeeded,
        string outputDirectory,
        string? error,
        bool generatedReconstruction,
        CollectorTaskKind task,
        string? teammateStatus = null,
        IReadOnlyList<string>? reconstructionNotices = null,
        string? teammateReportPath = null)
    {
        if (!succeeded)
        {
            MessageBox.Show(
                error ?? "运行失败。原始数据如果已经下载完成，会继续保留在输出目录。",
                "Dota 2 数据采集器",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        var isPractice = task == CollectorTaskKind.PracticeMatchReport;
        var page = new TaskDialogPage
        {
            Caption = "Dota 2 数据采集器",
            Heading = isPractice
                ? "练习比赛报表已生成"
                : generatedReconstruction
                    ? "报告已生成，可以直接打开查看"
                    : "原始 GC 数据下载完成",
            Text = $"输出目录：{outputDirectory}\n\n" +
                (reconstructionNotices is { Count: > 0 } ? string.Join("\n", reconstructionNotices) + "\n\n" : "") +
                (teammateStatus is null ? "" : teammateStatus + "\n\n") +
                (isPractice
                    ? "下次也可以进入上面的文件夹，打开 practice-match-report.xlsx。"
                    : "下次也可以进入上面的文件夹，双击 HTML 文件查看报告。"),
            AllowCancel = true,
            SizeToContent = true,
        };
        var practiceWorkbook = Path.Combine(outputDirectory, "practice-match-report.xlsx");
        if (isPractice && File.Exists(practiceWorkbook))
            AddOpenButton("打开练习比赛报表", "查看玩家胜负与共同场数/胜率矩阵", practiceWorkbook);
        if (!isPractice && teammateReportPath is not null && File.Exists(teammateReportPath))
            AddOpenButton("打开队友报告", "从天梯分数图选择范围，查看综合表现、胜率及上分下分榜单", teammateReportPath);
        var mmrPath = Path.Combine(outputDirectory, "mmr-history.html");
        if (!isPractice && generatedReconstruction && File.Exists(mmrPath))
            AddOpenButton("打开 MMR 曲线", "查看天梯分数变化与逐场记录", mmrPath);
        AddOpenButton("打开输出文件夹", "查看或分享 HTML、PNG、Excel 等结果文件", outputDirectory);
        page.Buttons.Add(TaskDialogButton.Close);
        TaskDialog.ShowDialog(page);

        void AddOpenButton(string text, string description, string path)
        {
            var button = new TaskDialogCommandLinkButton(text, description) { AllowCloseDialog = false };
            button.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
                { MessageBox.Show($"无法自动打开，请在文件夹中手动查看：\n{path}", "打开报告", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            };
            page.Buttons.Add(button);
        }
    }
}
