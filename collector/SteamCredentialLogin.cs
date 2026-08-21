using System.Drawing;
using System.Windows.Forms;
using SteamKit2.Authentication;

namespace Dota2MmrCollector;

internal enum SteamLoginMode
{
    QrCode,
    Credentials,
}

internal sealed class EphemeralSteamCredentials : IDisposable
{
    public EphemeralSteamCredentials(string username, string password)
    {
        Username = username;
        Password = password;
    }

    public string Username { get; private set; }

    public string Password { get; private set; }

    public bool IsCleared => Username.Length == 0 && Password.Length == 0;

    public void Clear()
    {
        Username = string.Empty;
        Password = string.Empty;
    }

    public void Dispose() => Clear();
}

internal sealed record SteamLoginSelection(
    SteamLoginMode Mode,
    EphemeralSteamCredentials? Credentials)
{
    public static SteamLoginSelection QrCode { get; } =
        new(SteamLoginMode.QrCode, null);
}

internal sealed class InteractiveSteamAuthenticator : IAuthenticator
{
    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) =>
        SteamGuardCodeWindow.AskAsync(
            "Steam Guard 手机验证码",
            "请输入手机 Steam 令牌页面当前显示的五位验证码。",
            previousCodeWasIncorrect);

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) =>
        SteamGuardCodeWindow.AskAsync(
            "Steam Guard 邮箱验证码",
            $"请输入 Steam 发送到 {email} 的验证码。",
            previousCodeWasIncorrect);

    public Task<bool> AcceptDeviceConfirmationAsync() => Task.FromResult(false);
}

internal sealed class SteamGuardCodeWindow : Form
{
    private readonly TextBox codeText = new();

    private SteamGuardCodeWindow(
        string title,
        string explanation,
        bool previousCodeWasIncorrect)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 245);
        MinimumSize = new Size(500, 235);
        Font = new Font("Microsoft YaHei UI", 10);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;

        var heading = new Label
        {
            Text = previousCodeWasIncorrect ? "上一个验证码无效，请输入新验证码" : title,
            Dock = DockStyle.Top,
            Height = 54,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, 14, FontStyle.Bold),
            ForeColor = previousCodeWasIncorrect ? Color.Firebrick : SystemColors.ControlText,
        };

        var explanationLabel = new Label
        {
            Text = explanation + "\n验证码只用于本次登录，不写入文件或日志。",
            Dock = DockStyle.Top,
            Height = 66,
            Padding = new Padding(22, 4, 22, 4),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
        };

        codeText.CharacterCasing = CharacterCasing.Upper;
        codeText.MaxLength = 8;
        codeText.Font = new Font("Consolas", 22, FontStyle.Bold);
        codeText.TextAlign = HorizontalAlignment.Center;
        codeText.Dock = DockStyle.Fill;
        codeText.PlaceholderText = "ABCDE";

        var codePanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(90, 5, 90, 5),
        };
        codePanel.Controls.Add(codeText);

        var cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Padding = new Padding(14, 4, 14, 4),
        };
        var submitButton = new Button
        {
            Text = "提交验证码",
            AutoSize = true,
            Padding = new Padding(16, 4, 16, 4),
        };
        submitButton.Click += (_, _) => SubmitCode();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(16, 8, 16, 8),
        };
        buttons.Controls.Add(submitButton);
        buttons.Controls.Add(cancelButton);

        Controls.Add(buttons);
        Controls.Add(codePanel);
        Controls.Add(explanationLabel);
        Controls.Add(heading);
        AcceptButton = submitButton;
        CancelButton = cancelButton;
        Shown += (_, _) => codeText.Focus();
    }

    public string TakeSubmittedCode()
    {
        var code = codeText.Text.Trim();
        codeText.Clear();
        return code;
    }

    public static Task<string> AskAsync(
        string title,
        string explanation,
        bool previousCodeWasIncorrect)
    {
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var window = new SteamGuardCodeWindow(
                    title,
                    explanation,
                    previousCodeWasIncorrect);
                if (window.ShowDialog() == DialogResult.OK)
                {
                    completion.TrySetResult(window.TakeSubmittedCode());
                }
                else
                {
                    completion.TrySetException(
                        new OperationCanceledException("验证码输入已取消。"));
                }
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "SteamGuardCodeWindow",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private void SubmitCode()
    {
        var code = codeText.Text.Trim();
        if (code.Length != 5 || code.Any(character => !char.IsLetterOrDigit(character)))
        {
            MessageBox.Show(
                this,
                "请输入五位字母或数字组成的 Steam Guard 验证码。",
                "验证码格式不正确",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            codeText.SelectAll();
            codeText.Focus();
            return;
        }

        codeText.Text = code.ToUpperInvariant();
        DialogResult = DialogResult.OK;
        Close();
    }
}
