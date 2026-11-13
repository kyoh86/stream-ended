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
    private bool observedDuringSchedule;

    public TrayApp()
    {
        // Capture the WinForms synchronization context for async polling and menu callbacks.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        youtube = CreateYouTube(settings.YouTube);
        twitch = CreateTwitch(settings.Twitch);
        menu.Items.AddRange([youtubeStatus, twitchStatus, deadlineStatus, new ToolStripSeparator()]);
        menu.Items.Add("今すぐ確認", null, async (_, _) => await CheckAsync());
        menu.Items.Add("YouTube を連携…", null, async (_, _) => await ConnectYouTubeAsync());
        menu.Items.Add("Twitch を登録…", null, async (_, _) => await ConnectTwitchAsync());
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

    private static Watch? CreateTwitch(TwitchAccount? account) => account is null ? null :
        new("Twitch", (provider, cancellation) => provider.TwitchAsync(account, cancellation));

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
        try
        {
            using var file = new OpenFileDialog
            {
                Title = "Google のデスクトップ OAuth クライアント JSON を選択",
                Filter = "OAuth JSON (*.json)|*.json"
            };
            if (file.ShowDialog() == DialogResult.OK)
            {
                var account = await Providers.ConnectYouTubeAsync(file.FileName, lifetime.Token);
                if (!lifetime.IsCancellationRequested)
                    Update(settings with { YouTube = account });
            }
        }
        catch (Exception failure) when (!lifetime.IsCancellationRequested)
        {
            MessageBox.Show(failure.Message, "YouTubeを連携できませんでした");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            editing = false;
        }
    }

    private async Task ConnectTwitchAsync()
    {
        if (editing)
            return;
        using var form = new Form { Text = "Twitch 登録", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog };
        var panel = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(12) };
        var login = new TextBox { Width = 300, Text = settings.Twitch?.Login ?? "" };
        var id = new TextBox { Width = 300, Text = settings.Twitch?.ClientId ?? "" };
        var secret = new TextBox { Width = 300, Text = settings.Twitch?.ClientSecret ?? "", UseSystemPasswordChar = true };
        foreach (var field in new[] { ("チャンネル名", login), ("Client ID", id), ("Client Secret", secret) })
        {
            panel.Controls.Add(new Label { Text = field.Item1, AutoSize = true });
            panel.Controls.Add(field.Item2);
        }
        var save = new Button { Text = "登録", DialogResult = DialogResult.OK };
        panel.Controls.Add(save);
        form.Controls.Add(panel);
        form.AcceptButton = save;
        if (form.ShowDialog() != DialogResult.OK)
            return;
        var account = new TwitchAccount(login.Text.Trim(), id.Text.Trim(), secret.Text.Trim());
        if (account.Login.Length == 0 || account.ClientId.Length == 0 || account.ClientSecret.Length == 0)
        {
            MessageBox.Show("すべての項目を入力してください。", "Twitch 登録");
            return;
        }
        editing = true;
        try
        {
            await new Providers().TwitchAsync(account, lifetime.Token);
            if (!lifetime.IsCancellationRequested)
                Update(settings with { Twitch = account });
        }
        catch (Exception failure) when (!lifetime.IsCancellationRequested)
        {
            MessageBox.Show(failure.Message, "Twitchを登録できませんでした");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
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
