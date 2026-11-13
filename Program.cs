using System.Security.Cryptography;
using System.Text.Json;

namespace StreamEnded;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var singleInstance = new Mutex(true, @"Local\StreamEnded", out var first);
        if (!first)
            return;

        ApplicationConfiguration.Initialize();
        try
        {
            using var app = new TrayApp();
            Application.Run(app);
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

    public string Describe()
    {
        if (error is not null)
            return $"{Name}：確認できません（{error}）";
        if (endedAt is not null)
            return $"{Name}：終了確認済み {endedAt:HH:mm:ss}";
        if (CheckedAt is null)
            return $"{Name}：確認中";
        return $"{Name}：{(Live == true ? "配信中" : "オフライン")} {CheckedAt:HH:mm:ss} 確認";
    }
}

sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon tray = new() { Icon = SystemIcons.Information, Text = "Stream Ended" };
    private readonly ContextMenuStrip menu = new();
    private readonly ToolStripMenuItem youtubeStatus = new() { Enabled = false };
    private readonly ToolStripMenuItem twitchStatus = new() { Enabled = false };
    private readonly ToolStripMenuItem deadlineStatus = new() { Enabled = false };
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
    private readonly ToolStripMenuItem cancelTwitchLogin = new("Twitch ログインをキャンセル") { Visible = false };
    private CancellationTokenSource? youtubeLogin;
    private readonly ToolStripMenuItem cancelYouTubeLogin = new("YouTube ログインをキャンセル")
    {
        Visible = false
    };
    private bool observedDuringSchedule;

    public TrayApp()
    {
        // Capture the WinForms synchronization context for async polling and menu callbacks.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        youtube = CreateYouTube(settings.YouTube);
        twitch = CreateTwitch(settings.Twitch);
        menu.Items.AddRange([youtubeStatus, twitchStatus, deadlineStatus, new ToolStripSeparator()]);
        menu.Items.Add("今すぐ確認", null, async (_, _) => await CheckAsync());
        var connectYouTube = menu.Items.Add("YouTube でログイン…", null, async (_, _) => await ConnectYouTubeAsync());
        if (!Providers.HasYouTubeClient)
        {
            connectYouTube.Text = "YouTube ログイン未設定のビルド";
            connectYouTube.Enabled = false;
        }
        cancelYouTubeLogin.Click += (_, _) => youtubeLogin?.Cancel();
        menu.Items.Add(cancelYouTubeLogin);
        var connectTwitch = menu.Items.Add("Twitch でログイン…", null, async (_, _) => await ConnectTwitchAsync());
        if (Providers.TwitchClientId is null)
        {
            connectTwitch.Text = "Twitch ログイン未設定のビルド";
            connectTwitch.Enabled = false;
        }
        cancelTwitchLogin.Click += (_, _) => twitchLogin?.Cancel();
        menu.Items.Add(cancelTwitchLogin);
        menu.Items.Add("YouTube の連携を解除", null, (_, _) => Update(settings with { YouTube = null }));
        menu.Items.Add("Twitch の登録を解除", null, (_, _) => Update(settings with { Twitch = null }));
        menu.Items.Add("今回の終了予定時刻…", null, async (_, _) => await SetDeadlineAsync());
        menu.Items.Add("終了予定時刻を解除", null, (_, _) => ClearDeadline());
        menu.Items.Add("終了", null, (_, _) => ExitThread());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => MessageBox.Show(
            $"{youtubeStatus.Text}\n{twitchStatus.Text}\n{deadlineStatus.Text}", "Stream Ended");
        tray.Visible = true;
        RefreshMenu();
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
            RefreshMenu();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            checking = false;
        }
    }

    private void Notify(string title, string text) =>
        tray.ShowBalloonTip(8000, title, text, ToolTipIcon.Info);

    private void RefreshMenu()
    {
        youtubeStatus.Text = youtube?.Describe() ?? "YouTube：未登録";
        twitchStatus.Text = twitch?.Describe() ?? "Twitch：未登録";
        deadlineStatus.Text = deadline is { } scheduled
            ? $"終了予定 {scheduled:MM/dd HH:mm}" : "終了予定なし";
    }

    private void ClearDeadline()
    {
        deadline = null;
        overdue.Clear();
        observedDuringSchedule = false;
        RefreshMenu();
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
            RefreshMenu();
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
        cancelYouTubeLogin.Visible = true;
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
            cancelYouTubeLogin.Visible = false;
            editing = false;
        }
    }

    private async Task ConnectTwitchAsync()
    {
        if (editing)
            return;
        editing = true;
        using var login = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        twitchLogin = login;
        cancelTwitchLogin.Visible = true;
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
            cancelTwitchLogin.Visible = false;
            editing = false;
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
        if (form.ShowDialog() != DialogResult.OK)
            return;
        var time = picker.Value;
        deadline = new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0, DateTimeKind.Local);
        overdue.Clear();
        observedDuringSchedule = false;
        RefreshMenu();
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
            lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
