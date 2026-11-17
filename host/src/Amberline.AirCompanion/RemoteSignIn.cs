using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Amberline.AirCompanion;

/// <summary>
/// NOT RETAIL: the friend's sign-in for the remote tunnels (retail has no tunnel). The friend signs in once, in the AIR client, with
/// the login the host created: AIR posts it to the login queue, the proxy passes it to the platform over the pinned HTTPS port, and the
/// platform answers with a per-account bearer. The companion caches the bearer in the stage's connections folder until it expires or
/// the platform refuses it. The login is never logged, written to disk or put in a URL. Batch 854: beside the bearer the cache keeps a
/// SHA-256 of the user name it was issued for (<see cref="LoginKey"/>), never the name itself, so a later AIR login as someone else signs
/// in again (<c>AirRemoteProxyHandle.SignInFromLoginAsync</c>).
/// </summary>
public static class RemoteSignIn
{
    private const string SignInPath = "/air/remote-token";
    private const string CacheFileName = "remote-token.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public sealed record Credentials(string Username, string Password);

    /// <summary>The bearer and the identity it was issued for: a cached token leaves the companion only for that host, port and pin.
    /// Batch 854: <paramref name="Login"/> is the <see cref="LoginKey"/> of the login it was issued for (null in a cache written before).</summary>
    private sealed record CachedToken(string Token, DateTimeOffset Expires, string Host, int Port, string CertificateSha256, string? Login = null)
    {
        public bool IsFor(AirRemoteInvitation invitation) =>
            string.Equals(Host, invitation.Host, StringComparison.OrdinalIgnoreCase) && Port == invitation.Port &&
            string.Equals(CertificateSha256, invitation.CertificateSha25664hex, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SignInReply(string Token, DateTimeOffset Expires);

    /// <summary>
    /// Startup: the invitation with the cached bearer when the platform still accepts it, or null. Never asks for a login: without a
    /// bearer the proxy starts empty, and the friend's login in the AIR client supplies one.
    /// </summary>
    public static async Task<AirRemoteInvitation?> TryCachedTokenAsync(AirRemoteInvitation invitation, string connectionsDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        using var client = AirRemoteProxy.CreatePinnedClient(Convert.FromHexString(invitation.CertificateSha25664hex));
        // A cached token leaves this process only for the host, port and pinned certificate it was issued by. The first request
        // on this client is the check itself, and its TLS handshake must pass the pin before any header is sent.
        if (ReadCache(CachePath(connectionsDirectory)) is { } cached && cached.IsFor(invitation) && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(1) &&
            IsHex64(cached.Token) && await IsAcceptedAsync(client, SignInUri(invitation), cached.Token, cancellationToken).ConfigureAwait(false))
            return WithToken(invitation, cached.Token);
        return null;
    }

    /// <summary>
    /// Exchanges the login for the account's bearer over the pinned client and caches the bearer for this host, port and pin when
    /// <paramref name="connectionsDirectory"/> is given. A refusal throws <see cref="HttpRequestException"/> with its status code:
    /// 403 for a wrong login, 429 while the address is throttled.
    /// </summary>
    internal static async Task<string> SignInAsync(HttpClient client, AirRemoteInvitation invitation, string? connectionsDirectory,
        Credentials credentials, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(SignInUri(invitation),
            new { username = credentials.Username, password = credentials.Password }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<SignInReply>(cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("The remote sign-in reply was empty.");
        if (!IsHex64(reply.Token)) throw new IOException("The remote sign-in reply carried an invalid token.");
        if (connectionsDirectory is not null)
            WriteCache(CachePath(connectionsDirectory), new CachedToken(reply.Token, reply.Expires, invitation.Host, invitation.Port,
                invitation.CertificateSha25664hex, LoginKey(credentials.Username)));
        return reply.Token;
    }

    /// <summary>
    /// Batch 854: whose bearer this is, without the login: a SHA-256 of the user name exactly as AIR posted it (the platform compares
    /// user names exactly, <c>PlatformAccounts.Authenticate</c>).
    /// </summary>
    internal static string LoginKey(string username) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(username)));

    /// <summary>Batch 854: the <see cref="LoginKey"/> the cache holds for <paramref name="token"/> on this host, port and pin; null when it holds none.</summary>
    internal static string? CachedLogin(string connectionsDirectory, AirRemoteInvitation invitation, string token) =>
        ReadCache(CachePath(connectionsDirectory)) is { } cached && cached.IsFor(invitation) && cached.Token == token ? cached.Login : null;

    /// <summary>
    /// The login AIR posts to the login queue: its <c>payload</c> field is <c>user=&lt;name&gt;,password=&lt;password&gt;</c>. It is split
    /// exactly as the platform splits it, so the companion sends the login the platform just accepted. Null for any other form.
    /// </summary>
    internal static Credentials? ReadLoginQueueForm(byte[] form)
    {
        var fields = QueryHelpers.ParseQuery(Encoding.UTF8.GetString(form));
        var payload = fields.TryGetValue("payload", out var values) ? values.ToString() : "";
        var split = payload.IndexOf(",password=", StringComparison.Ordinal);
        return payload.StartsWith("user=", StringComparison.Ordinal) && split >= 5
            ? new Credentials(payload[5..split], payload[(split + 10)..])
            : null;
    }

    private static AirRemoteInvitation WithToken(AirRemoteInvitation invitation, string token) =>
        invitation with { Token64hex = token, Bearer = null };

    private static Uri SignInUri(AirRemoteInvitation invitation) =>
        new(new UriBuilder(Uri.UriSchemeHttps, invitation.Host, invitation.Port).Uri, SignInPath);

    private static string CachePath(string connectionsDirectory) => Path.Combine(connectionsDirectory, CacheFileName);

    private static async Task<bool> IsAcceptedAsync(HttpClient client, Uri signInUri, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, signInUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    private static CachedToken? ReadCache(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<CachedToken>(File.ReadAllText(path), Json) : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static void WriteCache(string path, CachedToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(token, Json));
        File.Move(path + ".tmp", path, true);
    }

    private static bool IsHex64(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
