using L = Diga.Core.Localization.AppText;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Diga.Core.Cloud;

public sealed class CloudAuthService
{
    internal static readonly HttpClient DefaultHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
    private readonly HttpClient _http;
    private readonly ICloudTokenStore _tokens;
    private readonly Action<Uri> _launchBrowser;
    private readonly Func<int> _choosePort;

    /// <summary>How long the drive-label question may take before sign-in continues without a label.</summary>
    public TimeSpan DriveLookupTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public CloudAuthService(HttpClient? http = null, ICloudTokenStore? tokenStore = null, Action<Uri>? launchBrowser = null, Func<int>? choosePort = null)
    {
        _http = http ?? DefaultHttp;
        _tokens = tokenStore ?? new ProtectedTokenStore();
        _launchBrowser = launchBrowser ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        _choosePort = choosePort ?? (() => RandomNumberGenerator.GetInt32(49152, 65535));
    }

    public Task<CloudAccount?> GetSavedAccountAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default) => _tokens.LoadAsync(provider, clientId, cancellationToken);
    public Task DisconnectAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default) => _tokens.DeleteAsync(provider, clientId, cancellationToken);
    public Task<CloudAccount> AuthenticateAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default) => AuthenticateAsync(new(provider, clientId), cancellationToken);

    public async Task<CloudAccount> AuthenticateAsync(OAuthClientOptions options, CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var ct = timeout.Token;
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var (listener, redirect) = StartListener();
        using var listening = listener;
        var query = new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId, ["redirect_uri"] = redirect, ["response_type"] = "code",
            ["scope"] = Scope(options.Provider), ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256"
        };
        if (options.Provider == CloudProvider.GoogleDrive) { query["access_type"] = "offline"; query["prompt"] = "consent"; }
        // Without select_account Microsoft silently signs in the browser's only current account, which may be the wrong one.
        else { query["response_mode"] = "query"; query["prompt"] = "select_account"; }
        _launchBrowser(new Uri(AuthorizeEndpoint(options.Provider) + "?" + EncodeQuery(query)));
        string code;
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
            var incomingState = context.Request.QueryString["state"] ?? "";
            var valid = context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/" &&
                CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(incomingState), Encoding.UTF8.GetBytes(state));
            if (!valid)
            {
                context.Response.StatusCode = 400;
                context.Response.ContentLength64 = 0;
                context.Response.KeepAlive = false;
                context.Response.Close();
                continue;
            }
            code = context.Request.QueryString["code"] ?? "";
            var error = context.Request.QueryString["error"];
            var declined = error == "access_denied" || error == null && string.IsNullOrWhiteSpace(code);
            var content = Encoding.UTF8.GetBytes(L.T(declined ? "Core.Cloud.Auth.CallbackCancelled" : error != null ? "Core.Cloud.Auth.CallbackFailed" : "Core.Cloud.Auth.CallbackComplete"));
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.ContentLength64 = content.Length;
            context.Response.KeepAlive = false;
            context.Response.Headers["Cache-Control"] = "no-store";
            await context.Response.OutputStream.WriteAsync(content, ct).ConfigureAwait(false);
            context.Response.Close();
            if (declined) throw new InvalidOperationException(L.T("Core.Cloud.Auth.Declined"));
            if (error != null) throw new InvalidOperationException(CloudErrorDetail.WithDetail(L.T("Core.Cloud.Auth.CallbackError"), CloudErrorDetail.Describe(error, context.Request.QueryString["error_description"]).Detail));
            break;
        }
        var form = new Dictionary<string, string> { ["client_id"] = options.ClientId, ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code" };
        if (!string.IsNullOrEmpty(options.ClientSecret)) form["client_secret"] = options.ClientSecret;
        var account = await ExchangeAsync(options, form, null, ct).ConfigureAwait(false);
        if (options.Provider == CloudProvider.OneDrive) account = await DescribeDriveAsync(account, ct).ConfigureAwait(false);
        await _tokens.SaveAsync(account, ct).ConfigureAwait(false);
        return account;
    }

    private (HttpListener Listener, string Redirect) StartListener()
    {
        for (var attempt = 0; ; attempt++)
        {
            // The redirect is sent without a trailing slash, as the provider libraries do; providers ignore only the loopback port.
            var redirect = FormattableString.Invariant($"http://localhost:{_choosePort()}");
            // A listener whose Start failed is closed and cannot be given another prefix, so each attempt needs its own.
            var listener = new HttpListener();
            try
            {
                listener.Prefixes.Add(redirect + "/");
                listener.Start();
                return (listener, redirect);
            }
            catch (HttpListenerException) when (attempt < 11) { listener.Close(); }
            catch { listener.Close(); throw; }
        }
    }

    public async Task<CloudAccount> RefreshAsync(CloudAccount account, CancellationToken cancellationToken = default)
    {
        if (account.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return account;
        if (string.IsNullOrEmpty(account.RefreshToken)) throw new InvalidOperationException(L.T("Core.Cloud.Auth.Expired"));
        var options = new OAuthClientOptions(account.Provider, account.ClientId, account.ClientSecret);
        ValidateOptions(options);
        var form = new Dictionary<string, string> { ["client_id"] = account.ClientId, ["refresh_token"] = account.RefreshToken, ["grant_type"] = "refresh_token" };
        if (!string.IsNullOrEmpty(options.ClientSecret)) form["client_secret"] = options.ClientSecret;
        if (account.Provider == CloudProvider.OneDrive) form["scope"] = Scope(account.Provider);
        var refreshed = await ExchangeAsync(options, form, account, cancellationToken).ConfigureAwait(false);
        await _tokens.SaveAsync(refreshed, cancellationToken).ConfigureAwait(false);
        return refreshed;
    }

    private async Task<CloudAccount> ExchangeAsync(OAuthClientOptions options, Dictionary<string, string> form, CloudAccount? existing, CancellationToken cancellationToken)
    {
        using var body = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(TokenEndpoint(options.Provider), body, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var (errorCode, detail) = await CloudErrorDetail.ReadAsync(response, cancellationToken).ConfigureAwait(false);
            // invalid_grant on a refresh means the saved sign-in was revoked or expired, not a registration fault.
            var message = existing != null && errorCode == "invalid_grant" ? L.T("Core.Cloud.Auth.Expired") : L.T("Core.Cloud.Auth.Failed", (int)response.StatusCode);
            throw new InvalidOperationException(CloudErrorDetail.WithDetail(message, detail));
        }
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var accessToken = root.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidDataException(L.T("Core.Cloud.Auth.MissingToken"));
        var expires = root.TryGetProperty("expires_in", out var expiry) && expiry.TryGetInt32(out var seconds) ? Math.Clamp(seconds, 1, 86400) : 3600;
        return new() { Provider = options.Provider, ClientId = options.ClientId, ClientSecret = options.ClientSecret, AccessToken = accessToken, RefreshToken = root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : existing?.RefreshToken, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expires), DriveType = existing?.DriveType, OwnerName = existing?.OwnerName };
    }

    /// <summary>Asks which drive the new sign-in reaches, so the app can name it. A failure here does not fail the sign-in.</summary>
    private async Task<CloudAccount> DescribeDriveAsync(CloudAccount account, CancellationToken cancellationToken)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(DriveLookupTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/drive?$select=driveType,owner");
            request.Headers.Authorization = new("Bearer", account.AccessToken);
            using var response = await _http.SendAsync(request, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return account;
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false), cancellationToken: limit.Token).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return account;
            var driveType = root.TryGetProperty("driveType", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
            var ownerName = root.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                ? CloudErrorDetail.Describe(null, user.TryGetProperty("displayName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null).Detail : null;
            return new() { Provider = account.Provider, ClientId = account.ClientId, ClientSecret = account.ClientSecret, AccessToken = account.AccessToken, RefreshToken = account.RefreshToken, ExpiresAt = account.ExpiresAt, DriveType = driveType, OwnerName = ownerName };
        }
        // Whatever goes wrong with the label, including this question's own time limit, the sign-in stands; only the caller's cancellation ends it.
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return account; }
    }

    public static string CreatePkceChallenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string EncodeQuery(Dictionary<string, string> pairs) => string.Join('&', pairs.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    private static void ValidateOptions(OAuthClientOptions options)
    {
        if (!Enum.IsDefined(options.Provider)) throw new ArgumentOutOfRangeException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.ClientId)) throw new ArgumentException(L.T("Core.Cloud.Auth.ClientIdRequired"), nameof(options));
    }
    private static string Scope(CloudProvider provider) => provider == CloudProvider.GoogleDrive ? "https://www.googleapis.com/auth/drive.file" : "offline_access https://graph.microsoft.com/Files.ReadWrite";
    private static string AuthorizeEndpoint(CloudProvider provider) => provider == CloudProvider.GoogleDrive ? "https://accounts.google.com/o/oauth2/v2/auth" : "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
    private static string TokenEndpoint(CloudProvider provider) => provider == CloudProvider.GoogleDrive ? "https://oauth2.googleapis.com/token" : "https://login.microsoftonline.com/common/oauth2/v2.0/token";
}
