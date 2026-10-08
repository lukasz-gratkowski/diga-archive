using System.Net;
using System.Text;
using System.Text.Json;
using Diga.Core.Cloud;
using Xunit;

namespace Diga.Tests;

public sealed class CloudTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DigaCloudTests-" + Guid.NewGuid().ToString("N"));
    public CloudTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact] public void PkceMatchesRfc7636Vector() => Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", CloudAuthService.CreatePkceChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    [Theory]
    [InlineData(CloudProvider.GoogleDrive, "http://www.googleapis.com/upload")]
    [InlineData(CloudProvider.GoogleDrive, "https://googleapis.com.attacker.example/upload")]
    [InlineData(CloudProvider.GoogleDrive, "https://evilgoogleapis.com/upload")]
    [InlineData(CloudProvider.GoogleDrive, "https://localhost/upload")]
    [InlineData(CloudProvider.GoogleDrive, "https://www.googleapis.com:444/upload")]
    [InlineData(CloudProvider.OneDrive, "https://attacker.example/upload")]
    [InlineData(CloudProvider.OneDrive, "https://user@upload.1drv.com/path")]
    [InlineData(CloudProvider.OneDrive, "https://evilmicrosoftpersonalcontent.com/personal/upload")]
    [InlineData(CloudProvider.OneDrive, "https://my.microsoftpersonalcontent.com.attacker.example/personal/upload")]
    public void UnsafeUploadSessionHostsAreRejected(CloudProvider provider, string uri) => Assert.Throws<InvalidDataException>(() => CloudUploadService.ValidateSessionUri(provider, uri));
    [Theory]
    [InlineData(CloudProvider.GoogleDrive, "https://www.googleapis.com/upload/session")]
    [InlineData(CloudProvider.OneDrive, "https://sn3302.up.1drv.com/up/session")]
    [InlineData(CloudProvider.OneDrive, "https://tenant.sharepoint.com/upload/session")]
    [InlineData(CloudProvider.OneDrive, "https://api.onedrive.com/rup/session")]
    [InlineData(CloudProvider.OneDrive, "https://my.microsoftpersonalcontent.com/personal/0123456789abcdef/_api/v2.0/drive/items/session")]
    public void OfficialSessionHostsAreAccepted(CloudProvider provider, string uri) => Assert.Equal(uri, CloudUploadService.ValidateSessionUri(provider, uri).AbsoluteUri);

    [Theory] [InlineData(CloudProvider.GoogleDrive)] [InlineData(CloudProvider.OneDrive)]
    public async Task ChunkUploadUsesProviderProtocolAndPreservesExistingCloudFiles(CloudProvider provider)
    {
        var path = CreateFile(CloudUploadService.ChunkSize + 17, "Family film.mkv");
        var calls = new List<(string Method, string Uri, string? Authorization, string? Range, int BodySize, string? Body)>();
        using var http = new HttpClient(new Handler(async request =>
        {
            var bytes = request.Content == null ? [] : await request.Content.ReadAsByteArrayAsync();
            calls.Add((request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentRange?.ToString(), bytes.Length, request.Method == HttpMethod.Post ? Encoding.UTF8.GetString(bytes) : null));
            if (request.Method == HttpMethod.Post)
            {
                if (provider == CloudProvider.GoogleDrive)
                {
                    var response = Json(HttpStatusCode.OK, "{}"); response.Headers.Location = new("https://www.googleapis.com/upload/session"); return response;
                }
                return Json(HttpStatusCode.OK, "{\"uploadUrl\":\"https://up.1drv.com/session\"}");
            }
            if (calls.Count == 2)
            {
                if (provider == CloudProvider.OneDrive) return Json(HttpStatusCode.Accepted, $"{{\"nextExpectedRanges\":[\"{CloudUploadService.ChunkSize}-\"]}}");
                var response = Json((HttpStatusCode)308, ""); response.Headers.TryAddWithoutValidation("Range", $"bytes=0-{CloudUploadService.ChunkSize - 1}"); return response;
            }
            return Json(HttpStatusCode.Created, "{\"id\":\"done\",\"name\":\"Family film.mkv\"}");
        }));
        var result = await new CloudUploadService(http).UploadAsync(new(Account(provider), path));
        Assert.Equal("done", result.Id);
        Assert.Equal(3, calls.Count);
        Assert.Equal("POST", calls[0].Method);
        Assert.Equal("Bearer access-token", calls[0].Authorization);
        Assert.Equal($"bytes 0-{CloudUploadService.ChunkSize - 1}/{CloudUploadService.ChunkSize + 17}", calls[1].Range);
        Assert.Equal(CloudUploadService.ChunkSize, calls[1].BodySize);
        Assert.Equal(17, calls[2].BodySize);
        Assert.Equal(provider == CloudProvider.GoogleDrive ? "Bearer access-token" : null, calls[1].Authorization);
        if (provider == CloudProvider.OneDrive) Assert.Contains("\"@microsoft.graph.conflictBehavior\":\"rename\"", calls[0].Body!);
    }

    [Fact] public async Task MissingClientIdDoesNotOpenBrowserOrCallNetwork()
    {
        using var http = new HttpClient(new Handler(_ => throw new Exception("Network must not be used.")));
        var service = new CloudAuthService(http, new MemoryTokens(), _ => throw new Exception("Browser must not be opened."));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AuthenticateAsync(CloudProvider.GoogleDrive, ""));
    }
    [Fact] public async Task ExpiredTokenIsRefreshedAndPersisted()
    {
        var tokens = new MemoryTokens();
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/token", request.RequestUri!.AbsoluteUri);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("grant_type=refresh_token", body);
            Assert.Contains("refresh_token=refresh-token", body);
            return Json(HttpStatusCode.OK, "{\"access_token\":\"new-access\",\"refresh_token\":\"rotated-refresh\",\"expires_in\":3600}");
        }));
        var account = await new CloudAuthService(http, tokens).RefreshAsync(Account(CloudProvider.OneDrive, expired: true));
        Assert.Equal("new-access", account.AccessToken);
        Assert.Equal("rotated-refresh", tokens.Saved!.RefreshToken);
        Assert.DoesNotContain("new-access", account.ToString());
    }
    [Fact] public async Task NativeBrowserFlowValidatesStateAndPkce()
    {
        var tokens = new MemoryTokens();
        string? challenge = null;
        Task? callback = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            var form = ParseQuery(await request.Content!.ReadAsStringAsync());
            Assert.Equal("authorization_code", form["grant_type"]);
            Assert.Equal("test-code", form["code"]);
            Assert.Equal(challenge, CloudAuthService.CreatePkceChallenge(form["code_verifier"]));
            return Json(HttpStatusCode.OK, "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}");
        }));
        var auth = new CloudAuthService(http, tokens, uri =>
        {
            var query = ParseQuery(uri.Query.TrimStart('?'));
            challenge = query["code_challenge"];
            Assert.Equal("S256", query["code_challenge_method"]);
            callback = Task.Run(async () =>
            {
                using var loopback = new HttpClient();
                using var invalid = await loopback.GetAsync(query["redirect_uri"] + "?code=bad&state=wrong");
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                using var valid = await loopback.GetAsync(query["redirect_uri"] + "?code=test-code&state=" + Uri.EscapeDataString(query["state"]));
                Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
            });
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await auth.AuthenticateAsync(CloudProvider.GoogleDrive, "test-client", timeout.Token);
        await callback!;
        Assert.Equal("access", result.AccessToken);
        Assert.Same(result, tokens.Saved);
    }
    [Fact] public async Task EmptyFileCannotCreateRemoteSession()
    {
        var path = CreateFile(0);
        using var http = new HttpClient(new Handler(_ => throw new Exception("Network must not be used.")));
        await Assert.ThrowsAsync<InvalidDataException>(() => new CloudUploadService(http).UploadAsync(new(Account(CloudProvider.GoogleDrive), path)));
    }
    [Fact] public async Task AuthenticationFailureDoesNotRevealResponseSecrets()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(HttpStatusCode.BadRequest, "{\"secret\":\"sensitive-response\"}"))));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudAuthService(http, new MemoryTokens()).RefreshAsync(Account(CloudProvider.GoogleDrive, true)));
        Assert.DoesNotContain("sensitive-response", error.Message);
    }
    [Fact] public async Task CloudRejectsAcknowledgementsBeyondBytesSent()
    {
        var path = CreateFile(100);
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            if (++calls == 1) { var response = Json(HttpStatusCode.OK, "{}"); response.Headers.Location = new("https://www.googleapis.com/upload/session"); return Task.FromResult(response); }
            var bad = Json((HttpStatusCode)308, ""); bad.Headers.TryAddWithoutValidation("Range", "bytes=0-9999"); return Task.FromResult(bad);
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new CloudUploadService(http).UploadAsync(new(Account(CloudProvider.GoogleDrive), path)));
        Assert.Equal(2, calls);
    }
    [Fact] public async Task UploadIntoASharedFolderAddressesTheFolderByItsDriveAndItsId()
    {
        var path = CreateFile(10, "2026-0142.mkv");
        var folder = new CloudFolder("b!drive of anna", "01FOLDER!7", "Recordings", new Uri("https://contoso-my.sharepoint.com/:f:/g/personal/anna/Eh?e=1"));
        var calls = new List<(string Method, string Uri, string? Authorization)>();
        using var http = new HttpClient(new Handler(request =>
        {
            calls.Add((request.Method.Method, request.RequestUri!.OriginalString, request.Headers.Authorization?.ToString()));
            return Task.FromResult(request.Method == HttpMethod.Post
                ? Json(HttpStatusCode.OK, "{\"uploadUrl\":\"https://contoso-my.sharepoint.com/personal/anna/_api/v2.0/drive/items/01FOLDER/uploadSession?guid=1\"}")
                : Json(HttpStatusCode.Created, "{\"id\":\"done\",\"name\":\"2026-0142.mkv\",\"webUrl\":\"https://contoso-my.sharepoint.com/personal/anna/Documents/Recordings/2026-0142.mkv\"}"));
        }));
        var result = await new CloudUploadService(http).UploadAsync(new(Account(CloudProvider.OneDrive), path, folder));
        Assert.Equal("done", result.Id);
        Assert.Equal(2, calls.Count);
        Assert.Equal(("POST", "https://graph.microsoft.com/v1.0/drives/b%21drive%20of%20anna/items/01FOLDER%217:/2026-0142.mkv:/createUploadSession", "Bearer access-token"), calls[0]);
        // The bytes go to the address OneDrive handed out, without the sign-in.
        Assert.Equal(("PUT", "https://contoso-my.sharepoint.com/personal/anna/_api/v2.0/drive/items/01FOLDER/uploadSession?guid=1", (string?)null), calls[1]);
    }

    [Theory] [InlineData(HttpStatusCode.Forbidden)] [InlineData(HttpStatusCode.NotFound)]
    public async Task ASharedFolderThatRefusesTheFileSaysWhoMustBeAllowedToWrite(HttpStatusCode status)
    {
        var path = CreateFile(10, "2026-0142.mkv");
        var folder = new CloudFolder("b!d", "01FOLDER", "Recordings", new Uri("https://1drv.ms/f/s!AkxYzExample"));
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return Task.FromResult(Json(status, "{\"error\":{\"code\":\"accessDenied\",\"message\":\"Access denied\"}}")); }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudUploadService(http).UploadAsync(new(Account(CloudProvider.OneDrive) with { SharedFiles = true }, path, folder)));
        Assert.StartsWith(Diga.Core.Localization.AppText.T("Core.Cloud.Upload.FolderRefused", (int)status), error.Message, StringComparison.Ordinal);
        Assert.Contains("Access denied", error.Message, StringComparison.Ordinal);
        // A refusal is an answer: it is not asked again.
        Assert.Equal(1, calls);
        // A sign-in that reaches only the account's own files is the likeliest reason, and the message says how to change it.
        var own = await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudUploadService(http).UploadAsync(new(Account(CloudProvider.OneDrive), path, folder)));
        Assert.StartsWith(Diga.Core.Localization.AppText.T("Core.Cloud.Upload.FolderRefusedOwnFiles", (int)status), own.Message, StringComparison.Ordinal);
        Assert.Contains("Access denied", own.Message, StringComparison.Ordinal);
        Assert.Equal(2, calls);
        // The top folder keeps the general message.
        var top = await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudUploadService(http).UploadAsync(new(Account(CloudProvider.OneDrive), path)));
        Assert.StartsWith(Diga.Core.Localization.AppText.T("Core.Cloud.Upload.StartFailed", (int)status), top.Message, StringComparison.Ordinal);
        // Google has no such folder, and nothing is sent to find that out.
        await Assert.ThrowsAsync<ArgumentException>(() => new CloudUploadService(http).UploadAsync(new(Account(CloudProvider.GoogleDrive), path, folder)));
        Assert.Equal(3, calls);
    }

    // What is asked for at sign-in, what Microsoft says it gave, and what the application then takes the sign-in to reach.
    [Theory]
    [InlineData(false, null, "Files.ReadWrite", false)]
    [InlineData(true, null, "Files.ReadWrite.All", true)]
    [InlineData(true, "https://graph.microsoft.com/Files.ReadWrite.All", "Files.ReadWrite.All", true)]
    [InlineData(true, "Files.ReadWrite", "Files.ReadWrite.All", false)]
    [InlineData(false, "files.readwrite Files.ReadWrite.All", "Files.ReadWrite", true)]
    [InlineData(false, "https://graph.microsoft.com/Sites.ReadWrite.All Files.ReadWrite", "Files.ReadWrite", false)]
    public async Task AFolderForUploadsMakesTheSignInAskForSharedFilesAndARenewalAsksForWhatWasGiven(bool asked, string? given, string expectedPermission, bool reachesSharedFiles)
    {
        var tokens = new MemoryTokens();
        var grants = new List<Dictionary<string, string>>();
        Task? callback = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method != HttpMethod.Post) return Json(HttpStatusCode.OK, "{\"driveType\":\"business\"}");
            grants.Add(ParseQuery(await request.Content!.ReadAsStringAsync()));
            return Json(HttpStatusCode.OK, "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":1" + (given is null ? "" : ",\"scope\":\"" + given + "\"") + "}");
        }));
        var auth = new CloudAuthService(http, tokens, uri =>
        {
            var query = ParseQuery(uri.Query.TrimStart('?'));
            Assert.Equal("offline_access https://graph.microsoft.com/" + expectedPermission, query["scope"]);
            callback = Task.Run(async () =>
            {
                using var loopback = new HttpClient();
                using var valid = await loopback.GetAsync(query["redirect_uri"] + "/?code=test-code&state=" + Uri.EscapeDataString(query["state"]));
            });
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var account = await auth.AuthenticateAsync(new OAuthClientOptions(CloudProvider.OneDrive, "test-client", SharedFiles: asked), timeout.Token);
        await callback!;
        Assert.Equal(reachesSharedFiles, account.SharedFiles);
        Assert.Equal(reachesSharedFiles, tokens.Saved!.SharedFiles);
        // Asking a renewal for more than was given would end the sign-in; asking for less would lose the folder.
        var renewed = await auth.RefreshAsync(account, timeout.Token);
        Assert.Equal(2, grants.Count);
        Assert.False(grants[0].ContainsKey("scope"));
        Assert.Equal("offline_access https://graph.microsoft.com/" + (reachesSharedFiles ? "Files.ReadWrite.All" : "Files.ReadWrite"), grants[1]["scope"]);
        Assert.Equal(reachesSharedFiles, renewed.SharedFiles);
    }

    [Fact] public async Task GoogleIsNeverAskedForSharedFiles()
    {
        var tokens = new MemoryTokens();
        Task? callback = null;
        using var http = new HttpClient(new Handler(request => Task.FromResult(request.Method == HttpMethod.Post
            ? Json(HttpStatusCode.OK, "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600,\"scope\":\"Files.ReadWrite.All\"}")
            : Json(HttpStatusCode.OK, "{}"))));
        var auth = new CloudAuthService(http, tokens, uri =>
        {
            var query = ParseQuery(uri.Query.TrimStart('?'));
            Assert.Equal("https://www.googleapis.com/auth/drive.file", query["scope"]);
            callback = Task.Run(async () =>
            {
                using var loopback = new HttpClient();
                using var valid = await loopback.GetAsync(query["redirect_uri"] + "/?code=test-code&state=" + Uri.EscapeDataString(query["state"]));
            });
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var account = await auth.AuthenticateAsync(new OAuthClientOptions(CloudProvider.GoogleDrive, "test-client", "test-secret", SharedFiles: true), timeout.Token);
        await callback!;
        Assert.False(account.SharedFiles);
    }

    [Fact] public async Task TheLinkToTheFolderForUploadsIsKeptEncryptedAndIsNotASignIn()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(_directory, "folder-link");
        var store = new CloudFolderLinkStore(directory);
        const string link = "https://contoso-my.sharepoint.com/:f:/g/personal/anna_contoso_com/EhK9aZ7xQ0?e=ab12Cd";
        Assert.Null(await store.LoadAsync(CloudProvider.OneDrive));
        await store.SaveAsync(CloudProvider.OneDrive, "  " + link + " ");
        Assert.Equal(link, await store.LoadAsync(CloudProvider.OneDrive));
        Assert.Null(await store.LoadAsync(CloudProvider.GoogleDrive));
        var file = Assert.Single(Directory.GetFiles(directory));
        var stored = await File.ReadAllBytesAsync(file);
        Assert.DoesNotContain("sharepoint", Encoding.UTF8.GetString(stored), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sharepoint", Encoding.Unicode.GetString(stored), StringComparison.OrdinalIgnoreCase);
        // Disconnect removes sign-ins. Where uploads go is a setting, and it stays.
        var signIns = new ProtectedTokenStore(directory);
        Assert.False(signIns.HasAny(CloudProvider.OneDrive));
        await signIns.DeleteAsync(CloudProvider.OneDrive, "client");
        Assert.Equal(link, await store.LoadAsync(CloudProvider.OneDrive));
        // What is not a whole https address is not saved, and a file that cannot be read counts as nothing saved.
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(CloudProvider.OneDrive, "contoso.sharepoint.com/folder"));
        Assert.Equal(link, await store.LoadAsync(CloudProvider.OneDrive));
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        Assert.Null(await store.LoadAsync(CloudProvider.OneDrive));
        // An empty link removes the file: uploads go to the top folder again.
        await store.SaveAsync(CloudProvider.OneDrive, link);
        await store.SaveAsync(CloudProvider.OneDrive, " ");
        Assert.Null(await store.LoadAsync(CloudProvider.OneDrive));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact] public async Task ProtectedStoreEncryptsTokensForCurrentWindowsUser()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new ProtectedTokenStore(_directory);
        Assert.False(store.HasAny(CloudProvider.GoogleDrive));
        await store.SaveAsync(Account(CloudProvider.GoogleDrive));
        Assert.DoesNotContain("access-token", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Directory.GetFiles(_directory).Single())));
        Assert.Equal("access-token", (await store.LoadAsync(CloudProvider.GoogleDrive, "client"))!.AccessToken);
        // A sign-in made with an ID that is no longer in use is still a saved sign-in, and only of its own service.
        Assert.Null(await store.LoadAsync(CloudProvider.GoogleDrive, "another client"));
        Assert.True(store.HasAny(CloudProvider.GoogleDrive));
        Assert.False(store.HasAny(CloudProvider.OneDrive));
        await store.DeleteAsync(CloudProvider.GoogleDrive, "another client");
        Assert.False(store.HasAny(CloudProvider.GoogleDrive));
        Assert.Null(await store.LoadAsync(CloudProvider.GoogleDrive, "client"));
    }

    [Theory] [InlineData(CloudProvider.GoogleDrive)] [InlineData(CloudProvider.OneDrive)]
    public async Task TransientFailureQueriesServerAndResumesAcknowledgedOffset(CloudProvider provider)
    {
        var path = CreateFile(CloudUploadService.ChunkSize + 17);
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            if (calls == 1)
            {
                var response = provider == CloudProvider.GoogleDrive ? Json(HttpStatusCode.OK, "{}") : Json(HttpStatusCode.OK, "{\"uploadUrl\":\"https://up.1drv.com/session\"}");
                if (provider == CloudProvider.GoogleDrive) response.Headers.Location = new("https://www.googleapis.com/upload/session");
                return Task.FromResult(response);
            }
            if (calls == 2)
            {
                var response = Json(HttpStatusCode.ServiceUnavailable, ""); response.Headers.RetryAfter = new(TimeSpan.FromMilliseconds(1)); return Task.FromResult(response);
            }
            if (calls == 3)
            {
                if (provider == CloudProvider.OneDrive) { Assert.Equal(HttpMethod.Get, request.Method); return Task.FromResult(Json(HttpStatusCode.OK, "{\"nextExpectedRanges\":[\"327680-\"]}")); }
                Assert.Equal($"bytes */{CloudUploadService.ChunkSize + 17}", request.Content!.Headers.ContentRange!.ToString());
                var response = Json((HttpStatusCode)308, ""); response.Headers.TryAddWithoutValidation("Range", "bytes=0-327679"); return Task.FromResult(response);
            }
            Assert.Equal(327680, request.Content!.Headers.ContentRange!.From);
            Assert.Equal(CloudUploadService.ChunkSize + 16, request.Content.Headers.ContentRange.To);
            return Task.FromResult(Json(HttpStatusCode.Created, "{\"id\":\"recovered\"}"));
        }));
        var result = await new CloudUploadService(http) { MinimumRetryDelay = TimeSpan.FromMilliseconds(1) }.UploadAsync(new(Account(provider), path));
        Assert.Equal("recovered", result.Id);
        Assert.Equal(4, calls);
    }
    [Fact] public async Task CancellationDuringRetryDoesNotSendMoreRequests()
    {
        var path = CreateFile(100);
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            if (++calls == 1) { var response = Json(HttpStatusCode.OK, "{}"); response.Headers.Location = new("https://www.googleapis.com/upload/session"); return Task.FromResult(response); }
            cancellation.Cancel(); return Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, ""));
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CloudUploadService(http).UploadAsync(new(Account(CloudProvider.GoogleDrive), path), cancellationToken: cancellation.Token));
        Assert.Equal(2, calls);
    }
    [Fact] public async Task PrematureCompletionIsNotReportedAsSuccess()
    {
        var path = CreateFile(CloudUploadService.ChunkSize + 17);
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            if (++calls == 1) { var response = Json(HttpStatusCode.OK, "{}"); response.Headers.Location = new("https://www.googleapis.com/upload/session"); return Task.FromResult(response); }
            return Task.FromResult(Json(HttpStatusCode.Created, "{\"id\":\"incomplete\"}"));
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new CloudUploadService(http).UploadAsync(new(Account(CloudProvider.GoogleDrive), path)));
    }

    [Fact] public void UnexpectedUploadHostIsNamedInTheError()
    {
        var error = Assert.Throws<InvalidDataException>(() => CloudUploadService.ValidateSessionUri(CloudProvider.OneDrive, "https://upload.attacker.example/session?tempauth=secret-token"));
        Assert.Contains("upload.attacker.example", error.Message);
        Assert.DoesNotContain("secret-token", error.Message);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task OneDriveSignInOffersAccountChoiceAndNamesTheDrive(bool driveAnswers)
    {
        var tokens = new MemoryTokens();
        var requests = new List<string>();
        Task? callback = null;
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.Method.Method + " " + request.RequestUri!.AbsoluteUri);
            if (request.Method == HttpMethod.Post) return Task.FromResult(Json(HttpStatusCode.OK, "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":1}"));
            Assert.Equal("Bearer access", request.Headers.Authorization!.ToString());
            return Task.FromResult(driveAnswers
                ? Json(HttpStatusCode.OK, "{\"driveType\":\"personal\",\"owner\":{\"user\":{\"displayName\":\"Test Person\\r\\nsecond line\",\"id\":\"1\"}}}")
                : Json(HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"accessDenied\",\"message\":\"no\"}}"));
        }));
        var auth = new CloudAuthService(http, tokens, uri =>
        {
            Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/authorize", uri.GetLeftPart(UriPartial.Path));
            var query = ParseQuery(uri.Query.TrimStart('?'));
            Assert.Equal("select_account", query["prompt"]);
            Assert.Matches("^http://localhost:[0-9]+$", query["redirect_uri"]);
            callback = Task.Run(async () =>
            {
                using var loopback = new HttpClient();
                using var valid = await loopback.GetAsync(query["redirect_uri"] + "/?code=test-code&state=" + Uri.EscapeDataString(query["state"]));
                Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
            });
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await auth.AuthenticateAsync(CloudProvider.OneDrive, "test-client", timeout.Token);
        await callback!;
        Assert.Equal(["POST https://login.microsoftonline.com/common/oauth2/v2.0/token", "GET https://graph.microsoft.com/v1.0/me/drive?$select=driveType,owner"], requests);
        Assert.Equal(driveAnswers ? "personal" : null, result.DriveType);
        Assert.Equal(driveAnswers ? "Test Person" : null, result.OwnerName);
        if (driveAnswers) Assert.EndsWith(" · Test Person", result.DisplayName); else Assert.Equal("OneDrive", result.DisplayName);
        Assert.Same(result, tokens.Saved);
        // A refresh keeps what sign-in learned about the drive.
        var refreshed = await auth.RefreshAsync(result, timeout.Token);
        Assert.Equal(result.DriveType, refreshed.DriveType);
        Assert.Equal(result.OwnerName, refreshed.OwnerName);
        Assert.Equal("refresh", refreshed.RefreshToken);
    }

    [Theory]
    [InlineData("error=server_error&error_description=AADSTS90033%3A+A+transient+error+has+occurred.%0D%0ATrace+ID%3A+abc", "AADSTS90033: A transient error has occurred.", true)]
    [InlineData("error=access_denied&error_description=AADSTS65004%3A+User+declined+to+consent", "AADSTS65004", false)]
    public async Task SignInCallbackErrorIsReportedWithTheProvidersReason(string callbackQuery, string reason, bool reasonShown)
    {
        Task? callback = null;
        using var http = new HttpClient(new Handler(_ => throw new Exception("No token request is expected.")));
        var auth = new CloudAuthService(http, new MemoryTokens(), uri =>
        {
            var query = ParseQuery(uri.Query.TrimStart('?'));
            callback = Task.Run(async () =>
            {
                using var loopback = new HttpClient();
                using var response = await loopback.GetAsync(query["redirect_uri"] + "?" + callbackQuery + "&state=" + Uri.EscapeDataString(query["state"]));
            });
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => auth.AuthenticateAsync(CloudProvider.OneDrive, "test-client", timeout.Token));
        await callback!;
        if (reasonShown) Assert.Contains(reason, error.Message); else Assert.DoesNotContain(reason, error.Message);
        Assert.DoesNotContain("Trace ID", error.Message);
    }

    [Fact] public async Task TokenErrorShowsOnlyTheNamedErrorFields()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(HttpStatusCode.BadRequest,
            "{\"error\":\"invalid_client\",\"error_description\":\"AADSTS7000218: The request body must contain client_assertion or client_secret.\\r\\nTrace ID: abc\",\"secret\":\"sensitive-response\"}"))));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudAuthService(http, new MemoryTokens()).RefreshAsync(Account(CloudProvider.OneDrive, true)));
        Assert.Contains("invalid_client: AADSTS7000218: The request body must contain client_assertion or client_secret.", error.Message);
        Assert.Contains("400", error.Message);
        Assert.DoesNotContain("sensitive-response", error.Message);
        Assert.DoesNotContain("Trace ID", error.Message);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task GoogleSignInOffersAccountChoiceUsesTheLoopbackAddressAndNamesTheAccount(bool aboutAnswers)
    {
        var tokens = new MemoryTokens();
        var requests = new List<string>();
        Task? callback = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            requests.Add(request.Method.Method + " " + request.RequestUri!.GetLeftPart(UriPartial.Path));
            if (request.Method == HttpMethod.Post)
            {
                var form = ParseQuery(await request.Content!.ReadAsStringAsync());
                Assert.Equal("test-secret", form["client_secret"]);
                Assert.Matches("^http://127\\.0\\.0\\.1:[0-9]+$", form["redirect_uri"]);
                return Json(HttpStatusCode.OK, "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}");
            }
            Assert.Equal("Bearer access", request.Headers.Authorization!.ToString());
            Assert.Equal("?fields=user(displayName,emailAddress)", Uri.UnescapeDataString(request.RequestUri.Query));
            return aboutAnswers ? Json(HttpStatusCode.OK, "{\"user\":{\"displayName\":\"Test Person\",\"emailAddress\":\"person@example.com\"}}") : Json(HttpStatusCode.Forbidden, "{}");
        }));
        var auth = new CloudAuthService(http, tokens, uri =>
        {
            Assert.Equal("https://accounts.google.com/o/oauth2/v2/auth", uri.GetLeftPart(UriPartial.Path));
            var query = ParseQuery(uri.Query.TrimStart('?'));
            Assert.Equal("consent select_account", query["prompt"]);
            Assert.Equal("https://www.googleapis.com/auth/drive.file", query["scope"]);
            Assert.Matches("^http://127\\.0\\.0\\.1:[0-9]+$", query["redirect_uri"]);
            Assert.DoesNotContain("test-secret", uri.AbsoluteUri);
            callback = Task.Run(async () =>
            {
                using var loopback = new HttpClient();
                using var valid = await loopback.GetAsync(query["redirect_uri"] + "/?code=test-code&state=" + Uri.EscapeDataString(query["state"]));
                Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
            });
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await auth.AuthenticateAsync(new OAuthClientOptions(CloudProvider.GoogleDrive, "test-client", "test-secret"), timeout.Token);
        await callback!;
        Assert.Equal(["POST https://oauth2.googleapis.com/token", "GET https://www.googleapis.com/drive/v3/about"], requests);
        Assert.Equal(aboutAnswers ? "Google Drive · person@example.com" : "Google Drive", result.DisplayName);
        // A Drive that refuses the first question (the API is not switched on in the user's project) is said at once, not at the first upload.
        Assert.Equal(!aboutAnswers, result.Notice is not null);
        Assert.Null(result.DriveType);
        Assert.Same(result, tokens.Saved);
    }

    [Theory]
    [InlineData("recording.mkv", "video/x-matroska")]
    [InlineData("recording.MP4", "video/mp4")]
    [InlineData("recording.mpg", "video/mpeg")]
    [InlineData("recording.ts", "video/mp2t")]
    [InlineData("recording.bin", "application/octet-stream")]
    public async Task GoogleUploadDeclaresTheRecordingsMediaType(string fileName, string mediaType)
    {
        var path = CreateFile(10, fileName);
        string? declared = null;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                declared = request.Headers.GetValues("X-Upload-Content-Type").Single();
                var response = Json(HttpStatusCode.OK, "{}"); response.Headers.Location = new("https://www.googleapis.com/upload/session"); return Task.FromResult(response);
            }
            return Task.FromResult(Json(HttpStatusCode.Created, "{\"id\":\"done\"}"));
        }));
        await new CloudUploadService(http).UploadAsync(new(Account(CloudProvider.GoogleDrive), path));
        Assert.Equal(mediaType, declared);
    }

    [Fact] public async Task TraceTextThatFollowsTheReasonOnTheSameLineIsDropped()
    {
        // Layout of the answer Microsoft's token endpoint gave on 2026-10-02 (artifacts/onedrive-052-results/microsoft-precheck.txt):
        // spaces, not line breaks, before the trace text. The trace values here are made up.
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(HttpStatusCode.BadRequest,
            "{\"error\":\"invalid_grant\",\"error_description\":\"AADSTS70000: The provided value for the 'code' parameter is not valid. Trace ID: f9e0561b Correlation ID: 8020ce90 Timestamp: 2026-10-02 15:55:15Z\",\"error_codes\":[70000]}"))));
        var error = await Assert.ThrowsAsync<CloudSignInExpiredException>(() => new CloudAuthService(http, new MemoryTokens()).RefreshAsync(Account(CloudProvider.OneDrive, true)));
        Assert.EndsWith("invalid_grant: AADSTS70000: The provided value for the 'code' parameter is not valid.", error.Message);
    }

    [Fact] public async Task RevokedSignInIsNotReportedAsARegistrationFault()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\",\"error_description\":\"AADSTS70000: The refresh token has expired.\"}"))));
        var error = await Assert.ThrowsAsync<CloudSignInExpiredException>(() => new CloudAuthService(http, new MemoryTokens()).RefreshAsync(Account(CloudProvider.OneDrive, true)));
        Assert.Equal(CloudProvider.OneDrive, error.Provider);
        Assert.Contains("AADSTS70000", error.Message);
        Assert.DoesNotContain("400", error.Message);
    }

    [Fact] public async Task AGoogleSignInThatEndedSaysWhyThatHappensEverySevenDays()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\",\"error_description\":\"Token has been expired or revoked.\"}"))));
        var error = await Assert.ThrowsAsync<CloudSignInExpiredException>(() => new CloudAuthService(http, new MemoryTokens()).RefreshAsync(Account(CloudProvider.GoogleDrive, true)));
        Assert.Equal(CloudProvider.GoogleDrive, error.Provider);
        Assert.StartsWith(Diga.Core.Localization.AppText.T("Core.Cloud.Auth.ExpiredGoogle"), error.Message);
        Assert.Contains("Token has been expired or revoked.", error.Message);
    }

    [Fact] public async Task UploadStartFailureShowsTheProvidersReason()
    {
        var path = CreateFile(100);
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"accessDenied\",\"message\":\"Access denied for this drive\",\"innerError\":{\"request-id\":\"hidden-id\"}}}"))));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudUploadService(http).UploadAsync(new(Account(CloudProvider.OneDrive), path)));
        Assert.Contains("403", error.Message);
        Assert.Contains("accessDenied: Access denied for this drive", error.Message);
        Assert.DoesNotContain("hidden-id", error.Message);
    }

    [Fact] public async Task FailedStatusQueryIsRetriedBeforeTheUploadResumes()
    {
        var path = CreateFile(100);
        var calls = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            calls.Add(request.Method.Method);
            switch (calls.Count)
            {
                case 1: return Task.FromResult(Json(HttpStatusCode.OK, "{\"uploadUrl\":\"https://my.microsoftpersonalcontent.com/personal/session\"}"));
                case 2: throw new HttpRequestException("connection lost");
                case 3: var busy = Json(HttpStatusCode.ServiceUnavailable, ""); busy.Headers.RetryAfter = new(TimeSpan.FromMilliseconds(1)); return Task.FromResult(busy);
                case 4: return Task.FromResult(Json(HttpStatusCode.OK, "{\"nextExpectedRanges\":[\"0-\"]}"));
                default: Assert.Equal(0, request.Content!.Headers.ContentRange!.From); return Task.FromResult(Json(HttpStatusCode.Created, "{\"id\":\"recovered\"}"));
            }
        }));
        var result = await new CloudUploadService(http) { MinimumRetryDelay = TimeSpan.FromMilliseconds(1) }.UploadAsync(new(Account(CloudProvider.OneDrive), path));
        Assert.Equal("recovered", result.Id);
        Assert.Equal(["POST", "PUT", "GET", "GET", "PUT"], calls);
    }

    [Fact] public async Task UploadGivesUpAfterTheRetryBudget()
    {
        var path = CreateFile(100);
        var calls = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            calls.Add(request.Method.Method);
            if (calls.Count == 1) return Task.FromResult(Json(HttpStatusCode.OK, "{\"uploadUrl\":\"https://up.1drv.com/session\"}"));
            var busy = Json(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"code\":\"serviceNotAvailable\",\"message\":\"The service is busy\"}}"); busy.Headers.RetryAfter = new(TimeSpan.FromMilliseconds(1)); return Task.FromResult(busy);
        }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new CloudUploadService(http) { MinimumRetryDelay = TimeSpan.FromMilliseconds(1) }.UploadAsync(new(Account(CloudProvider.OneDrive), path)));
        Assert.Equal(2 + CloudUploadService.MaximumRetries, calls.Count);
        Assert.All(calls.Skip(2), method => Assert.Equal("GET", method));
        // The reason for giving up is the provider's, not a bare "unavailable".
        Assert.Contains("503", error.Message);
        Assert.Contains("serviceNotAvailable: The service is busy", error.Message);
    }

    [Fact] public async Task AFullDriveIsReportedAtOnceInsteadOfBeingRetried()
    {
        var path = CreateFile(100);
        var calls = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            calls.Add(request.Method.Method);
            if (calls.Count == 1) return Task.FromResult(Json(HttpStatusCode.OK, "{\"uploadUrl\":\"https://up.1drv.com/session\"}"));
            return Task.FromResult(Json((HttpStatusCode)507, "{\"error\":{\"code\":\"quotaLimitReached\",\"message\":\"Insufficient Storage\"}}"));
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudUploadService(http).UploadAsync(new(Account(CloudProvider.OneDrive), path)));
        // Waiting does not empty a drive: one session, one refused part, and the provider's reason.
        Assert.Equal(["POST", "PUT"], calls);
        Assert.Contains("507", error.Message);
        Assert.Contains("quotaLimitReached: Insufficient Storage", error.Message);
    }

    [Fact] public async Task SignInTriesAnotherPortWhenTheFirstIsBusy()
    {
        using var busy = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        var busyPort = ((IPEndPoint)busy.LocalEndpoint).Port;
        int freePort;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { probe.Start(); freePort = ((IPEndPoint)probe.LocalEndpoint).Port; }
        var offered = new List<int>();
        Task? callback = null;
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(HttpStatusCode.OK, "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}"))));
        var auth = new CloudAuthService(http, new MemoryTokens(), uri =>
        {
            var query = ParseQuery(uri.Query.TrimStart('?'));
            Assert.Equal($"http://127.0.0.1:{freePort}", query["redirect_uri"]);
            callback = Task.Run(async () =>
            {
                using var loopback = new HttpClient();
                using var valid = await loopback.GetAsync(query["redirect_uri"] + "?code=test-code&state=" + Uri.EscapeDataString(query["state"]));
                Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
            });
        }, () => { var port = offered.Count == 0 ? busyPort : freePort; offered.Add(port); return port; });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await auth.AuthenticateAsync(CloudProvider.GoogleDrive, "test-client", timeout.Token);
        await callback!;
        Assert.Equal("access", result.AccessToken);
        Assert.Equal([busyPort, freePort], offered);
    }

    [Theory]
    [InlineData("stall")]
    [InlineData("{\"driveType\":\"personal\",\"owner\":{\"user\":{\"displayName\":\"A\\ud800\"}}}")]
    [InlineData("[1,2]")]
    [InlineData("not json")]
    public async Task UnusableDriveAnswerDoesNotFailASignInThatSucceeded(string driveAnswer)
    {
        var tokens = new MemoryTokens();
        Task? callback = null;
        using var http = new HttpClient(new CancellableHandler(async (request, cancellation) =>
        {
            if (request.Method == HttpMethod.Post) return Json(HttpStatusCode.OK, "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}");
            if (driveAnswer == "stall") await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return Json(HttpStatusCode.OK, driveAnswer);
        }));
        var auth = new CloudAuthService(http, tokens, uri =>
        {
            var query = ParseQuery(uri.Query.TrimStart('?'));
            callback = Task.Run(async () =>
            {
                using var loopback = new HttpClient();
                using var valid = await loopback.GetAsync(query["redirect_uri"] + "?code=test-code&state=" + Uri.EscapeDataString(query["state"]));
            });
        }) { DriveLookupTimeout = TimeSpan.FromMilliseconds(300) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await auth.AuthenticateAsync(CloudProvider.OneDrive, "test-client", timeout.Token);
        await callback!;
        Assert.Equal("OneDrive", result.DisplayName);
        Assert.Same(result, tokens.Saved);
        Assert.False(timeout.IsCancellationRequested);
    }

    [Fact] public async Task UnreadableErrorTextFallsBackToTheAppsOwnMessage()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_client\",\"error_description\":\"bad \\ud800\"}"))));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudAuthService(http, new MemoryTokens()).RefreshAsync(Account(CloudProvider.OneDrive, true)));
        Assert.Contains("400", error.Message);
        Assert.DoesNotContain("UTF-16", error.Message);
    }

    [Fact] public async Task MissingSessionAfterAFailureIsReportedAsLost()
    {
        var path = CreateFile(100);
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            if (calls == 1) return Task.FromResult(Json(HttpStatusCode.OK, "{\"uploadUrl\":\"https://up.1drv.com/session\"}"));
            if (calls == 2) { var busy = Json(HttpStatusCode.ServiceUnavailable, ""); busy.Headers.RetryAfter = new(TimeSpan.FromMilliseconds(1)); return Task.FromResult(busy); }
            return Task.FromResult(Json(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"itemNotFound\",\"message\":\"The upload session was not found\"}}"));
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CloudUploadService(http) { MinimumRetryDelay = TimeSpan.FromMilliseconds(1) }.UploadAsync(new(Account(CloudProvider.OneDrive), path)));
        Assert.Equal(3, calls);
        Assert.DoesNotContain("404", error.Message);
    }

    // A file is uploaded under its own name, so a test that needs a name gives the file that name, in a folder of its own.
    private string CreateFile(int size, string? name = null)
    {
        var path = name is null ? Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".mkv") : Path.Combine(Directory.CreateDirectory(Path.Combine(_directory, Guid.NewGuid().ToString("N"))).FullName, name);
        using var file = File.Create(path); file.SetLength(size); return path;
    }
    private static CloudAccount Account(CloudProvider provider, bool expired = false) => new() { Provider = provider, ClientId = "client", AccessToken = "access-token", RefreshToken = "refresh-token", ExpiresAt = DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1) };
    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static Dictionary<string, string> ParseQuery(string query) => query.Split('&').Select(part => part.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return handle(request); }
    }
    private sealed class CancellableHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }
    private sealed class MemoryTokens : ICloudTokenStore
    {
        public CloudAccount? Saved { get; private set; }
        public Task<CloudAccount?> LoadAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default) => Task.FromResult(Saved);
        public Task SaveAsync(CloudAccount account, CancellationToken cancellationToken = default) { Saved = account; return Task.CompletedTask; }
        public Task DeleteAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default) { Saved = null; return Task.CompletedTask; }
    }
}
