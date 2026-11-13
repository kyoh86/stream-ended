using System.Security.Cryptography;
using System.Text.Json;

namespace StreamEnded;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        using var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\StreamEnded.Show");
        using var singleInstance = new Mutex(true, @"Local\StreamEnded", out var first);
        if (!first)
        {
            if (!background)
                showRequest.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        try
        {
            using var app = new TrayApp(background);
            var context = SynchronizationContext.Current!;
            var registration = ThreadPool.RegisterWaitForSingleObject(showRequest,
                (_, _) => context.Post(_ => app.ShowWindow(), null), null, Timeout.Infinite, false);
            try
            {
                Application.Run(app);
            }
            finally
            {
                registration.Unregister(null);
            }
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Stream Ended", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

static class SettingsFile
{
    private static readonly string Pathname = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StreamEnded", "settings.dat");

    public static Settings Load() => File.Exists(Pathname)
        ? JsonSerializer.Deserialize<Settings>(ProtectedData.Unprotect(
            File.ReadAllBytes(Pathname), null, DataProtectionScope.CurrentUser)) ?? new()
        : new();

    public static void Save(Settings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Pathname)!);
        var encrypted = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(settings),
            null, DataProtectionScope.CurrentUser);
        var temporary = Pathname + ".tmp";
        File.WriteAllBytes(temporary, encrypted);
        File.Move(temporary, Pathname, overwrite: true);
    }
}

sealed class Watch(string name, Func<Providers, CancellationToken, Task<Observation>> observe)
{
    private readonly Providers providers = new();
    private bool? lastLive;
    private DateTimeOffset? endedAt;
    private string? error;

    public string Name => name;
    public bool? Live => error is null ? lastLive : null;
    public DateTimeOffset? CheckedAt { get; private set; }

    public async Task<bool> CheckAsync(CancellationToken cancellation)
    {
        try
        {
            var current = await observe(providers, cancellation);
            var ended = current.Ended || (Name == "Twitch" && lastLive == true && !current.Live);
            CheckedAt = DateTimeOffset.Now;
            if (ended)
                endedAt = CheckedAt;
            if (current.Live)
                endedAt = null;
            lastLive = current.Live;
            error = null;
            return ended;
        }
        catch (Exception failure) when (!cancellation.IsCancellationRequested)
        {
            error = failure.Message;
            return false;
        }
    }

    public string Status => error is not null ? "確認できません"
        : endedAt is not null ? "終了確認済み"
        : CheckedAt is null ? "確認中"
        : Live == true ? "配信中" : "オフライン";

    public string Detail => error
        ?? (endedAt is { } ended ? $"終了を確認 {ended:g}"
        : CheckedAt is { } checkedAt ? $"最終確認 {checkedAt:g}"
        : "配信先に問い合わせています。");

}

sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon tray = new() { Icon = SystemIcons.Information, Text = "Stream Ended" };
    private readonly ContextMenuStrip menu = new();
    private readonly StatusWindow window = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 30_000 };
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<Watch> overdue = new();
    private Settings settings = SettingsFile.Load();
    private Watch? youtube;
    private Watch? twitch;
    private DateTimeOffset? deadline;
    private bool checking;
    private bool editing;
    private CancellationTokenSource? twitchLogin;
    private CancellationTokenSource? youtubeLogin;
    private bool observedDuringSchedule;

    public TrayApp(bool background)
    {
        // Capture the WinForms synchronization context for async polling and menu callbacks.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        youtube = CreateYouTube(settings.YouTube);
        twitch = CreateTwitch(settings.Twitch);
        window.YouTube.Connect.Click += async (_, _) => await ConnectYouTubeAsync();
        window.Twitch.Connect.Click += async (_, _) => await ConnectTwitchAsync();
        window.YouTube.Cancel.Click += (_, _) => youtubeLogin?.Cancel();
        window.Twitch.Cancel.Click += (_, _) => twitchLogin?.Cancel();
        window.YouTube.Disconnect.Click += (_, _) => Update(settings with { YouTube = null });
        window.Twitch.Disconnect.Click += (_, _) => Update(settings with { Twitch = null });
        window.Check.Click += async (_, _) => await CheckAsync();
        window.SetDeadline.Click += async (_, _) => await SetDeadlineAsync();
        window.ClearDeadline.Click += (_, _) => ClearDeadline();
        window.FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing && !lifetime.IsCancellationRequested)
            {
                e.Cancel = true;
                window.Hide();
            }
        };
        menu.Items.Add("画面を開く", null, (_, _) => ShowWindow());
        menu.Items.Add("アプリを終了", null, (_, _) => ExitThread());
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowWindow();
        };
        tray.BalloonTipClicked += (_, _) => ShowWindow();
        tray.Visible = true;
        RefreshWindow();
        if (!background || (youtube is null && twitch is null))
            ShowWindow();
        timer.Tick += async (_, _) => await CheckAsync();
        timer.Start();
        _ = CheckAsync();
    }

    private static Watch? CreateYouTube(YouTubeAccount? account) => account is null ? null :
        new("YouTube", (provider, cancellation) => provider.YouTubeAsync(account, cancellation));

    private Watch? CreateTwitch(TwitchAccount? account)
    {
        if (account is null)
            return null;

        Watch? watch = null;
        watch = new("Twitch", (provider, cancellation) =>
            provider.TwitchAsync(account, cancellation, refresh =>
            {
                if (twitch != watch)
                    return;
                var updated = settings with { Twitch = account with { RefreshToken = refresh } };
                SettingsFile.Save(updated);
                settings = updated;
            }));
        return watch;
    }

    private async Task CheckAsync()
    {
        if (checking || lifetime.IsCancellationRequested)
            return;
        checking = true;
        RefreshWindow();
        try
        {
            var watches = new[] { youtube, twitch }.OfType<Watch>().ToArray();
            var results = await Task.WhenAll(watches.Select(async watch =>
                (Watch: watch, Ended: await watch.CheckAsync(lifetime.Token))));
            if (lifetime.IsCancellationRequested)
                return;
            var current = results.Where(result => result.Watch == youtube || result.Watch == twitch).ToArray();
            var ended = current.Where(result => result.Ended).Select(result => result.Watch.Name).ToArray();
            var late = Array.Empty<string>();
            if (deadline is { } scheduled)
            {
                observedDuringSchedule |= ended.Length > 0 || current.Any(result => result.Watch.Live == true);
                if (DateTimeOffset.Now >= scheduled)
                    late = current.Where(result => result.Watch.Live == true && overdue.Add(result.Watch))
                        .Select(result => result.Watch.Name).ToArray();
                if (observedDuringSchedule && current.All(result => result.Watch.Live == false))
                    ClearDeadline();
            }

            if (ended.Length > 0)
            {
                var text = string.Join("・", ended);
                if (late.Length > 0)
                    text += "\n終了予定を超過して配信中：" + string.Join("・", late);
                Notify("配信終了を確認しました", text);
            }
            else if (late.Length > 0)
            {
                Notify("終了予定を過ぎても配信中です", string.Join("・", late));
            }
            RefreshWindow();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            checking = false;
            if (!lifetime.IsCancellationRequested)
                RefreshWindow();
        }
    }

    private void Notify(string title, string text) =>
        tray.ShowBalloonTip(8000, title, text, ToolTipIcon.Info);

    public void ShowWindow()
    {
        if (lifetime.IsCancellationRequested)
            return;
        window.Show();
        if (window.WindowState == FormWindowState.Minimized)
            window.WindowState = FormWindowState.Normal;
        window.Activate();
    }

    private void RefreshWindow()
    {
        window.YouTube.Render(youtube, Providers.HasYouTubeClient, editing, youtubeLogin is not null);
        window.Twitch.Render(twitch, Providers.TwitchClientId is not null, editing, twitchLogin is not null);
        window.Check.Enabled = !checking && (youtube is not null || twitch is not null);
        window.Check.Text = checking ? "確認中…" : "今すぐ確認";
        window.Intro.Text = youtube is not null || twitch is not null
            ? "登録した配信先を約30秒ごとに確認し、配信が終わったら通知します。"
            : Providers.HasYouTubeClient || Providers.TwitchClientId is not null
                ? "配信に使うサービスでログインすると、配信終了を Windows に通知します。"
                : "監視を始めるには、ログインに対応したビルドを使用してください。";
        window.Deadline.Text = deadline is { } scheduled ? scheduled.ToString("g") : "未設定";
        window.ClearDeadline.Enabled = deadline is not null;
    }

    private void ClearDeadline()
    {
        deadline = null;
        overdue.Clear();
        observedDuringSchedule = false;
        RefreshWindow();
    }

    private void Update(Settings updated)
    {
        try
        {
            SettingsFile.Save(updated);
            if (settings.YouTube != updated.YouTube)
                youtube = CreateYouTube(updated.YouTube);
            if (settings.Twitch != updated.Twitch)
                twitch = CreateTwitch(updated.Twitch);
            settings = updated;
            RefreshWindow();
            _ = CheckAsync();
        }
        catch (Exception failure)
        {
            MessageBox.Show(failure.Message, "設定を保存できませんでした");
        }
    }

    private async Task ConnectYouTubeAsync()
    {
        if (editing)
            return;
        editing = true;
        using var login = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        youtubeLogin = login;
        RefreshWindow();
        try
        {
            var account = await Providers.ConnectYouTubeAsync(login.Token);
            if (!login.IsCancellationRequested)
                Update(settings with { YouTube = account });
        }
        catch (OperationCanceledException) when (login.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show("ログインの待機時間が過ぎました。もう一度お試しください。",
                "YouTubeを連携できませんでした");
        }
        catch (Exception failure) when (!login.IsCancellationRequested)
        {
            MessageBox.Show(failure.Message, "YouTubeを連携できませんでした");
        }
        finally
        {
            youtubeLogin = null;
            editing = false;
            if (!lifetime.IsCancellationRequested)
                RefreshWindow();
        }
    }

    private async Task ConnectTwitchAsync()
    {
        if (editing)
            return;
        editing = true;
        using var login = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        twitchLogin = login;
        RefreshWindow();
        try
        {
            var account = await Providers.ConnectTwitchAsync(login.Token);
            if (!login.IsCancellationRequested)
                Update(settings with { Twitch = account });
        }
        catch (OperationCanceledException) when (login.IsCancellationRequested)
        {
        }
        catch (Exception failure)
        {
            if (!login.IsCancellationRequested)
                MessageBox.Show(failure.Message, "Twitchを連携できませんでした");
        }
        finally
        {
            twitchLogin = null;
            editing = false;
            if (!lifetime.IsCancellationRequested)
                RefreshWindow();
        }
    }

    private async Task SetDeadlineAsync()
    {
        using var form = new Form { Text = "今回の終了予定", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog };
        var panel = new FlowLayoutPanel { AutoSize = true, Padding = new Padding(12) };
        var picker = new DateTimePicker { Format = DateTimePickerFormat.Custom,
            CustomFormat = "yyyy/MM/dd HH:mm", ShowUpDown = true, Width = 170,
            Value = deadline?.LocalDateTime ?? DateTime.Now.AddHours(1) };
        var save = new Button { Text = "設定", DialogResult = DialogResult.OK };
        panel.Controls.AddRange([picker, save]);
        form.Controls.Add(panel);
        form.AcceptButton = save;
        if (form.ShowDialog(window) != DialogResult.OK)
            return;
        var time = picker.Value;
        deadline = new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0, DateTimeKind.Local);
        overdue.Clear();
        observedDuringSchedule = false;
        RefreshWindow();
        await CheckAsync();
    }

    protected override void ExitThreadCore()
    {
        timer.Stop();
        lifetime.Cancel();
        tray.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            tray.Dispose();
            menu.Dispose();
            window.Dispose();
            lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
