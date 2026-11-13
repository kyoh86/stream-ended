using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace StreamEnded;

sealed partial class Providers
{
    private const string TwitchToken = "https://id.twitch.tv/oauth2/token";
    private string? twitchRefreshToken;
    private string? unsavedRefreshToken;

    public static string? TwitchClientId => typeof(Providers).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "TwitchClientId")?.Value;

    public static async Task<TwitchAccount> ConnectTwitchAsync(CancellationToken cancellation)
    {
        var id = TwitchClientId ?? throw new InvalidOperationException("Twitch Client ID が未設定です。");
        var device = await PostAsync("https://id.twitch.tv/oauth2/device", new()
        {
            ["client_id"] = id,
            ["scope"] = ""
        }, cancellation);

        var uri = new Uri(device.GetProperty("verification_uri").GetString()!);
        if (uri.Scheme != "https" || uri.Host != "www.twitch.tv")
            throw new InvalidOperationException("Twitch の認証 URL を確認できません。");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });

        var code = device.GetProperty("device_code").GetString()!;
        var interval = Math.Max(5, device.GetProperty("interval").GetInt32());
        var expires = DateTimeOffset.UtcNow.AddSeconds(device.GetProperty("expires_in").GetInt32());
        while (DateTimeOffset.UtcNow < expires)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellation);
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = id,
                ["device_code"] = code,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            });
            using var response = await Http.PostAsync(TwitchToken, form, cancellation);
            if (response.IsSuccessStatusCode)
            {
                var token = await ReadAsync(response, cancellation);
                var users = await new Providers().GetAsync("https://api.twitch.tv/helix/users",
                    token.GetProperty("access_token").GetString()!, id, cancellation);
                var login = users.GetProperty("data")[0].GetProperty("login").GetString()!;
                return new(login, id, null, token.GetProperty("refresh_token").GetString());
            }

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
                var message = body.RootElement.GetProperty("message").GetString();
                if (message == "authorization_pending")
                    continue;
                if (message == "slow_down")
                {
                    interval += 5;
                    continue;
                }
                throw new InvalidOperationException("Twitch の認可が完了しませんでした。もう一度お試しください。");
            }
            response.EnsureSuccessStatusCode();
        }
        throw new InvalidOperationException("Twitch のログイン待機時間が過ぎました。");
    }

    private async Task<string> TwitchUserTokenAsync(TwitchAccount account, Action<string> save,
        CancellationToken cancellation)
    {
        if (unsavedRefreshToken is not null)
        {
            save(unsavedRefreshToken);
            unsavedRefreshToken = null;
        }
        if (accessToken is not null && DateTimeOffset.UtcNow < expiresAt)
            return accessToken;

        JsonElement token;
        try
        {
            token = await PostAsync(TwitchToken, new()
            {
                ["client_id"] = account.ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = twitchRefreshToken ?? account.RefreshToken!
            }, cancellation);
        }
        catch (HttpRequestException error) when (error.StatusCode is System.Net.HttpStatusCode.BadRequest
            or System.Net.HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("Twitch でもう一度ログインしてください。", error);
        }
        twitchRefreshToken = token.GetProperty("refresh_token").GetString()!;
        unsavedRefreshToken = twitchRefreshToken;
        accessToken = token.GetProperty("access_token").GetString()!;
        expiresAt = DateTimeOffset.UtcNow.AddSeconds(token.GetProperty("expires_in").GetInt32() - 60);
        save(unsavedRefreshToken);
        unsavedRefreshToken = null;
        return accessToken;
    }
}
