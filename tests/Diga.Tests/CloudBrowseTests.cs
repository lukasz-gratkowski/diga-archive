using System.Net;
using System.Text;
using Diga.Core.Cloud;
using Xunit;

namespace Diga.Tests;

/// <summary>The read-only cloud listing, against simulated provider answers. Nothing here contacts a provider.</summary>
public sealed class CloudBrowseTests
{
    private const string OneDriveFirst = "https://graph.microsoft.com/v1.0/me/drive/root/children?$select=id,name,size,lastModifiedDateTime,file,folder,package,remoteItem,webUrl&$top=200";
    private const string GoogleFirst = "https://www.googleapis.com/drive/v3/files?q=trashed%20%3D%20false&orderBy=modifiedTime%20desc&pageSize=200&spaces=drive&corpora=user&fields=nextPageToken,incompleteSearch,files(id,name,size,modifiedTime,mimeType,webViewLink)";

    [Fact]
    public async Task OneDriveRootIsListedWithOneGetAndTheSavedSignIn()
    {
        var calls = new List<HttpRequestMessage>();
        using var http = Client(request =>
        {
            calls.Add(request);
            return Json(HttpStatusCode.OK, """
                {"value":[
                  {"id":"1","name":"T52-A.mkv","size":2011565,"lastModifiedDateTime":"2026-10-02T15:35:00Z","file":{"mimeType":"video/x-matroska"},"webUrl":"https://onedrive.live.com/?id=1","@microsoft.graph.downloadUrl":"https://download.example/secret"},
                  {"id":"2","name":"Documents","size":123,"lastModifiedDateTime":"2026-09-01T10:00:00Z","folder":{"childCount":3},"webUrl":"https://onedrive.live.com/?id=2"},
                  {"id":"3","name":"Notebook","lastModifiedDateTime":"2026-08-01T10:00:00Z","package":{"type":"oneNote"}},
                  {"id":"4","name":"No facet.mpg","size":7,"lastModifiedDateTime":"2026-10-03T08:00:00Z"},
                  {"id":"5","name":"Shared folder","remoteItem":{"folder":{"childCount":1}}}
                ]}
                """);
        });
        var listing = await Service(http).ListAsync(Account(CloudProvider.OneDrive));
        var call = Assert.Single(calls);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal(OneDriveFirst, call.RequestUri!.OriginalString);
        Assert.Equal("Bearer access-token", call.Headers.Authorization!.ToString());
        Assert.False(listing.Truncated);
        // Newest first; entries without a time come last.
        Assert.Equal(["No facet.mpg", "T52-A.mkv", "Documents", "Notebook", "Shared folder"], listing.Items.Select(item => item.Name));
        var file = listing.Items[1];
        Assert.Equal((CloudItemKind.File, 2011565L, new Uri("https://onedrive.live.com/?id=1")), (file.Kind, file.Size, file.WebLink));
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 15, 35, 0, TimeSpan.Zero), file.Modified);
        Assert.Equal(CloudItemKind.File, listing.Items[0].Kind);
        Assert.Equal((CloudItemKind.Folder, (long?)null), (listing.Items[2].Kind, listing.Items[2].Size));
        Assert.Equal(CloudItemKind.Other, listing.Items[3].Kind);
        Assert.Equal(CloudItemKind.Folder, listing.Items[4].Kind);
    }

    [Fact]
    public async Task GoogleDriveListsTheFilesTheApplicationCanSee()
    {
        var calls = new List<HttpRequestMessage>();
        using var http = Client(request =>
        {
            calls.Add(request);
            return Json(HttpStatusCode.OK, """
                {"files":[
                  {"id":"a","name":"T52-B.mkv","size":"2011565","modifiedTime":"2026-10-02T15:36:00.000Z","mimeType":"video/x-matroska","webViewLink":"https://drive.google.com/file/d/a/view"},
                  {"id":"b","name":"Archive","modifiedTime":"2026-10-01T09:00:00.000Z","mimeType":"application/vnd.google-apps.folder"},
                  {"id":"c","name":"Notes","mimeType":"application/vnd.google-apps.document"}
                ]}
                """);
        });
        var listing = await Service(http).ListAsync(Account(CloudProvider.GoogleDrive));
        var call = Assert.Single(calls);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal(GoogleFirst, call.RequestUri!.OriginalString);
        Assert.Equal("Bearer access-token", call.Headers.Authorization!.ToString());
        Assert.Equal([CloudItemKind.File, CloudItemKind.Folder, CloudItemKind.Other], listing.Items.Select(item => item.Kind));
        Assert.Equal(2011565L, listing.Items[0].Size);
        Assert.Null(listing.Items[1].Size);
        Assert.Equal(new Uri("https://drive.google.com/file/d/a/view"), listing.Items[0].WebLink);
    }

    [Theory]
    [InlineData(CloudProvider.OneDrive, "{\"value\":[]}")]
    [InlineData(CloudProvider.GoogleDrive, "{\"files\":[]}")]
    [InlineData(CloudProvider.OneDrive, "{}")]
    [InlineData(CloudProvider.GoogleDrive, "{}")]
    public async Task EmptyStorageGivesAnEmptyListing(CloudProvider provider, string body)
    {
        using var http = Client(_ => Json(HttpStatusCode.OK, body));
        var listing = await Service(http).ListAsync(Account(provider));
        Assert.Empty(listing.Items);
        Assert.False(listing.Truncated);
    }

    [Fact]
    public async Task OneDriveNextPageAddressIsUsedWholeAndWithTheSignIn()
    {
        const string next = "https://graph.microsoft.com/v1.0/me/drive/root/children?$select=id&$skiptoken=opaque%2Btoken";
        var calls = new List<HttpRequestMessage>();
        using var http = Client(request =>
        {
            calls.Add(request);
            // A short first page still has a successor: paging follows the provider's link, not the item count.
            return Json(HttpStatusCode.OK, calls.Count == 1
                ? "{\"value\":[{\"id\":\"1\",\"name\":\"first.mkv\",\"file\":{}}],\"@odata.nextLink\":\"" + next + "\"}"
                : "{\"value\":[{\"id\":\"2\",\"name\":\"second.mkv\",\"file\":{}}]}");
        });
        var listing = await Service(http).ListAsync(Account(CloudProvider.OneDrive));
        Assert.Equal(2, calls.Count);
        Assert.Equal(next, calls[1].RequestUri!.OriginalString);
        Assert.Equal("Bearer access-token", calls[1].Headers.Authorization!.ToString());
        Assert.Equal(2, listing.Items.Count);
        Assert.False(listing.Truncated);
    }

    [Fact]
    public async Task GooglePageTokenIsEscapedAndTheQueryRepeated()
    {
        var calls = new List<HttpRequestMessage>();
        using var http = Client(request =>
        {
            calls.Add(request);
            return Json(HttpStatusCode.OK, calls.Count == 1 ? "{\"files\":[{\"id\":\"a\",\"name\":\"a.mkv\"}],\"nextPageToken\":\"t+o/k=n\"}" : "{\"files\":[{\"id\":\"b\",\"name\":\"b.mkv\"}]}");
        });
        var listing = await Service(http).ListAsync(Account(CloudProvider.GoogleDrive));
        Assert.Equal(2, calls.Count);
        Assert.Equal(GoogleFirst + "&pageToken=t%2Bo%2Fk%3Dn", calls[1].RequestUri!.OriginalString);
        Assert.Equal(2, listing.Items.Count);
    }

    [Theory]
    [InlineData("http://graph.microsoft.com/v1.0/next?$skiptoken=secret", null)]
    [InlineData("https://graph.microsoft.com:444/v1.0/next?$skiptoken=secret", null)]
    [InlineData("https://user@graph.microsoft.com/v1.0/next?$skiptoken=secret", null)]
    [InlineData("/v1.0/me/drive/root/children?$skiptoken=secret", null)]
    [InlineData("https://graph.microsoft.com.attacker.example/v1.0/next?$skiptoken=secret", "graph.microsoft.com.attacker.example")]
    [InlineData("https://evilgraph.microsoft.com/v1.0/next?$skiptoken=secret", "evilgraph.microsoft.com")]
    [InlineData("https://tenant.sharepoint.com/_api/next?$skiptoken=secret", "tenant.sharepoint.com")]
    public async Task NextPageOnAnotherServerIsNeverRequested(string nextLink, string? namedHost)
    {
        var calls = 0;
        using var http = Client(_ => { calls++; return Json(HttpStatusCode.OK, "{\"value\":[],\"@odata.nextLink\":\"" + nextLink + "\"}"); });
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Service(http).ListAsync(Account(CloudProvider.OneDrive)));
        Assert.Equal(1, calls);
        Assert.DoesNotContain("secret", error.Message);
        if (namedHost is not null) Assert.Contains(namedHost, error.Message);
    }

    [Fact]
    public async Task EndlessPagingStopsAtTheLimitAndSaysSo()
    {
        var calls = 0;
        using var http = Client(_ =>
        {
            calls++;
            return Json(HttpStatusCode.OK, "{\"value\":[{\"id\":\"" + calls + "\",\"name\":\"file " + calls + ".mkv\",\"file\":{}}],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/next?page=" + calls + "\"}");
        });
        var listing = await Service(http).ListAsync(Account(CloudProvider.OneDrive));
        Assert.Equal(CloudBrowseService.MaximumPages, calls);
        Assert.Equal(CloudBrowseService.MaximumPages, listing.Items.Count);
        Assert.True(listing.Truncated);
    }

    [Fact]
    public async Task IncompleteGoogleSearchIsReportedAsTruncated()
    {
        using var http = Client(_ => Json(HttpStatusCode.OK, "{\"incompleteSearch\":true,\"files\":[{\"id\":\"a\",\"name\":\"a.mkv\"}]}"));
        Assert.True((await Service(http).ListAsync(Account(CloudProvider.GoogleDrive))).Truncated);
    }

    [Fact]
    public async Task RefusalShowsTheProvidersReasonAndNothingElseFromTheAnswer()
    {
        using var http = Client(_ => Json(HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"accessDenied\",\"message\":\"Access denied for this drive\",\"innerError\":{\"request-id\":\"hidden-id\"}}}"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(http).ListAsync(Account(CloudProvider.OneDrive)));
        Assert.Contains("403", error.Message);
        Assert.Contains("accessDenied: Access denied for this drive", error.Message);
        Assert.DoesNotContain("hidden-id", error.Message);
    }

    [Fact]
    public async Task GoogleRefusalShowsGooglesMessage()
    {
        using var http = Client(_ => Json(HttpStatusCode.Forbidden, "{\"error\":{\"code\":403,\"message\":\"The user has exceeded their Drive storage quota\",\"errors\":[{\"reason\":\"storageQuotaExceeded\"}]}}"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(http).ListAsync(Account(CloudProvider.GoogleDrive)));
        Assert.Contains("The user has exceeded their Drive storage quota", error.Message);
    }

    [Fact]
    public async Task RejectedSignInIsNotRetried()
    {
        var calls = 0;
        using var http = Client(_ => { calls++; return Json(HttpStatusCode.Unauthorized, "{\"error\":{\"code\":\"InvalidAuthenticationToken\",\"message\":\"The token could not be read.\"}}"); });
        // The caller can tell a sign-in that ended from any other failure, and send the user to connect again.
        var error = await Assert.ThrowsAsync<CloudSignInExpiredException>(() => Service(http).ListAsync(Account(CloudProvider.OneDrive)));
        Assert.Equal(CloudProvider.OneDrive, error.Provider);
        Assert.Equal(1, calls);
        Assert.Contains("InvalidAuthenticationToken", error.Message);
        Assert.DoesNotContain("401", error.Message);
    }

    [Fact]
    public async Task ExpiredSignInIsRefreshedSavedAndUsed()
    {
        var tokens = new MemoryTokens();
        var calls = new List<string>();
        using var http = Client(request =>
        {
            calls.Add(request.Method.Method + " " + request.RequestUri!.Host + " " + request.Headers.Authorization);
            return request.Method == HttpMethod.Post
                ? Json(HttpStatusCode.OK, "{\"access_token\":\"new-access\",\"expires_in\":3600}")
                : Json(HttpStatusCode.OK, "{\"value\":[]}");
        });
        await new CloudBrowseService(http, new CloudAuthService(http, tokens)).ListAsync(Account(CloudProvider.OneDrive, expired: true));
        Assert.Equal(["POST login.microsoftonline.com ", "GET graph.microsoft.com Bearer new-access"], calls);
        Assert.Equal("new-access", tokens.Saved!.AccessToken);
        Assert.Equal("refresh-token", tokens.Saved.RefreshToken);
    }

    [Fact]
    public async Task RevokedSignInSendsNoListRequest()
    {
        var calls = new List<string>();
        using var http = Client(request =>
        {
            calls.Add(request.Method.Method);
            return Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\",\"error_description\":\"AADSTS70000: The refresh token has expired.\"}");
        });
        var error = await Assert.ThrowsAsync<CloudSignInExpiredException>(() => new CloudBrowseService(http, new CloudAuthService(http, new MemoryTokens())).ListAsync(Account(CloudProvider.OneDrive, expired: true)));
        Assert.Equal(["POST"], calls);
        Assert.Contains("AADSTS70000", error.Message);
    }

    [Fact]
    public async Task ThrottledRequestIsRetriedAfterTheProvidersDelay()
    {
        var calls = 0;
        using var http = Client(_ =>
        {
            if (++calls > 1) return Json(HttpStatusCode.OK, "{\"value\":[{\"id\":\"1\",\"name\":\"a.mkv\",\"file\":{}}]}");
            var busy = Json(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"activityLimitReached\",\"message\":\"Slow down\"}}");
            busy.Headers.RetryAfter = new(TimeSpan.FromMilliseconds(1));
            return busy;
        });
        var listing = await Service(http).ListAsync(Account(CloudProvider.OneDrive));
        Assert.Equal(2, calls);
        Assert.Single(listing.Items);
    }

    [Fact]
    public async Task PersistentServerErrorStopsAfterTheRetryLimitWithTheProvidersReason()
    {
        var calls = 0;
        using var http = Client(_ => { calls++; return Json(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"code\":\"serviceNotAvailable\",\"message\":\"Try later\"}}"); });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Service(http).ListAsync(Account(CloudProvider.OneDrive)));
        Assert.Equal(CloudBrowseService.MaximumRetries + 1, calls);
        Assert.Contains("503", error.Message);
        Assert.Contains("serviceNotAvailable: Try later", error.Message);
    }

    [Fact]
    public async Task ProviderThatNeverAnswersIsReportedAsATimeoutNotACancellation()
    {
        var calls = 0;
        using var http = new HttpClient(new CancellableHandler(async (_, cancellation) =>
        {
            calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            throw new InvalidOperationException("unreachable");
        }));
        var service = new CloudBrowseService(http, new CloudAuthService(http, new MemoryTokens()))
            { RequestTimeout = TimeSpan.FromMilliseconds(100), MinimumRetryDelay = TimeSpan.FromMilliseconds(1) };
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<TimeoutException>(() => service.ListAsync(Account(CloudProvider.OneDrive), caller.Token));
        Assert.Equal(CloudBrowseService.MaximumRetries + 1, calls);
        Assert.False(caller.IsCancellationRequested);
    }

    [Fact]
    public async Task TotalTimeLimitCoversAllRetries()
    {
        using var http = new HttpClient(new CancellableHandler(async (_, cancellation) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            throw new InvalidOperationException("unreachable");
        }));
        var service = new CloudBrowseService(http, new CloudAuthService(http, new MemoryTokens()))
            { RequestTimeout = TimeSpan.FromSeconds(20), TotalTimeout = TimeSpan.FromMilliseconds(200), MinimumRetryDelay = TimeSpan.FromMilliseconds(1) };
        var started = DateTime.UtcNow;
        await Assert.ThrowsAsync<TimeoutException>(() => service.ListAsync(Account(CloudProvider.OneDrive)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task CallersCancellationIsACancellationAndSendsNothingMore()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var http = Client(_ => throw new Exception("Network must not be used."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(http).ListAsync(Account(CloudProvider.OneDrive), cancellation.Token));

        using var during = new CancellationTokenSource();
        var calls = 0;
        using var busy = Client(_ => { calls++; during.Cancel(); return Json(HttpStatusCode.ServiceUnavailable, ""); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(busy).ListAsync(Account(CloudProvider.OneDrive), during.Token));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"value\":\"x\"}")]
    [InlineData("")]
    public async Task UnreadableListIsAnErrorNotACrash(string body)
    {
        using var http = Client(_ => Json(HttpStatusCode.OK, body));
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(http).ListAsync(Account(CloudProvider.OneDrive)));
    }

    [Fact]
    public async Task OversizedAnswerIsNotRead()
    {
        using var http = Client(_ => Json(HttpStatusCode.OK, "{\"value\":[],\"padding\":\"" + new string('x', 4 * 1024 * 1024) + "\"}"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(http).ListAsync(Account(CloudProvider.OneDrive)));
    }

    [Fact]
    public async Task OddEntriesAreSkippedOrCleanedWithoutFailingTheListing()
    {
        using var http = Client(_ => Json(HttpStatusCode.OK, """
            {"value":[
              {"id":"1","name":"lone \ud800 surrogate.mkv","file":{}},
              {"id":2,"name":"numeric id.mkv","file":{}},
              {"name":"no id.mkv","file":{}},
              "not an object",
              {"id":"5","name":"   ","file":{}},
              {"id":"6","name":"gpj.‮cod\ttab\r\nline.mkv","size":-5,"lastModifiedDateTime":12,"file":{},"webUrl":"javascript:alert(1)"},
              {"id":"7","name":"plain http.mkv","size":"9","lastModifiedDateTime":"not a date","file":{},"webUrl":"http://example.com/file"},
              {"id":"8","name":"user info.mkv","file":{},"webUrl":"https://user@example.com/file"}
            ]}
            """));
        var listing = await Service(http).ListAsync(Account(CloudProvider.OneDrive));
        Assert.Equal(["6", "7", "8"], listing.Items.Select(item => item.Id).Order());
        var cleaned = listing.Items.Single(item => item.Id == "6");
        Assert.Equal("gpj.cod tab  line.mkv", cleaned.Name);
        Assert.Null(cleaned.Size);
        Assert.Null(cleaned.Modified);
        Assert.All(listing.Items, item => Assert.Null(item.WebLink));
        Assert.Null(listing.Items.Single(item => item.Id == "7").Size);
    }

    [Fact]
    public async Task OnlyGetRequestsToTheProvidersApiAreSent()
    {
        foreach (var provider in Enum.GetValues<CloudProvider>())
        {
            var calls = new List<HttpRequestMessage>();
            using var http = Client(request => { calls.Add(request); return Json(HttpStatusCode.OK, "{}"); });
            await Service(http).ListAsync(Account(provider));
            Assert.All(calls, call =>
            {
                Assert.Equal(HttpMethod.Get, call.Method);
                Assert.Null(call.Content);
                Assert.Equal(provider == CloudProvider.OneDrive ? "graph.microsoft.com" : "www.googleapis.com", call.RequestUri!.Host);
            });
        }
    }

    private const string SharePointLink = "https://contoso-my.sharepoint.com/:f:/g/personal/anna_contoso_com/EhK9aZ7xQ0?e=ab12Cd";
    private const string SharePointToken = "u!aHR0cHM6Ly9jb250b3NvLW15LnNoYXJlcG9pbnQuY29tLzpmOi9nL3BlcnNvbmFsL2FubmFfY29udG9zb19jb20vRWhLOWFaN3hRMD9lPWFiMTJDZA";

    [Theory]
    [InlineData("https://1drv.ms/f/s!AkxYzExample", true)]
    [InlineData("  " + SharePointLink + "  ", true)]
    [InlineData("https://onedrive.live.com/redir?resid=1231244193912!12&authKey=1201919!12921!1", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("contoso-my.sharepoint.com/:f:/g/personal/x", false)]
    [InlineData("http://1drv.ms/f/s!AkxYz", false)]
    [InlineData("https://user:secret@1drv.ms/f/s!AkxYz", false)]
    [InlineData("https://1drv.ms:8443/f/s!AkxYz", false)]
    [InlineData("file:///C:/Users/someone/Videos", false)]
    public void OnlyAWholeHttpsAddressCountsAsAFolderLink(string text, bool accepted)
    {
        Assert.Equal(accepted, CloudBrowseService.TryParseFolderLink(text, out var link));
        Assert.Equal(accepted, link is not null);
        Assert.False(CloudBrowseService.TryParseFolderLink("https://1drv.ms/f/" + new string('a', 2048), out _));
    }

    [Fact]
    public void ASharingLinkIsEncodedAsMicrosoftDescribes()
    {
        // The example of Microsoft's own page on shared items, and a SharePoint link.
        Assert.Equal("u!aHR0cHM6Ly9vbmVkcml2ZS5saXZlLmNvbS9yZWRpcj9yZXNpZD0xMjMxMjQ0MTkzOTEyITEyJmF1dGhLZXk9MTIwMTkxOSExMjkyMSEx",
            CloudBrowseService.ShareToken(new Uri("https://onedrive.live.com/redir?resid=1231244193912!12&authKey=1201919!12921!1")));
        Assert.Equal(SharePointToken, CloudBrowseService.ShareToken(new Uri(SharePointLink)));
    }

    [Fact]
    public async Task AFolderLinkIsLookedUpWithOneGetThatAlsoAcceptsTheLink()
    {
        var calls = new List<HttpRequestMessage>();
        using var http = Client(request =>
        {
            calls.Add(request);
            return Json(HttpStatusCode.OK, """{"id":"01FOLDER","name":"Recordings 2026","folder":{"childCount":4},"parentReference":{"driveId":"b!drive-of-anna","id":"01ROOT"}}""");
        });
        var folder = await Service(http).ResolveFolderAsync(Account(CloudProvider.OneDrive), new Uri(SharePointLink));
        var call = Assert.Single(calls);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal("https://graph.microsoft.com/v1.0/shares/" + SharePointToken + "/driveItem?$select=id,name,folder,file,package,parentReference,remoteItem", call.RequestUri!.OriginalString);
        Assert.Equal("Bearer access-token", call.Headers.Authorization!.ToString());
        Assert.Equal("redeemSharingLink", Assert.Single(call.Headers.GetValues("Prefer")));
        Assert.Null(call.Content);
        Assert.Equal(("b!drive-of-anna", "01FOLDER", "Recordings 2026", new Uri(SharePointLink)), (folder.DriveId, folder.ItemId, folder.Name, folder.Link));
    }

    [Fact]
    public async Task AFolderSharedByAnotherAccountIsTakenFromThePointerToIt()
    {
        using var http = Client(_ => Json(HttpStatusCode.OK, """
            {"id":"LOCAL!1","name":"Shared with me","remoteItem":{"id":"REMOTE!77","name":"Family films","folder":{"childCount":2},"parentReference":{"driveId":"abcdef0123456789"}}}
            """));
        var folder = await Service(http).ResolveFolderAsync(Account(CloudProvider.OneDrive), new Uri("https://1drv.ms/f/s!AkxYzExample"));
        Assert.Equal(("abcdef0123456789", "REMOTE!77", "Family films"), (folder.DriveId, folder.ItemId, folder.Name));
    }

    [Theory]
    // A file, not a folder.
    [InlineData("""{"id":"01FILE","name":"film.mkv","file":{"mimeType":"video/x-matroska"},"parentReference":{"driveId":"b!d"}}""", "Core.Cloud.Folder.NotAFolder")]
    // A folder whose answer does not say where it is, or says it in a form that cannot be part of an address.
    [InlineData("""{"id":"01FOLDER","name":"Recordings","folder":{}}""", "Core.Cloud.Browse.UnexpectedResponse")]
    [InlineData("""{"id":"01/../x","name":"Recordings","folder":{},"parentReference":{"driveId":"b!d"}}""", "Core.Cloud.Browse.UnexpectedResponse")]
    [InlineData("""{"id":"01FOLDER","name":"Recordings","folder":{},"parentReference":{"driveId":"b!d?x=1"}}""", "Core.Cloud.Browse.UnexpectedResponse")]
    [InlineData("""["not","an","object"]""", "Core.Cloud.Browse.UnexpectedResponse")]
    [InlineData("<html>a proxy's page</html>", "Core.Cloud.Browse.UnexpectedResponse")]
    public async Task AnAnswerThatIsNotAUsableFolderIsRefusedInTheApplicationsWords(string answer, string messageKey)
    {
        using var http = Client(_ => Json(HttpStatusCode.OK, answer));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Service(http).ResolveFolderAsync(Account(CloudProvider.OneDrive), new Uri(SharePointLink)));
        Assert.Equal(Diga.Core.Localization.AppText.T(messageKey), error.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "accessDenied", "The sharing link no longer exists, or you do not have permission to access it.")]
    [InlineData(HttpStatusCode.NotFound, "itemNotFound", "The resource could not be found.")]
    [InlineData(HttpStatusCode.BadRequest, "invalidRequest", "The sharing link is malformed.")]
    public async Task ALinkOneDriveDoesNotOpenIsReportedWithItsReason(HttpStatusCode status, string code, string reason)
    {
        var calls = 0;
        using var http = Client(_ => { calls++; return Json(status, "{\"error\":{\"code\":\"" + code + "\",\"message\":\"" + reason + "\"}}"); });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(http).ResolveFolderAsync(Account(CloudProvider.OneDrive) with { SharedFiles = true }, new Uri(SharePointLink)));
        Assert.StartsWith(Diga.Core.Localization.AppText.T("Core.Cloud.Folder.Failed", (int)status), error.Message, StringComparison.Ordinal);
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
        Assert.IsNotType<CloudSignInExpiredException>(error);
        // A refusal is an answer; it is not asked again.
        Assert.Equal(1, calls);
        // A sign-in that reaches only the account's own files is told how to get the wider one.
        var own = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(http).ResolveFolderAsync(Account(CloudProvider.OneDrive), new Uri(SharePointLink)));
        Assert.StartsWith(Diga.Core.Localization.AppText.T("Core.Cloud.Folder.FailedOwnFiles", (int)status), own.Message, StringComparison.Ordinal);
        Assert.Contains(reason, own.Message, StringComparison.Ordinal);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task AFolderIsOnlyForOneDriveAndASignInThatEndedIsSaidAsThat()
    {
        using var none = Client(_ => throw new InvalidOperationException("No request is expected."));
        await Assert.ThrowsAsync<ArgumentException>(() => Service(none).ResolveFolderAsync(Account(CloudProvider.GoogleDrive), new Uri(SharePointLink)));
        var shared = new CloudFolder("b!d", "01FOLDER", "Recordings", new Uri(SharePointLink));
        await Assert.ThrowsAsync<ArgumentException>(() => Service(none).ListAsync(Account(CloudProvider.GoogleDrive), shared));
        await Assert.ThrowsAsync<ArgumentException>(() => Service(none).GetFreeSpaceAsync(Account(CloudProvider.GoogleDrive), shared));
        using var refused = Client(_ => Json(HttpStatusCode.Unauthorized, """{"error":{"code":"InvalidAuthenticationToken","message":"Access token has expired."}}"""));
        await Assert.ThrowsAsync<CloudSignInExpiredException>(() => Service(refused).ResolveFolderAsync(Account(CloudProvider.OneDrive), new Uri(SharePointLink)));
    }

    [Fact]
    public async Task TheFolderForUploadsIsListedAndAskedForItsRoomByItsDriveAndItsId()
    {
        var folder = new CloudFolder("b!drive of anna", "01FOLDER!7", "Recordings", new Uri(SharePointLink));
        var calls = new List<string>();
        using var http = Client(request =>
        {
            calls.Add(request.RequestUri!.OriginalString);
            return request.RequestUri.AbsolutePath.EndsWith("/children", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, """{"value":[{"id":"9","name":"2026-0142.mkv","size":5,"file":{},"webUrl":"https://contoso-my.sharepoint.com/personal/anna/Documents/Recordings/2026-0142.mkv"}]}""")
                : Json(HttpStatusCode.OK, """{"quota":{"remaining":123456}}""");
        });
        var listing = await Service(http).ListAsync(Account(CloudProvider.OneDrive), folder);
        Assert.Equal("2026-0142.mkv", Assert.Single(listing.Items).Name);
        Assert.Equal(123456, await Service(http).GetFreeSpaceAsync(Account(CloudProvider.OneDrive), folder));
        Assert.Equal(
        [
            "https://graph.microsoft.com/v1.0/drives/b%21drive%20of%20anna/items/01FOLDER%217/children?$select=id,name,size,lastModifiedDateTime,file,folder,package,remoteItem,webUrl&$top=200",
            "https://graph.microsoft.com/v1.0/drives/b%21drive%20of%20anna?$select=quota"
        ], calls);
        // Another account's drive may take files without saying how much room it has; that is not a reason to stop.
        using var silent = Client(_ => Json(HttpStatusCode.Forbidden, """{"error":{"code":"accessDenied","message":"Access denied"}}"""));
        Assert.Null(await Service(silent).GetFreeSpaceAsync(Account(CloudProvider.OneDrive), folder));
        // The account's own drive is asked as before when no folder is set.
        calls.Clear();
        await Service(http).GetFreeSpaceAsync(Account(CloudProvider.OneDrive), null);
        Assert.Equal(["https://graph.microsoft.com/v1.0/me/drive?$select=quota"], calls);
    }

    private static CloudBrowseService Service(HttpClient http) =>
        new(http, new CloudAuthService(http, new MemoryTokens())) { MinimumRetryDelay = TimeSpan.FromMilliseconds(1) };
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> handle) => new(new Handler(request => Task.FromResult(handle(request))));
    private static CloudAccount Account(CloudProvider provider, bool expired = false) => new() { Provider = provider, ClientId = "client", AccessToken = "access-token", RefreshToken = "refresh-token", ExpiresAt = DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1) };
    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
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
