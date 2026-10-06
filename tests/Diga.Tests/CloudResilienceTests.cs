using System.Net;
using System.Text;
using Diga.Core.Cloud;
using Xunit;

namespace Diga.Tests;

/// <summary>What the cloud code does when things are not as they should be: an unreadable saved sign-in, a sign-in nobody finishes, a drive without room.</summary>
public sealed class CloudResilienceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DigaCloudResilience-" + Guid.NewGuid().ToString("N"));
    public CloudResilienceTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task ASavedSignInThatCannotBeDecryptedCountsAsNoSignIn()
    {
        var store = new ProtectedTokenStore(_directory);
        await store.SaveAsync(new CloudAccount { Provider = CloudProvider.GoogleDrive, ClientId = "client", AccessToken = "access", RefreshToken = "refresh", ClientSecret = "secret", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        var file = Assert.Single(Directory.GetFiles(_directory));
        Assert.Equal("secret", (await store.LoadAsync(CloudProvider.GoogleDrive, "client"))!.ClientSecret);
        // What a profile restored on another PC, or after a reset Windows password, looks like to this account.
        await File.WriteAllBytesAsync(file, Encoding.UTF8.GetBytes("this was protected for somebody else"));
        Assert.Null(await store.LoadAsync(CloudProvider.GoogleDrive, "client"));
        // Connecting again replaces the unreadable file.
        await store.SaveAsync(new CloudAccount { Provider = CloudProvider.GoogleDrive, ClientId = "client", AccessToken = "new", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        Assert.Equal("new", (await store.LoadAsync(CloudProvider.GoogleDrive, "client"))!.AccessToken);
    }

    [Fact]
    public async Task ASavedSignInIsNotReadableAsPlainText()
    {
        var store = new ProtectedTokenStore(_directory);
        await store.SaveAsync(new CloudAccount { Provider = CloudProvider.OneDrive, ClientId = "client", AccessToken = "access-token-value", RefreshToken = "refresh-token-value", ClientSecret = "client-secret-value", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        var stored = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(_directory))));
        Assert.DoesNotContain("access-token-value", stored);
        Assert.DoesNotContain("refresh-token-value", stored);
        Assert.DoesNotContain("client-secret-value", stored);
    }

    [Fact]
    public async Task ASignInNobodyFinishesEndsWithItsOwnMessageNotAsACancellation()
    {
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("No request is expected before the browser answers.")));
        var opened = 0;
        var auth = new CloudAuthService(http, new MemoryTokens(), _ => opened++) { SignInTimeout = TimeSpan.FromMilliseconds(300) };
        var error = await Assert.ThrowsAsync<TimeoutException>(() => auth.AuthenticateAsync(CloudProvider.OneDrive, "client"));
        Assert.Equal(1, opened);
        Assert.Equal(Diga.Core.Localization.AppText.T("Core.Cloud.Auth.TimedOut", 0), error.Message);
    }

    [Fact]
    public async Task AServiceThatDoesNotAnswerAfterTheBrowserStepIsNotCalledAnUnfinishedSignIn()
    {
        // What HttpClient raises when its own time limit runs out: a cancellation that neither the caller nor the sign-in limit asked for.
        using var http = new HttpClient(new Handler(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.")));
        Task? browser = null;
        var auth = new CloudAuthService(http, new MemoryTokens(), uri =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var redirect = new Uri(query["redirect_uri"]!);
            browser = Task.Run(async () =>
            {
                using var client = new HttpClient();
                using var done = await client.GetAsync($"http://localhost:{redirect.Port}/?code=c&state={Uri.EscapeDataString(query["state"]!)}");
            });
        });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => auth.AuthenticateAsync(CloudProvider.OneDrive, "client", limit.Token));
        await browser!;
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.False(limit.IsCancellationRequested);
    }

    [Fact]
    public async Task TheUsersOwnCancellationOfASignInStaysACancellation()
    {
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("No request is expected.")));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var auth = new CloudAuthService(http, new MemoryTokens(), _ => { });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.AuthenticateAsync(CloudProvider.OneDrive, "client", cancel.Token));
    }

    [Theory]
    [InlineData(CloudProvider.OneDrive, "{\"quota\":{\"deleted\":0,\"remaining\":3221225472,\"state\":\"normal\",\"total\":5368709120,\"used\":2147483648}}", 3221225472L)]
    [InlineData(CloudProvider.GoogleDrive, "{\"storageQuota\":{\"limit\":\"16106127360\",\"usage\":\"6106127360\",\"usageInDrive\":\"1\"}}", 10000000000L)]
    // More used than the limit (a downgraded plan) is no room, not a negative number.
    [InlineData(CloudProvider.GoogleDrive, "{\"storageQuota\":{\"limit\":\"100\",\"usage\":\"250\"}}", 0L)]
    public async Task FreeSpaceIsReadFromTheProvidersQuota(CloudProvider provider, string answer, long expected)
    {
        var calls = new List<HttpRequestMessage>();
        using var http = new HttpClient(new Handler(request => { calls.Add(request); return Json(HttpStatusCode.OK, answer); }));
        Assert.Equal(expected, await new CloudBrowseService(http, new CloudAuthService(http, new MemoryTokens())).GetFreeSpaceAsync(Account(provider)));
        var call = Assert.Single(calls);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal("Bearer access-token", call.Headers.Authorization!.ToString());
        Assert.Equal(provider == CloudProvider.OneDrive
            ? "https://graph.microsoft.com/v1.0/me/drive?$select=quota"
            : "https://www.googleapis.com/drive/v3/about?fields=storageQuota(limit,usage)", call.RequestUri!.OriginalString);
    }

    [Theory]
    // An unlimited Google plan names no limit; an answer in another shape tells nothing.
    [InlineData(CloudProvider.GoogleDrive, "{\"storageQuota\":{\"usage\":\"250\"}}")]
    [InlineData(CloudProvider.GoogleDrive, "{\"storageQuota\":{\"limit\":\"-5\",\"usage\":\"1\"}}")]
    [InlineData(CloudProvider.OneDrive, "{\"quota\":{\"remaining\":\"plenty\"}}")]
    [InlineData(CloudProvider.OneDrive, "{\"quota\":{\"remaining\":-1}}")]
    [InlineData(CloudProvider.OneDrive, "{\"value\":[]}")]
    [InlineData(CloudProvider.OneDrive, "[1,2,3]")]
    [InlineData(CloudProvider.OneDrive, "not json")]
    public async Task AnAnswerWithoutAUsableLimitMeansTheFreeSpaceIsUnknown(CloudProvider provider, string answer)
    {
        using var http = new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, answer)));
        Assert.Null(await new CloudBrowseService(http, new CloudAuthService(http, new MemoryTokens())).GetFreeSpaceAsync(Account(provider)));
    }

    [Fact]
    public async Task ASignInTheProviderNoLongerAcceptsIsReportedAsSuchWhenAskingForFreeSpace()
    {
        using var http = new HttpClient(new Handler(_ => Json(HttpStatusCode.Unauthorized, "{\"error\":{\"code\":\"InvalidAuthenticationToken\",\"message\":\"Expired\"}}")));
        var error = await Assert.ThrowsAsync<CloudSignInExpiredException>(() => new CloudBrowseService(http, new CloudAuthService(http, new MemoryTokens())).GetFreeSpaceAsync(Account(CloudProvider.OneDrive)));
        Assert.Equal(CloudProvider.OneDrive, error.Provider);
    }

    [Fact]
    public async Task TheOneDriveSignInListensOnThisPcOnly()
    {
        // An address of this PC that other machines can reach; a PC without a network has none, and then only the rest is checked.
        var outside = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(card => card.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && card.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(card => card.GetIPProperties().UnicastAddresses).Select(entry => entry.Address)
            .FirstOrDefault(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));
        using var http = new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}")));
        Task? browser = null;
        var auth = new CloudAuthService(http, new MemoryTokens(), uri =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var redirect = new Uri(query["redirect_uri"]!);
            Assert.Equal("localhost", redirect.Host);
            browser = Task.Run(async () =>
            {
                if (outside is not null)
                {
                    using var socket = new System.Net.Sockets.TcpClient(System.Net.Sockets.AddressFamily.InterNetwork);
                    await Assert.ThrowsAnyAsync<System.Net.Sockets.SocketException>(() => socket.ConnectAsync(outside, redirect.Port));
                }
                using var client = new HttpClient();
                // The loopback address answers, and so does the name the browser is sent to.
                using var stranger = await client.GetAsync($"http://127.0.0.1:{redirect.Port}/?code=c&state=wrong");
                Assert.Equal(HttpStatusCode.BadRequest, stranger.StatusCode);
                using var done = await client.GetAsync($"http://localhost:{redirect.Port}/?code=c&state={Uri.EscapeDataString(query["state"]!)}");
                Assert.Equal(HttpStatusCode.OK, done.StatusCode);
            });
        });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Assert.Equal("access", (await auth.AuthenticateAsync(CloudProvider.OneDrive, "client", limit.Token)).AccessToken);
        await browser!;
    }

    [Fact]
    public async Task DisconnectingGoogleAsksGoogleToEndTheSignInAndRemovesItFromThisPc()
    {
        var tokens = new SavedTokens(Account(CloudProvider.GoogleDrive));
        var calls = new List<(string Method, string Address, string Body)>();
        using var http = new HttpClient(new Handler(request =>
        {
            calls.Add((request.Method.Method, request.RequestUri!.AbsoluteUri, request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            Assert.Null(request.Headers.Authorization);
            return Json(HttpStatusCode.OK, "{}");
        }));
        Assert.True(await new CloudAuthService(http, tokens).DisconnectAsync(CloudProvider.GoogleDrive, "client"));
        Assert.Equal(("POST", "https://oauth2.googleapis.com/revoke", "token=refresh-token"), Assert.Single(calls));
        Assert.Null(tokens.Saved);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASignInGoogleDoesNotEndStillLeavesThisPc(bool googleAnswers)
    {
        var tokens = new SavedTokens(Account(CloudProvider.GoogleDrive));
        using var http = new HttpClient(new Handler(_ => googleAnswers ? Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_token\"}") : throw new HttpRequestException("offline")));
        Assert.False(await new CloudAuthService(http, tokens).DisconnectAsync(CloudProvider.GoogleDrive, "client"));
        Assert.Null(tokens.Saved);
    }

    [Fact]
    public async Task DisconnectingOneDriveSendsNothing()
    {
        var tokens = new SavedTokens(Account(CloudProvider.OneDrive));
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("Microsoft has no address to send a disconnect to.")));
        Assert.False(await new CloudAuthService(http, tokens).DisconnectAsync(CloudProvider.OneDrive, "client"));
        Assert.Null(tokens.Saved);
    }

    [Fact]
    public async Task RemovingASignInRemovesEveryOneSavedForThatProviderAndNoOther()
    {
        var store = new ProtectedTokenStore(_directory);
        foreach (var client in new[] { "an earlier client", "client" })
            await store.SaveAsync(new CloudAccount { Provider = CloudProvider.GoogleDrive, ClientId = client, AccessToken = "access", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        await store.SaveAsync(Account(CloudProvider.OneDrive));
        await File.WriteAllTextAsync(Path.Combine(_directory, "GoogleDrive-0123.bin.4567.tmp"), "left by a save that was interrupted");
        Assert.Equal(4, Directory.GetFiles(_directory).Length);
        await store.DeleteAsync(CloudProvider.GoogleDrive, "client");
        Assert.StartsWith("OneDrive-", Path.GetFileName(Assert.Single(Directory.GetFiles(_directory))), StringComparison.Ordinal);
        Assert.Equal("access-token", (await store.LoadAsync(CloudProvider.OneDrive, "client"))!.AccessToken);
        // Nothing saved at all is not an error either.
        await new ProtectedTokenStore(Path.Combine(_directory, "never created")).DeleteAsync(CloudProvider.OneDrive, "client");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("{}")]
    [InlineData("{\"access_token\":42}")]
    [InlineData("{\"access_token\":\"  \"}")]
    public async Task ATokenAnswerInAnotherShapeIsReportedInTheApplicationsOwnWords(string answer)
    {
        using var http = new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, answer)));
        var expired = new CloudAccount { Provider = CloudProvider.OneDrive, ClientId = "client", AccessToken = "old", RefreshToken = "refresh-token", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new CloudAuthService(http, new MemoryTokens()).RefreshAsync(expired));
        Assert.Equal(Diga.Core.Localization.AppText.T("Core.Cloud.Auth.MissingToken"), error.Message);
    }

    [Fact]
    public async Task ATokenAnswerMayLeaveOutTheRefreshTokenAndGiveTheLifetimeAsText()
    {
        using var http = new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, "{\"access_token\":\"new\",\"expires_in\":\"3600\",\"refresh_token\":null}")));
        var expired = new CloudAccount { Provider = CloudProvider.GoogleDrive, ClientId = "client", ClientSecret = "secret", AccessToken = "old", RefreshToken = "kept", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        var refreshed = await new CloudAuthService(http, new MemoryTokens()).RefreshAsync(expired);
        Assert.Equal("new", refreshed.AccessToken);
        Assert.Equal("kept", refreshed.RefreshToken);
        Assert.InRange(refreshed.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(55), DateTimeOffset.UtcNow.AddMinutes(65));
    }

    [Theory]
    [InlineData("not json", "Core.Cloud.Upload.UnexpectedResponse")]
    [InlineData("[1,2]", "Core.Cloud.Upload.UnsafeSession")]
    [InlineData("{}", "Core.Cloud.Upload.UnsafeSession")]
    [InlineData("{\"uploadUrl\":7}", "Core.Cloud.Upload.UnsafeSession")]
    public async Task AnUploadSessionAnswerInAnotherShapeIsReportedInTheApplicationsOwnWords(string answer, string expected)
    {
        var path = Path.Combine(_directory, "recording.mkv");
        await File.WriteAllBytesAsync(path, new byte[64]);
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return Json(HttpStatusCode.OK, answer); }));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new CloudUploadService(http, new CloudAuthService(http, new MemoryTokens())).UploadAsync(new(Account(CloudProvider.OneDrive), path)));
        Assert.Equal(Diga.Core.Localization.AppText.T(expected), error.Message);
        // Nothing of the file was sent anywhere.
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AGoogleUploadAddressThatIsNotAFullAddressIsRefused()
    {
        var path = Path.Combine(_directory, "recording.mkv");
        await File.WriteAllBytesAsync(path, new byte[64]);
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            var response = Json(HttpStatusCode.OK, "{}");
            response.Headers.Location = new Uri("/upload/session", UriKind.Relative);
            return response;
        }));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new CloudUploadService(http, new CloudAuthService(http, new MemoryTokens())).UploadAsync(new(Account(CloudProvider.GoogleDrive), path)));
        Assert.Equal(Diga.Core.Localization.AppText.T("Core.Cloud.Upload.UnsafeSession"), error.Message);
        Assert.Equal(1, calls);
    }

    private const string OneDriveSession = "{\"uploadUrl\":\"https://sn3302.up.1drv.com/up/session\"}";
    private const string OneDriveStored = "{\"id\":\"item-1\",\"name\":\"recording.mkv\",\"webUrl\":\"https://onedrive.live.com/item-1\"}";

    private string SmallFile()
    {
        var path = Path.Combine(_directory, "recording.mkv");
        File.WriteAllBytes(path, new byte[64]);
        return path;
    }

    private sealed class Reports : IProgress<CloudUploadProgress>
    {
        public List<CloudUploadProgress> Values { get; } = [];
        public void Report(CloudUploadProgress value) { lock (Values) Values.Add(value); }
    }

    [Fact]
    public async Task AnUploadWaitsOutAnInterruptionLongerThanAFewTriesAndSaysThatItIsWaiting()
    {
        var calls = new List<string>();
        var lost = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls.Add(request.Method.Method);
            if (request.Method == HttpMethod.Post) return Json(HttpStatusCode.OK, OneDriveSession);
            // Nothing answers eight times in a row: more than the five tries an answer that asks to wait gets.
            if (lost < 8) { lost++; throw new HttpRequestException("connection lost"); }
            // Asked what it holds, the provider has nothing yet; the file is then sent and stored.
            return request.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, "{\"nextExpectedRanges\":[\"0-\"]}") : Json(HttpStatusCode.Created, OneDriveStored);
        }));
        var reports = new Reports();
        var service = new CloudUploadService(http, new CloudAuthService(http, new MemoryTokens())) { MinimumRetryDelay = TimeSpan.FromMilliseconds(1) };
        var result = await service.UploadAsync(new(Account(CloudProvider.OneDrive), SmallFile()), reports);
        Assert.Equal("item-1", result.Id);
        Assert.Equal(8, lost);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], reports.Values.Where(value => value.Retry > 0).Select(value => value.Retry));
        Assert.Equal(new CloudUploadProgress(64, 64), reports.Values[^1]);
        // One session; after every failure the provider is asked what it holds before anything is sent again.
        Assert.Equal(1, calls.Count(method => method == "POST"));
        Assert.Equal("PUT", calls[^1]);
        Assert.Equal("GET", calls[^2]);
    }

    [Fact]
    public async Task AnUploadGivesUpWhenNothingAnswersForLongerThanItsPatience()
    {
        using var http = new HttpClient(new Handler(request => request.Method == HttpMethod.Post ? Json(HttpStatusCode.OK, OneDriveSession) : throw new HttpRequestException("connection lost")));
        var service = new CloudUploadService(http, new CloudAuthService(http, new MemoryTokens())) { MinimumRetryDelay = TimeSpan.FromMilliseconds(20), NetworkPatience = TimeSpan.FromMilliseconds(300) };
        var started = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => service.UploadAsync(new(Account(CloudProvider.OneDrive), SmallFile())));
        Assert.Equal("connection lost", error.Message);
        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task OpeningAnUploadIsTriedAgainLikeEveryLaterStep()
    {
        var posts = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method != HttpMethod.Post) return Json(HttpStatusCode.Created, OneDriveStored);
            return ++posts switch { 1 => throw new HttpRequestException("no route"), 2 => Json(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"code\":\"serviceNotAvailable\",\"message\":\"busy\"}}"), _ => Json(HttpStatusCode.OK, OneDriveSession) };
        }));
        var service = new CloudUploadService(http, new CloudAuthService(http, new MemoryTokens())) { MinimumRetryDelay = TimeSpan.FromMilliseconds(1) };
        Assert.Equal("item-1", (await service.UploadAsync(new(Account(CloudProvider.OneDrive), SmallFile()))).Id);
        Assert.Equal(3, posts);
    }

    [Fact]
    public async Task ASignInServiceThatIsBusyIsTriedAgainAndNotBlamedOnTheRegistration()
    {
        var tokenCalls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return ++tokenCalls < 3 ? Json(HttpStatusCode.ServiceUnavailable, "{\"error\":\"temporarily_unavailable\"}") : Json(HttpStatusCode.OK, "{\"access_token\":\"new\",\"expires_in\":3600}");
            return request.Method == HttpMethod.Post ? Json(HttpStatusCode.OK, OneDriveSession) : Json(HttpStatusCode.Created, OneDriveStored);
        }));
        var expired = new CloudAccount { Provider = CloudProvider.OneDrive, ClientId = "client", AccessToken = "old", RefreshToken = "refresh-token", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        var auth = new CloudAuthService(http, new MemoryTokens());
        var busy = await Assert.ThrowsAsync<HttpRequestException>(() => auth.RefreshAsync(expired));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        Assert.StartsWith(Diga.Core.Localization.AppText.T("Core.Cloud.Auth.Unavailable", 503), busy.Message, StringComparison.Ordinal);
        tokenCalls = 0;
        var service = new CloudUploadService(http, auth) { MinimumRetryDelay = TimeSpan.FromMilliseconds(1) };
        Assert.Equal("item-1", (await service.UploadAsync(new(expired, SmallFile()))).Id);
        Assert.Equal(3, tokenCalls);
    }

    [Fact]
    public async Task ASignInRefusedWhenAnUploadStartsIsReportedAsSuchAndRememberedForTheNextStart()
    {
        var tokens = new SavedTokens(Account(CloudProvider.OneDrive));
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return Json(HttpStatusCode.Unauthorized, "{\"error\":{\"code\":\"InvalidAuthenticationToken\",\"message\":\"Access token has expired or is not yet valid.\"}}"); }));
        var error = await Assert.ThrowsAsync<CloudSignInExpiredException>(() => new CloudUploadService(http, new CloudAuthService(http, tokens)).UploadAsync(new(Account(CloudProvider.OneDrive), SmallFile())));
        Assert.Equal(CloudProvider.OneDrive, error.Provider);
        Assert.Equal(1, calls);
        Assert.True(tokens.Saved!.SignInEnded);
        Assert.Equal("refresh-token", tokens.Saved.RefreshToken);
    }

    [Fact]
    public async Task ASignInTheProviderEndedIsWrittenDownAndANewSignInClearsIt()
    {
        var store = new ProtectedTokenStore(_directory);
        var expired = new CloudAccount { Provider = CloudProvider.GoogleDrive, ClientId = "client", ClientSecret = "secret", AccessToken = "old", RefreshToken = "refresh-token", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        await store.SaveAsync(expired);
        using var refused = new HttpClient(new Handler(_ => Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\",\"error_description\":\"Token has been expired or revoked.\"}")));
        await Assert.ThrowsAsync<CloudSignInExpiredException>(() => new CloudAuthService(refused, store).RefreshAsync(expired));
        var kept = (await store.LoadAsync(CloudProvider.GoogleDrive, "client"))!;
        // The file stays, with the client secret Google shows only once; it only says that the sign-in no longer works.
        Assert.True(kept.SignInEnded);
        Assert.Equal("secret", kept.ClientSecret);
        using var accepted = new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, "{\"access_token\":\"new\",\"refresh_token\":\"new-refresh\",\"expires_in\":3600}")));
        var renewed = await new CloudAuthService(accepted, store).RefreshAsync(kept with { SignInEnded = false });
        Assert.False(renewed.SignInEnded);
        Assert.False((await store.LoadAsync(CloudProvider.GoogleDrive, "client"))!.SignInEnded);
        // The label the application shows is worked out, not stored.
        Assert.DoesNotContain("DisplayName", System.Text.Json.JsonSerializer.Serialize(kept), StringComparison.Ordinal);
    }

    [Theory]
    // A name written with characters outside ASCII has an empty ASCII form here, although it ends in the provider's domain as written.
    [InlineData(CloudProvider.OneDrive, "https://evil.example／.sharepoint.com/x")]
    [InlineData(CloudProvider.OneDrive, "https://evil.example＃.sharepoint.com/x")]
    [InlineData(CloudProvider.GoogleDrive, "https://evil.example／.googleapis.com/x")]
    [InlineData(CloudProvider.GoogleDrive, "https://www.göögleapis.com/x")]
    public void AnUploadAddressWrittenOutsideAsciiIsRefused(CloudProvider provider, string address) =>
        Assert.Throws<InvalidDataException>(() => CloudUploadService.ValidateSessionUri(provider, address));

    private static CloudAccount Account(CloudProvider provider) => new() { Provider = provider, ClientId = "client", AccessToken = "access-token", RefreshToken = "refresh-token", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handle(request));
    }

    private sealed class SavedTokens(CloudAccount saved) : ICloudTokenStore
    {
        public CloudAccount? Saved { get; private set; } = saved;
        public Task<CloudAccount?> LoadAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default) => Task.FromResult(Saved);
        public Task SaveAsync(CloudAccount account, CancellationToken cancellationToken = default) { Saved = account; return Task.CompletedTask; }
        public Task DeleteAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default) { Saved = null; return Task.CompletedTask; }
    }

    private sealed class MemoryTokens : ICloudTokenStore
    {
        public Task<CloudAccount?> LoadAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default) => Task.FromResult<CloudAccount?>(null);
        public Task SaveAsync(CloudAccount account, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
