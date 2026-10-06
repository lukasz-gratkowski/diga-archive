using Diga.Core.Cloud;
using System.Net;
using System.Text;

namespace Diga.Tests;

public sealed class CloudAuthRegressionTests
{
    [Fact]
    public async Task OptionalDesktopSecretSurvivesSignInAndRefresh()
    {
        const string secret = "test optional secret&+";
        var tokens = new MemoryTokens();
        int requests = 0;
        Task? callback = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            var form = ParseQuery(await request.Content!.ReadAsStringAsync());
            Assert.Equal(secret, form["client_secret"]);
            requests++;
            Assert.Equal(requests == 1 ? "authorization_code" : "refresh_token", form["grant_type"]);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(requests == 1
                    ? "{\"access_token\":\"initial-access\",\"refresh_token\":\"saved-refresh\",\"expires_in\":1}"
                    : "{\"access_token\":\"new-access\",\"expires_in\":3600}", Encoding.UTF8, "application/json")
            };
        }));
        var auth = new CloudAuthService(http, tokens, uri =>
        {
            var query = ParseQuery(uri.Query.TrimStart('?'));
            Assert.DoesNotContain(secret, uri.AbsoluteUri);
            callback = Task.Run(async () =>
            {
                using var loopback = new HttpClient();
                using var result = await loopback.GetAsync(query["redirect_uri"] + "?code=test-code&state=" + Uri.EscapeDataString(query["state"]));
                result.EnsureSuccessStatusCode();
            });
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var initial = await auth.AuthenticateAsync(new(CloudProvider.GoogleDrive, "test-client", secret), timeout.Token);
        await callback!;
        Assert.Equal(secret, initial.ClientSecret);
        var refreshed = await auth.RefreshAsync(initial, timeout.Token);
        Assert.Equal(2, requests);
        Assert.Equal("new-access", refreshed.AccessToken);
        Assert.Equal("saved-refresh", refreshed.RefreshToken);
        Assert.Equal(secret, tokens.Account!.ClientSecret);
        Assert.DoesNotContain(secret, refreshed.ToString());
        Assert.DoesNotContain(secret, new OAuthClientOptions(CloudProvider.GoogleDrive, "test-client", secret).ToString());
    }

    [Fact]
    public async Task CancelledAuthenticationDoesNotOpenBrowser()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var http = new HttpClient(new Handler(_ => throw new Exception("Unexpected network call.")));
        var auth = new CloudAuthService(http, new MemoryTokens(), _ => throw new Exception("Unexpected browser launch."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.AuthenticateAsync(CloudProvider.GoogleDrive, "client", cancellation.Token));
    }

    [Fact]
    public async Task OptionalSecretIsEncryptedWithAccount()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = Path.Combine(Path.GetTempPath(), "DigaAuthRegression-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProtectedTokenStore(directory);
            await store.SaveAsync(new() { Provider = CloudProvider.GoogleDrive, ClientId = "client", ClientSecret = "regression-secret-never-plaintext", AccessToken = "access" });
            string onDisk = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Directory.GetFiles(directory).Single()));
            Assert.DoesNotContain("regression-secret-never-plaintext", onDisk);
            Assert.Equal("regression-secret-never-plaintext", (await store.LoadAsync(CloudProvider.GoogleDrive, "client"))!.ClientSecret);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static Dictionary<string, string> ParseQuery(string query) => query.Split('&').Select(part => part.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return handle(request); }
    }
    private sealed class MemoryTokens : ICloudTokenStore
    {
        public CloudAccount? Account { get; private set; }
        public Task<CloudAccount?> LoadAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default) => Task.FromResult(Account);
        public Task SaveAsync(CloudAccount account, CancellationToken cancellationToken = default) { Account = account; return Task.CompletedTask; }
        public Task DeleteAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default) { Account = null; return Task.CompletedTask; }
    }
}
