using System.Drawing;
using System.Windows.Forms;

namespace Dota2MmrCollector;

internal sealed class SteamQrWindow
{
    private const string WindowTitle = "Steam QR 扫码登录 - Dota 2 MMR Collector";
    private readonly object gate = new();
    private readonly Action onUserClosed;
    private readonly ManualResetEventSlim ready = new(false);

    private Thread? uiThread;
    private Form? form;
    private PictureBox? qrPicture;
    private Label? statusLabel;
    private byte[]? pendingPng;
    private bool closeRequested;

    public SteamQrWindow(Action onUserClosed)
    {
        this.onUserClosed = onUserClosed;
    }

    public void ShowQr(byte[] pngBytes)
    {
        lock (gate)
        {
            pendingPng = (byte[])pngBytes.Clone();
            if (uiThread is null || !uiThread.IsAlive)
            {
                closeRequested = false;
                ready.Reset();
                uiThread = new Thread(RunUi)
                {
                    IsBackground = true,
                    Name = "Dota2MmrCollector QR window",
                };
                uiThread.SetApartmentState(ApartmentState.STA);
                uiThread.Start();
            }
        }

        if (!ready.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException("Steam QR scan window did not initialize.");
        }

        Form? currentForm;
        lock (gate)
        {
            currentForm = form;
        }

        if (currentForm is null || currentForm.IsDisposed)
        {
            throw new InvalidOperationException("Steam QR scan window closed before displaying the code.");
        }

        currentForm.BeginInvoke(new Action(UpdateQrAndActivate));
    }

    public void Close()
    {
        Form? currentForm;
        lock (gate)
        {
            closeRequested = true;
            currentForm = form;
        }

        if (currentForm is null || currentForm.IsDisposed || !currentForm.IsHandleCreated)
        {
            return;
        }

        try
        {
            currentForm.BeginInvoke(new Action(currentForm.Close));
        }
        catch (InvalidOperationException)
        {
            // The window can close between the state check and BeginInvoke.
        }
    }

    private void RunUi()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var createdForm = BuildForm();
        lock (gate)
        {
            form = createdForm;
        }

        UpdateQrAndActivate();
        ready.Set();
        Application.Run(createdForm);

        lock (gate)
        {
            form = null;
            qrPicture = null;
            statusLabel = null;
            uiThread = null;
        }
    }

    private Form BuildForm()
    {
        var offscreenTestMode =
            Environment.GetEnvironmentVariable("DOTA2_MMR_QR_TEST_OFFSCREEN") == "1";
        var createdForm = new Form
        {
            Text = WindowTitle,
            StartPosition = offscreenTestMode
                ? FormStartPosition.Manual
                : FormStartPosition.CenterScreen,
            Location = offscreenTestMode ? new Point(-32_000, -32_000) : Point.Empty,
            ClientSize = new Size(520, 620),
            MinimumSize = new Size(480, 580),
            BackColor = Color.White,
            ShowInTaskbar = !offscreenTestMode,
            TopMost = !offscreenTestMode,
        };

        var heading = new Label
        {
            Text = "使用 Steam 手机应用扫码登录",
            Font = new Font("Microsoft YaHei UI", 16, FontStyle.Bold),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 62,
        };
        statusLabel = new Label
        {
            Text = "二维码准备中……",
            Font = new Font("Microsoft YaHei UI", 10),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 52,
        };
        qrPicture = new PictureBox
        {
            BackColor = Color.White,
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            Padding = new Padding(18),
        };
        var footer = new Label
        {
            Text = "仅用于本次 GC 数据下载；不会把密码或登录令牌写入磁盘。\n" +
                   "扫码批准后，本窗口会自动关闭。",
            Font = new Font("Microsoft YaHei UI", 9),
            ForeColor = Color.DimGray,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Bottom,
            Height = 66,
        };

        createdForm.Controls.Add(qrPicture);
        createdForm.Controls.Add(footer);
        createdForm.Controls.Add(statusLabel);
        createdForm.Controls.Add(heading);
        createdForm.FormClosing += OnFormClosing;
        createdForm.FormClosed += (_, _) => qrPicture?.Image?.Dispose();
        return createdForm;
    }

    private void UpdateQrAndActivate()
    {
        byte[]? pngBytes;
        lock (gate)
        {
            pngBytes = pendingPng;
        }

        if (pngBytes is not null && qrPicture is not null)
        {
            using var stream = new MemoryStream(pngBytes, writable: false);
            using var decoded = Image.FromStream(stream);
            var bitmap = new Bitmap(decoded);
            var previous = qrPicture.Image;
            qrPicture.Image = bitmap;
            previous?.Dispose();
        }

        if (statusLabel is not null)
        {
            statusLabel.Text = "二维码已就绪。请扫描，并在手机上批准登录。";
        }

        if (form is not null)
        {
            form.Show();
            form.WindowState = FormWindowState.Normal;
            form.BringToFront();
            form.Activate();
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        bool wasRequested;
        lock (gate)
        {
            wasRequested = closeRequested;
        }

        if (!wasRequested && eventArgs.CloseReason == CloseReason.UserClosing)
        {
            onUserClosed();
        }
    }
}
