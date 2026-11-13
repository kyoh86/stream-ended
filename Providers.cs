using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StreamEnded;

record YouTubeAccount(string ClientId, string ClientSecret, string RefreshToken);
record TwitchAccount(string Login, string ClientId, string ClientSecret);
record Settings(YouTubeAccount? YouTube = null, TwitchAccount? Twitch = null);

record Observation(bool Live, bool Ended = false);

sealed class Providers
{
    private const string GoogleToken = "https://oauth2.googleapis.com/token";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private string? accessToken;
    private DateTimeOffset expiresAt;
    private string? twitchUserId;
    private readonly HashSet<string> broadcasts = new();

    public async Task<Observation> YouTubeAsync(YouTubeAccount account, CancellationToken cancellation)
    {
        var token = await TokenAsync(GoogleToken, new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = account.ClientId,
            ["client_secret"] = account.ClientSecret,
            ["refresh_token"] = account.RefreshToken
        }, cancellation);
        var live = new HashSet<string>();
        string? page = null;
        do
        {
            var query = "broadcastStatus=active&broadcastType=all&maxResults=50";
            if (page is not null)
                query += "&pageToken=" + Uri.EscapeDataString(page);
            var result = await BroadcastsAsync(query, token, cancellation);
            foreach (var item in result.GetProperty("items").EnumerateArray())
            {
                if (item.GetProperty("status").GetProperty("lifeCycleStatus").GetString() == "live")
                    live.Add(item.GetProperty("id").GetString()!);
            }
            page = result.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        } while (page is not null);

        var ended = new HashSet<string>();
        foreach (var batch in broadcasts.Except(live).Chunk(50))
        {
            var ids = string.Join(",", batch.Select(Uri.EscapeDataString));
            var result = await BroadcastsAsync("maxResults=50&id=" + ids, token, cancellation);
            foreach (var item in result.GetProperty("items").EnumerateArray())
            {
                if (item.GetProperty("status").GetProperty("lifeCycleStatus").GetString() == "complete")
                    ended.Add(item.GetProperty("id").GetString()!);
            }
        }
        broadcasts.ExceptWith(ended);
        broadcasts.UnionWith(live);
        if (live.Count == 0 && broadcasts.Count > 0)
            throw new InvalidOperationException("配信枠の終了をまだ確認できません。");

        return new(live.Count > 0, ended.Count > 0 && broadcasts.Count == 0);
    }

    public async Task<Observation> TwitchAsync(TwitchAccount account, CancellationToken cancellation)
    {
        var token = await TokenAsync("https://id.twitch.tv/oauth2/token", new()
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = account.ClientId,
            ["client_secret"] = account.ClientSecret
        }, cancellation);
        if (twitchUserId is null)
        {
            var users = await GetAsync("https://api.twitch.tv/helix/users?login=" +
                Uri.EscapeDataString(account.Login), token, account.ClientId, cancellation);
            var data = users.GetProperty("data");
            if (data.GetArrayLength() == 0)
                throw new InvalidOperationException("Twitchチャンネルが見つかりません。");
            twitchUserId = data[0].GetProperty("id").GetString();
        }
        var streams = await GetAsync("https://api.twitch.tv/helix/streams?user_id=" +
            Uri.EscapeDataString(twitchUserId!), token, account.ClientId, cancellation);
        return new(streams.GetProperty("data").GetArrayLength() > 0);
    }

    private Task<JsonElement> BroadcastsAsync(string query, string token, CancellationToken cancellation) =>
        GetAsync("https://www.googleapis.com/youtube/v3/liveBroadcasts?part=id,status&" + query,
            token, null, cancellation);

    private async Task<string> TokenAsync(string endpoint, Dictionary<string, string> form,
        CancellationToken cancellation)
    {
        if (accessToken is not null && DateTimeOffset.UtcNow < expiresAt)
            return accessToken;
        var json = await PostAsync(endpoint, form, cancellation);
        accessToken = json.GetProperty("access_token").GetString()!;
        expiresAt = DateTimeOffset.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32() - 60);
        return accessToken;
    }

    private async Task<JsonElement> GetAsync(string url, string token, string? clientId,
        CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (clientId is not null)
            request.Headers.Add("Client-Id", clientId);
        using var response = await Http.SendAsync(request, cancellation);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            accessToken = null;
        return await ReadAsync(response, cancellation);
    }

    private static async Task<JsonElement> PostAsync(string url, Dictionary<string, string> form,
        CancellationToken cancellation)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await Http.PostAsync(url, content, cancellation);
        return await ReadAsync(response, cancellation);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
        return json.RootElement.Clone();
    }

    public static async Task<YouTubeAccount> ConnectYouTubeAsync(string file, CancellationToken cancellation)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file, cancellation));
        var installed = document.RootElement.GetProperty("installed");
        var id = installed.GetProperty("client_id").GetString()!;
        var secret = installed.GetProperty("client_secret").GetString()!;
        var verifier = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/callback";
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = id,
            ["redirect_uri"] = redirect,
            ["response_type"] = "code",
            ["scope"] = "https://www.googleapis.com/auth/youtube.readonly",
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        var query = string.Join("&", parameters.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));
        Process.Start(new ProcessStartInfo("https://accounts.google.com/o/oauth2/v2/auth?" + query)
        {
            UseShellExecute = true
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var request = (await reader.ReadLineAsync(timeout.Token))?.Split(' ');
            if (request is not { Length: 3 } || request[0] != "GET" ||
                !request[1].StartsWith("/callback?", StringComparison.Ordinal))
                continue;
            var callback = System.Web.HttpUtility.ParseQueryString(request[1][10..]);
            if (callback["state"] != state)
                continue;
            // Drain the request headers before responding to the browser.
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 })
            {
            }

            try
            {
                var code = callback["code"] ?? throw new InvalidOperationException("YouTubeの連携がキャンセルされました。");
                var token = await PostAsync(GoogleToken, new()
                {
                    ["client_id"] = id,
                    ["client_secret"] = secret,
                    ["code"] = code,
                    ["code_verifier"] = verifier,
                    ["grant_type"] = "authorization_code",
                    ["redirect_uri"] = redirect
                }, timeout.Token);
                var refresh = token.GetProperty("refresh_token").GetString()!;
                await ReplyAsync(stream, "YouTube連携が完了しました。このタブを閉じてください。", timeout.Token);
                return new(id, secret, refresh);
            }
            catch
            {
                await ReplyAsync(stream, "YouTube連携に失敗しました。アプリを確認してください。", timeout.Token);
                throw;
            }
        }
    }

    private static async Task ReplyAsync(Stream stream, string message, CancellationToken cancellation)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellation);
        await stream.WriteAsync(body, cancellation);
    }
}
