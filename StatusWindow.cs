namespace StreamEnded;

sealed class StatusWindow : Form
{
    public ServicePanel YouTube { get; } = new("YouTube");
    public ServicePanel Twitch { get; } = new("Twitch");
    public Label Intro { get; } = TextLine();
    public Label Deadline { get; } = TextLine();
    public Button Check { get; } = ActionButton("今すぐ確認");
    public Button SetDeadline { get; } = ActionButton("設定…");
    public Button ClearDeadline { get; } = ActionButton("解除");

    public StatusWindow()
    {
        Text = "Stream Ended";
        Icon = SystemIcons.Information;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(600, 510);
        MinimumSize = new Size(520, 440);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(24),
            ColumnCount = 1
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var title = TextLine("Stream Ended");
        title.Font = new Font(Font.FontFamily, 20, FontStyle.Bold);
        layout.Controls.Add(title);
        layout.Controls.Add(Intro);
        layout.Controls.Add(YouTube);
        layout.Controls.Add(Twitch);
        layout.Controls.Add(TextLine("終了予定時刻（任意）"));
        layout.Controls.Add(TextLine("設定した時刻を過ぎても配信中なら通知します。"));
        layout.Controls.Add(Actions(Deadline, SetDeadline, ClearDeadline));
        layout.Controls.Add(Actions(Check));
        layout.Controls.Add(TextLine("ウィンドウを閉じても、通知領域で監視を続けます。"));
        Controls.Add(layout);
    }

    internal static Label TextLine(string text = "") => new()
    {
        AutoSize = true, Text = text, Margin = new Padding(0, 3, 0, 5)
    };

    internal static Button ActionButton(string text) => new() { AutoSize = true, Text = text };

    internal static FlowLayoutPanel Actions(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 4) };
        row.Controls.AddRange(controls);
        return row;
    }
}

sealed class ServicePanel : GroupBox
{
    private readonly Label status = StatusWindow.TextLine();
    private readonly Label detail = StatusWindow.TextLine();
    public Button Connect { get; } = StatusWindow.ActionButton("ログイン…");
    public Button Disconnect { get; } = StatusWindow.ActionButton("連携を解除");
    public Button Cancel { get; } = StatusWindow.ActionButton("キャンセル");

    public ServicePanel(string name)
    {
        Text = name;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Top;
        Margin = new Padding(0, 8, 0, 8);
        Padding = new Padding(12, 6, 12, 8);
        status.Font = new Font(Font, FontStyle.Bold);
        detail.MaximumSize = new Size(435, 0);
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false
        };
        layout.Controls.AddRange([status, detail, StatusWindow.Actions(Connect, Disconnect, Cancel)]);
        Controls.Add(layout);
    }

    public void Render(Watch? watch, bool configured, bool editing, bool signingIn)
    {
        status.Text = signingIn ? "ログイン待ち" : watch?.Status ?? "未連携";
        var description = signingIn ? "ブラウザーでログインと認可を完了してください。"
            : watch?.Detail ?? "ログインすると、この配信先の監視が始まります。";
        detail.Text = configured ? description
            : watch is null ? "このビルドではログインを利用できません。"
            : description + "\n再ログインにはログイン対応版が必要です。";
        Connect.Text = watch is null ? "ログイン…" : "再ログイン…";
        Connect.Enabled = configured && !editing;
        Disconnect.Visible = watch is not null;
        Disconnect.Enabled = !editing;
        Cancel.Visible = signingIn;
    }
}
