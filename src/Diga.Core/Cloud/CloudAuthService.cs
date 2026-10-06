using L = Diga.Core.Localization.AppText;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Diga.Core.Cloud;

public sealed class CloudAuthService
{
    // Every answer on the sign-in and upload paths is a few kilobytes, so nothing larger is ever taken into memory; the list of
    // cloud files reads its answers as streams under its own limit. Cookies from one account must not travel to the next.
    internal static readonly HttpClient DefaultHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromMinutes(5), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
    private readonly HttpClient _http;
    private readonly ICloudTokenStore _tokens;
    private readonly Action<Uri> _launchBrowser;
    private readonly Func<int> _choosePort;

    /// <summary>How long the drive-label question may take before sign-in continues without a label.</summary>
    public TimeSpan DriveLookupTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>How long the user has to finish the sign-in in the browser.</summary>
    public TimeSpan SignInTimeout { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>How long Google may take to confirm the end of a sign-in before the disconnect goes on without the confirmation.</summary>
    public TimeSpan RevokeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public CloudAuthService(HttpClient? http = null, ICloudTokenStore? tokenStore = null, Action<Uri>? launchBrowser = null, Func<int>? choosePort = null)
    {
        _http = http ?? DefaultHttp;
        _tokens = tokenStore ?? new ProtectedTokenStore();
        _launchBrowser = launchBrowser ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        _choosePort = choosePort ?? (() => RandomNumberGenerator.GetInt32(49152, 65535));
    }

    public Task<CloudAccount?> GetSavedAccountAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default) => _tokens.LoadAsync(provider, clientId, cancellationToken);

    /// <summary>
    /// Removes the saved sign-in from this PC. Google is first asked to end it, so that a copy of it works nowhere else either;
    /// Microsoft offers nothing of the kind for a personal account, where the permission is withdrawn on the account's own page.
    /// </summary>
    /// <returns>True when the provider confirmed that the sign-in has ended.</returns>
    public async Task<bool> DisconnectAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default)
    {
        var ended = false;
        if (provider == CloudProvider.GoogleDrive)
        {
            try
            {
                var account = await _tokens.LoadAsync(provider, clientId, cancellationToken).ConfigureAwait(false);
                // Ending the refresh token ends the access tokens issued for it as well.
                var token = string.IsNullOrEmpty(account?.RefreshToken) ? account?.AccessToken : account.RefreshToken;
                if (!string.IsNullOrEmpty(token))
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    limit.CancelAfter(RevokeTimeout);
                    using var body = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
                    using var response = await _http.PostAsync("https://oauth2.googleapis.com/revoke", body, limit.Token).ConfigureAwait(false);
                    ended = response.IsSuccessStatusCode;
                }
            }
            // Whatever Google answers, or does not answer, the sign-in leaves this PC; only the caller's cancellation stops that.
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
        }
        await _tokens.DeleteAsync(provider, clientId, cancellationToken).ConfigureAwait(false);
        return ended;
    }

    public Task<CloudAccount> AuthenticateAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default) => AuthenticateAsync(new(provider, clientId), cancellationToken);

    public async Task<CloudAccount> AuthenticateAsync(OAuthClientOptions options, CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SignInTimeout);
        // The limit running out is not the user cancelling: it gets its own message, which says to start again. Only this limit
        // gets it: a request to the service that ran into the HTTP client's own limit, after the user finished in the browser,
        // is a service that did not answer, and is reported as that.
        try { return await SignInAsync(options, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { throw new TimeoutException(L.T("Core.Cloud.Auth.TimedOut", (int)SignInTimeout.TotalMinutes)); }
    }

    private async Task<CloudAccount> SignInAsync(OAuthClientOptions options, CancellationToken ct)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var (listener, redirect) = StartListener(options.Provider);
        using var listening = listener;
        var query = new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId, ["redirect_uri"] = redirect, ["response_type"] = "code",
            ["scope"] = Scope(options.Provider), ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256"
        };
        // Without select_account the provider silently signs in the browser's only current account, which may be the wrong one.
        // Google additionally needs consent to be asked each time, or a repeated sign-in returns no refresh token.
        if (options.Provider == CloudProvider.GoogleDrive) { query["access_type"] = "offline"; query["prompt"] = "consent select_account"; }
        else { query["response_mode"] = "query"; query["prompt"] = "select_account"; }
        _launchBrowser(new Uri(AuthorizeEndpoint(options.Provider) + "?" + EncodeQuery(query)));
        string code;
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
            var incomingState = context.Request.QueryString["state"] ?? "";
            var valid = IPAddress.IsLoopback(context.Request.RemoteEndPoint.Address) && context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/" &&
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
        account = await DescribeDriveAsync(account, ct).ConfigureAwait(false);
        await _tokens.SaveAsync(account, ct).ConfigureAwait(false);
        return account;
    }

    private (HttpListener Listener, string Redirect) StartListener(CloudProvider provider)
    {
        // Google documents the loopback address for desktop clients; Microsoft's registration instructions use the name localhost.
        var google = provider == CloudProvider.GoogleDrive;
        for (var attempt = 0; ; attempt++)
        {
            var port = _choosePort();
            // The redirect is sent without a trailing slash, as the provider libraries do; providers ignore only the loopback port.
            var redirect = FormattableString.Invariant($"http://{(google ? "127.0.0.1" : "localhost")}:{port}");
            var ipv4 = FormattableString.Invariant($"http://127.0.0.1:{port}/");
            // Listening on the name localhost would open the port on every network interface. The two loopback addresses are what
            // the name stands for in a browser, and nothing outside this PC reaches them. A PC without IPv6 gets the first alone.
            string[][] choices = google ? [[ipv4]] : [[ipv4, FormattableString.Invariant($"http://[::1]:{port}/")], [ipv4]];
            foreach (var prefixes in choices)
            {
                // A listener whose Start failed is closed and cannot be given another prefix, so each attempt needs its own.
                var listener = new HttpListener();
                try
                {
                    foreach (var prefix in prefixes) listener.Prefixes.Add(prefix);
                    listener.Start();
                    return (listener, redirect);
                }
                catch (HttpListenerException) when (attempt < 11 || prefixes.Length > 1) { listener.Close(); }
                catch { listener.Close(); throw; }
            }
        }
    }

    public async Task<CloudAccount> RefreshAsync(CloudAccount account, CancellationToken cancellationToken = default)
    {
        if (account.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return account;
        if (string.IsNullOrEmpty(account.RefreshToken))
        {
            await RememberEndedAsync(account, cancellationToken).ConfigureAwait(false);
            throw new CloudSignInExpiredException(account.Provider, ExpiredMessage(account.Provider));
        }
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
            if (existing != null && errorCode == "invalid_grant")
            {
                await RememberEndedAsync(existing, cancellationToken).ConfigureAwait(false);
                throw new CloudSignInExpiredException(options.Provider, CloudErrorDetail.WithDetail(ExpiredMessage(options.Provider), detail));
            }
            // A sign-in service that is busy or failing is no fault of the registration, and is worth another try.
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                throw new HttpRequestException(CloudErrorDetail.WithDetail(L.T("Core.Cloud.Auth.Unavailable", (int)response.StatusCode), detail), null, response.StatusCode);
            throw new InvalidOperationException(CloudErrorDetail.WithDetail(L.T("Core.Cloud.Auth.Failed", (int)response.StatusCode), detail));
        }
        JsonDocument document;
        // An answer that is not what a token endpoint sends (a proxy's page, an empty body) gets the application's own message.
        try { document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false); }
        catch (JsonException ex) { throw new InvalidDataException(L.T("Core.Cloud.Auth.MissingToken"), ex); }
        using (document)
        {
            var root = document.RootElement;
            string? Text(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var accessToken = Text("access_token");
            if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidDataException(L.T("Core.Cloud.Auth.MissingToken"));
            var expires = root.TryGetProperty("expires_in", out var expiry) && expiry.ValueKind == JsonValueKind.Number && expiry.TryGetInt32(out var seconds) ? Math.Clamp(seconds, 1, 86400) : 3600;
            return new() { Provider = options.Provider, ClientId = options.ClientId, ClientSecret = options.ClientSecret, AccessToken = accessToken, RefreshToken = Text("refresh_token") ?? existing?.RefreshToken, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expires), DriveType = existing?.DriveType, OwnerName = existing?.OwnerName };
        }
    }

    /// <summary>Asks which drive the new sign-in reaches, so the app can name it. A failure here does not fail the sign-in.</summary>
    private async Task<CloudAccount> DescribeDriveAsync(CloudAccount account, CancellationToken cancellationToken)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(DriveLookupTimeout);
            var google = account.Provider == CloudProvider.GoogleDrive;
            // Both questions are allowed by the permission the sign-in already has; neither asks for anything about the files.
            using var request = new HttpRequestMessage(HttpMethod.Get, google
                ? "https://www.googleapis.com/drive/v3/about?fields=user(displayName,emailAddress)"
                : "https://graph.microsoft.com/v1.0/me/drive?$select=driveType,owner");
            request.Headers.Authorization = new("Bearer", account.AccessToken);
            using var response = await _http.SendAsync(request, limit.Token).ConfigureAwait(false);
            // A Google project in which the Drive API was never switched on signs in normally and then refuses every Drive request.
            if (google && response.StatusCode == HttpStatusCode.Forbidden)
            {
                var (_, refusal) = await CloudErrorDetail.ReadAsync(response, limit.Token).ConfigureAwait(false);
                return account with { Notice = CloudErrorDetail.WithDetail(L.T("Core.Cloud.Auth.DriveRefused"), refusal) };
            }
            if (!response.IsSuccessStatusCode) return account;
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false), cancellationToken: limit.Token).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return account;
            var driveType = !google && root.TryGetProperty("driveType", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
            var user = default(JsonElement);
            var hasUser = google
                ? root.TryGetProperty("user", out user) && user.ValueKind == JsonValueKind.Object
                : root.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty("user", out user) && user.ValueKind == JsonValueKind.Object;
            string? Field(string name) => hasUser && user.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            // A Google account is easier to recognise by its address; Microsoft gives only the name with this permission.
            var ownerName = CloudErrorDetail.Describe(null, google ? Field("emailAddress") ?? Field("displayName") : Field("displayName")).Detail;
            return account with { DriveType = driveType, OwnerName = ownerName };
        }
        // Whatever goes wrong with the label, including this question's own time limit, the sign-in stands; only the caller's cancellation ends it.
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return account; }
    }

    /// <summary>
    /// Writes down that the provider no longer accepts this sign-in. The file stays, because it also holds the Google client
    /// secret; a new sign-in replaces it. A failure to write changes nothing about the refusal that is being reported.
    /// </summary>
    internal async Task RememberEndedAsync(CloudAccount account, CancellationToken cancellationToken)
    {
        if (account.SignInEnded) return;
        try { await _tokens.SaveAsync(account with { SignInEnded = true }, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
    }

    internal static string ExpiredMessage(CloudProvider provider) => L.T(provider == CloudProvider.GoogleDrive ? "Core.Cloud.Auth.ExpiredGoogle" : "Core.Cloud.Auth.Expired");

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
