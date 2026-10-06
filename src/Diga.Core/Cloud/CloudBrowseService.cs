using L = Diga.Core.Localization.AppText;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Diga.Core.Cloud;

/// <summary>
/// Read-only look at what is already in the user's cloud storage: the OneDrive root, or the Google Drive files this
/// application created. It sends GET requests only and never asks for file contents.
/// </summary>
public sealed class CloudBrowseService(HttpClient? http = null, CloudAuthService? auth = null)
{
    /// <summary>A peek, not a full inventory: at most this many pages are read.</summary>
    public const int MaximumPages = 5;
    public const int MaximumRetries = 2;
    private const int MaximumBodyBytes = 4 * 1024 * 1024;
    private const int MaximumNameLength = 255;
    private const string OneDriveFirstPage = "https://graph.microsoft.com/v1.0/me/drive/root/children?$select=id,name,size,lastModifiedDateTime,file,folder,package,remoteItem,webUrl&$top=200";
    private const string GoogleFirstPage = "https://www.googleapis.com/drive/v3/files?q=trashed%20%3D%20false&orderBy=modifiedTime%20desc&pageSize=200&spaces=drive&corpora=user&fields=nextPageToken,incompleteSearch,files(id,name,size,modifiedTime,mimeType,webViewLink)";

    private readonly HttpClient _http = http ?? CloudAuthService.DefaultHttp;
    private readonly CloudAuthService _auth = auth ?? new CloudAuthService(http);

    /// <summary>Limit for one request, including reading its answer.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Limit for the whole listing, including a token refresh and every retry.</summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(90);
    /// <summary>Shortest wait before a retry; the provider's Retry-After can only lengthen it, up to 30 seconds.</summary>
    public TimeSpan MinimumRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public async Task<CloudListing> ListAsync(CloudAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!Enum.IsDefined(account.Provider)) throw new ArgumentOutOfRangeException(nameof(account));
        cancellationToken.ThrowIfCancellationRequested();
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(TotalTimeout);
        try
        {
            account = await _auth.RefreshAsync(account, total.Token).ConfigureAwait(false);
            var items = new List<CloudItem>();
            var truncated = false;
            var next = new Uri(account.Provider == CloudProvider.GoogleDrive ? GoogleFirstPage : OneDriveFirstPage);
            for (var page = 1; ; page++)
            {
                var body = await GetPageAsync(account, next, total.Token).ConfigureAwait(false);
                var following = ReadPage(account.Provider, body, items, ref truncated);
                if (following is null) break;
                // The loop follows the provider's own paging, never an item count: a page may be short or empty and still have a successor.
                if (page == MaximumPages) { truncated = true; break; }
                next = following;
            }
            // Newest first; the provider's own order is not the same for personal and work drives.
            return new(items.OrderByDescending(item => item.Modified ?? DateTimeOffset.MinValue).ToArray(), truncated);
        }
        // A limit of this service is a provider that did not answer, not the user's cancellation.
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException(L.T("Core.Cloud.Browse.Timeout"), ex); }
    }

    private const string OneDriveQuota = "https://graph.microsoft.com/v1.0/me/drive?$select=quota";
    private const string GoogleQuota = "https://www.googleapis.com/drive/v3/about?fields=storageQuota(limit,usage)";

    /// <summary>
    /// Free space in the drive in bytes, or null when the provider names no limit or its answer cannot be used. Both
    /// questions are covered by the permission the sign-in already has.
    /// </summary>
    public async Task<long?> GetFreeSpaceAsync(CloudAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!Enum.IsDefined(account.Provider)) throw new ArgumentOutOfRangeException(nameof(account));
        cancellationToken.ThrowIfCancellationRequested();
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(TotalTimeout);
        try
        {
            account = await _auth.RefreshAsync(account, total.Token).ConfigureAwait(false);
            var google = account.Provider == CloudProvider.GoogleDrive;
            var body = await GetPageAsync(account, new Uri(google ? GoogleQuota : OneDriveQuota), total.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(google ? "storageQuota" : "quota", out var quota) || quota.ValueKind != JsonValueKind.Object) return null;
            if (!google) return quota.TryGetProperty("remaining", out var remaining) && remaining.ValueKind == JsonValueKind.Number && remaining.TryGetInt64(out var free) && free >= 0 ? free : null;
            // Google gives both numbers as text, and no limit at all for an unlimited plan.
            return long.TryParse(Text(quota, "limit"), NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
                && long.TryParse(Text(quota, "usage"), NumberStyles.None, CultureInfo.InvariantCulture, out var usage) ? Math.Max(0, limit - usage) : null;
        }
        catch (JsonException) { return null; }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException(L.T("Core.Cloud.Browse.Timeout"), ex); }
    }

    /// <summary>The next-page address is requested with the sign-in attached, so it must be Microsoft Graph itself.</summary>
    public static Uri ValidateNextLink(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw new InvalidDataException(L.T("Core.Cloud.Browse.UnsafeLink"));
        if (!uri.Host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("Core.Cloud.Browse.UnexpectedHost", Ascii.IsValid(uri.Host) ? uri.IdnHost : uri.Host));
        return uri;
    }

    private async Task<byte[]> GetPageAsync(CloudAccount account, Uri address, CancellationToken cancellationToken)
    {
        for (var failures = 0; ; failures++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(RequestTimeout);
            HttpResponseMessage? response = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, address);
                request.Headers.Authorization = new("Bearer", account.AccessToken);
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
                var body = await ReadBodyAsync(response, limit.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return body ?? throw new InvalidDataException(L.T("Core.Cloud.Browse.UnexpectedResponse"));
                var detail = CloudErrorDetail.Parse(body).Detail;
                var status = (int)response.StatusCode;
                if (response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500)
                    throw new HttpRequestException(CloudErrorDetail.WithDetail(L.T("Core.Cloud.Browse.Unavailable", status), detail), null, response.StatusCode);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await _auth.RememberEndedAsync(account, cancellationToken).ConfigureAwait(false);
                    throw new CloudSignInExpiredException(account.Provider, CloudErrorDetail.WithDetail(L.T("Core.Cloud.Browse.SignInRejected"), detail));
                }
                throw new InvalidOperationException(CloudErrorDetail.WithDetail(L.T("Core.Cloud.Browse.Failed", status), detail));
            }
            catch (Exception ex) when (failures < MaximumRetries && (ex is HttpRequestException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                var retryAfter = response?.Headers.RetryAfter?.Delta;
                if (retryAfter == null && response?.Headers.RetryAfter?.Date is { } retryDate) retryAfter = retryDate - DateTimeOffset.UtcNow;
                var seconds = Math.Clamp(retryAfter?.TotalSeconds ?? MinimumRetryDelay.TotalSeconds * Math.Pow(2, failures), MinimumRetryDelay.TotalSeconds, Math.Max(30, MinimumRetryDelay.TotalSeconds));
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
            }
            finally { response?.Dispose(); }
        }
    }

    /// <summary>Reads at most <see cref="MaximumBodyBytes"/>; a longer or compressed answer is not read.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength > MaximumBodyBytes) return null;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > MaximumBodyBytes) return null;
            output.Write(buffer, 0, count);
        }
    }

    /// <summary>Adds the page's items and returns the address of the next page, or null when this was the last one.</summary>
    private static Uri? ReadPage(CloudProvider provider, byte[] body, List<CloudItem> items, ref bool truncated)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(body); }
        catch (JsonException ex) { throw new InvalidDataException(L.T("Core.Cloud.Browse.UnexpectedResponse"), ex); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException(L.T("Core.Cloud.Browse.UnexpectedResponse"));
            var google = provider == CloudProvider.GoogleDrive;
            if (root.TryGetProperty(google ? "files" : "value", out var list))
            {
                if (list.ValueKind != JsonValueKind.Array) throw new InvalidDataException(L.T("Core.Cloud.Browse.UnexpectedResponse"));
                foreach (var entry in list.EnumerateArray())
                    if ((google ? GoogleItem(entry) : OneDriveItem(entry)) is { } item) items.Add(item);
            }
            if (google)
            {
                if (root.TryGetProperty("incompleteSearch", out var incomplete) && incomplete.ValueKind == JsonValueKind.True) truncated = true;
                var token = Text(root, "nextPageToken");
                return string.IsNullOrEmpty(token) ? null : new Uri(GoogleFirstPage + "&pageToken=" + Uri.EscapeDataString(token));
            }
            var link = Text(root, "@odata.nextLink");
            return string.IsNullOrEmpty(link) ? null : ValidateNextLink(link);
        }
    }

    private static CloudItem? OneDriveItem(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object || Text(entry, "id") is not { Length: > 0 } id || CleanName(Text(entry, "name")) is not { } name) return null;
        var folder = IsObject(entry, "folder") || entry.TryGetProperty("remoteItem", out var remote) && remote.ValueKind == JsonValueKind.Object && IsObject(remote, "folder");
        var kind = folder ? CloudItemKind.Folder : IsObject(entry, "package") ? CloudItemKind.Other : CloudItemKind.File;
        long? size = kind == CloudItemKind.File && entry.TryGetProperty("size", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var bytes) && bytes >= 0 ? bytes : null;
        return new(id, name, kind, size, Time(entry, "lastModifiedDateTime"), WebLink(Text(entry, "webUrl")));
    }

    private static CloudItem? GoogleItem(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object || Text(entry, "id") is not { Length: > 0 } id || CleanName(Text(entry, "name")) is not { } name) return null;
        var type = Text(entry, "mimeType") ?? "";
        // Google's own document types have no size and are not files in the sense of an uploaded recording.
        var kind = type == "application/vnd.google-apps.folder" ? CloudItemKind.Folder
            : type.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) ? CloudItemKind.Other : CloudItemKind.File;
        long? size = kind == CloudItemKind.File && long.TryParse(Text(entry, "size"), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ? bytes : null;
        return new(id, name, kind, size, Time(entry, "modifiedTime"), WebLink(Text(entry, "webViewLink")));
    }

    private static bool IsObject(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object;

    private static string? Text(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        // GetString throws for text that is not valid UTF-16, such as a lone surrogate escape; such a value is treated as absent.
        try { return value.GetString(); }
        catch (InvalidOperationException) { return null; }
    }

    private static DateTimeOffset? Time(JsonElement parent, string name) =>
        DateTimeOffset.TryParse(Text(parent, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time) ? time : null;

    /// <summary>A link is opened in the browser, so only a plain https address is kept.</summary>
    private static Uri? WebLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 ? uri : null;

    /// <summary>Names come from the provider and are shown as they are, so characters that hide or reorder text are removed.</summary>
    private static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var text = new StringBuilder(Math.Min(name.Length, MaximumNameLength));
        foreach (var rune in name.EnumerateRunes())
        {
            if (text.Length + rune.Utf16SequenceLength > MaximumNameLength) break;
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) text.Append(' ');
            else if (category is not (UnicodeCategory.Format or UnicodeCategory.Surrogate)) text.Append(rune.ToString());
        }
        var cleaned = text.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
