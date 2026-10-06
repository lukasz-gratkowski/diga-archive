using L = Diga.Core.Localization.AppText;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Diga.Core.Cloud;

/// <summary>Starts uploads only when called. Chunks are acknowledged before advancing; transient failures query server progress.</summary>
public sealed class CloudUploadService(HttpClient? http = null, CloudAuthService? auth = null)
{
    public const int ChunkSize = 5 * 1024 * 1024; // 16 x 320 KiB and 20 x 256 KiB.
    /// <summary>Consecutive transient failures tolerated before an upload is given up; the waits between them add up to about a minute.</summary>
    public const int MaximumRetries = 5;
    private readonly HttpClient _http = http ?? CloudAuthService.DefaultHttp;
    private readonly CloudAuthService _auth = auth ?? new CloudAuthService(http);

    public async Task<CloudUploadResult> UploadAsync(CloudUploadRequest request, IProgress<CloudUploadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request.Account);
        if (!Enum.IsDefined(request.Account.Provider)) throw new ArgumentOutOfRangeException(nameof(request));
        var name = request.RemoteFileName ?? Path.GetFileName(request.SourcePath);
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\', '\0']) >= 0 || name is "." or "..") throw new ArgumentException(L.T("Core.Cloud.Upload.FileNameRequired"), nameof(request));
        await using var file = new FileStream(Path.GetFullPath(request.SourcePath), FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length == 0) throw new InvalidDataException(L.T("Core.Cloud.Upload.EmptyFile"));
        var account = await _auth.RefreshAsync(request.Account, cancellationToken).ConfigureAwait(false);
        var session = await CreateSessionAsync(account, name, request.FolderId, file.Length, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[ChunkSize];
        long offset = 0;
        var failures = 0;
        // After a transient failure the provider is asked what it holds before anything more is sent; that query shares the retry budget.
        var resync = false;
        progress?.Report(new(0, file.Length));
        while (offset < file.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpResponseMessage? response = null;
            try
            {
                if (resync)
                {
                    response = await QuerySessionAsync(account, session, file.Length, cancellationToken).ConfigureAwait(false);
                    if (IsTransient(response.StatusCode)) throw await TransientAsync(response, cancellationToken).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException(L.T("Core.Cloud.Upload.SessionLost"));
                    if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created && account.Provider == CloudProvider.GoogleDrive)
                    {
                        var result = await ReadResultAsync(response, name, cancellationToken).ConfigureAwait(false);
                        progress?.Report(new(file.Length, file.Length));
                        return result;
                    }
                    var held = await ReadOffsetAsync(account.Provider, response, cancellationToken).ConfigureAwait(false);
                    ValidateOffset(held, 0, file.Length, file.Length);
                    offset = held;
                    resync = false;
                    continue;
                }
            }
            catch (Exception ex) when (IsRetryable(ex, cancellationToken) && failures++ < MaximumRetries)
            {
                await Task.Delay(RetryDelay(response, failures), cancellationToken).ConfigureAwait(false);
                continue;
            }
            finally { response?.Dispose(); }
            response = null;
            file.Position = offset;
            var count = (int)Math.Min(buffer.Length, file.Length - offset);
            await file.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            using var message = new HttpRequestMessage(HttpMethod.Put, session);
            if (account.Provider == CloudProvider.GoogleDrive)
            {
                account = await _auth.RefreshAsync(account, cancellationToken).ConfigureAwait(false);
                message.Headers.Authorization = new("Bearer", account.AccessToken);
            }
            message.Content = new ByteArrayContent(buffer, 0, count);
            message.Content.Headers.ContentType = new("application/octet-stream");
            message.Content.Headers.ContentRange = new(offset, offset + count - 1, file.Length);
            try
            {
                response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
                if (IsTransient(response.StatusCode)) throw await TransientAsync(response, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                {
                    if (offset + count != file.Length) throw new InvalidDataException(L.T("Core.Cloud.Upload.PrematureCompletion"));
                    var result = await ReadResultAsync(response, name, cancellationToken).ConfigureAwait(false);
                    progress?.Report(new(file.Length, file.Length));
                    return result;
                }
                var next = await ReadOffsetAsync(account.Provider, response, cancellationToken).ConfigureAwait(false);
                ValidateOffset(next, offset, offset + count, file.Length);
                if (next == offset) throw new HttpRequestException(L.T("Core.Cloud.Upload.ChunkNotAcknowledged"));
                offset = next;
                failures = 0;
                progress?.Report(new(offset, file.Length));
            }
            catch (Exception ex) when (IsRetryable(ex, cancellationToken) && failures++ < MaximumRetries)
            {
                await Task.Delay(RetryDelay(response, failures), cancellationToken).ConfigureAwait(false);
                resync = true;
            }
            finally { response?.Dispose(); }
        }
        throw new InvalidDataException(L.T("Core.Cloud.Upload.CompletionUnconfirmed"));
    }

    /// <summary>The message is seen only when the retries run out, so it carries the status and the provider's reason, for example a full drive.</summary>
    private static async Task<HttpRequestException> TransientAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var (_, detail) = await CloudErrorDetail.ReadAsync(response, cancellationToken).ConfigureAwait(false);
        return new(CloudErrorDetail.WithDetail(L.T("Core.Cloud.Upload.Unavailable", (int)response.StatusCode), detail), null, response.StatusCode);
    }

    private static bool IsRetryable(Exception ex, CancellationToken cancellationToken) => ex is HttpRequestException || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested;

    private static TimeSpan RetryDelay(HttpResponseMessage? response, int failures)
    {
        var retryAfter = response?.Headers.RetryAfter?.Delta;
        if (retryAfter == null && response?.Headers.RetryAfter?.Date is { } retryDate) retryAfter = retryDate - DateTimeOffset.UtcNow;
        return TimeSpan.FromSeconds(Math.Clamp(retryAfter?.TotalSeconds ?? Math.Pow(2, failures), 1, 30));
    }

    private async Task<Uri> CreateSessionAsync(CloudAccount account, string name, string? folderId, long length, CancellationToken cancellationToken)
    {
        string endpoint;
        object metadata;
        if (account.Provider == CloudProvider.GoogleDrive)
        {
            endpoint = "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=id,name,webViewLink";
            metadata = string.IsNullOrWhiteSpace(folderId) ? new Dictionary<string, object> { ["name"] = name } : new Dictionary<string, object> { ["name"] = name, ["parents"] = new[] { folderId } };
        }
        else
        {
            var location = string.IsNullOrWhiteSpace(folderId) ? "root" : "items/" + Uri.EscapeDataString(folderId);
            endpoint = $"https://graph.microsoft.com/v1.0/me/drive/{location}:/{Uri.EscapeDataString(name)}:/createUploadSession";
            metadata = new Dictionary<string, object> { ["item"] = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = "rename", ["name"] = name } };
        }
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.Authorization = new("Bearer", account.AccessToken);
        message.Content = new StringContent(JsonSerializer.Serialize(metadata), Encoding.UTF8, "application/json");
        if (account.Provider == CloudProvider.GoogleDrive)
        {
            message.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "application/octet-stream");
            message.Headers.TryAddWithoutValidation("X-Upload-Content-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "Core.Cloud.Upload.StartFailed", cancellationToken).ConfigureAwait(false);
        string? url;
        if (account.Provider == CloudProvider.GoogleDrive) url = response.Headers.Location?.AbsoluteUri;
        else
        {
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            url = json.RootElement.GetProperty("uploadUrl").GetString();
        }
        return ValidateSessionUri(account.Provider, url);
    }

    public static Uri ValidateSessionUri(CloudProvider provider, string? url)
    {
        if (!Enum.IsDefined(provider)) throw new ArgumentOutOfRangeException(nameof(provider));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0) throw new InvalidDataException(L.T("Core.Cloud.Upload.UnsafeSession"));
        var suffixes = provider == CloudProvider.GoogleDrive ? new[] { "googleapis.com" } : new[] { "1drv.com", "onedrive.com", "microsoftpersonalcontent.com", "sharepoint.com", "sharepoint.cn", "sharepoint.us", "storage.live.com" };
        if (!suffixes.Any(s => uri.Host.Equals(s, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + s, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException(L.T("Core.Cloud.Upload.UnexpectedHost", uri.IdnHost));
        return uri;
    }

    private async Task<HttpResponseMessage> QuerySessionAsync(CloudAccount account, Uri session, long length, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(account.Provider == CloudProvider.GoogleDrive ? HttpMethod.Put : HttpMethod.Get, session);
        if (account.Provider == CloudProvider.GoogleDrive)
        {
            request.Headers.Authorization = new("Bearer", account.AccessToken);
            request.Content = new ByteArrayContent([]);
            request.Content.Headers.ContentRange = new(length);
        }
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ReadOffsetAsync(CloudProvider provider, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (provider == CloudProvider.GoogleDrive && (int)response.StatusCode == 308)
        {
            if (!response.Headers.TryGetValues("Range", out var values)) return 0;
            var range = values.FirstOrDefault();
            if (range?.StartsWith("bytes=0-", StringComparison.OrdinalIgnoreCase) == true && long.TryParse(range.AsSpan(8), NumberStyles.Integer, CultureInfo.InvariantCulture, out var last) && last >= 0 && last < long.MaxValue) return last + 1;
            throw new InvalidDataException(L.T("Core.Cloud.Upload.GoogleRangeInvalid"));
        }
        if (provider == CloudProvider.OneDrive && response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK)
        {
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("nextExpectedRanges", out var ranges))
            {
                var range = ranges.EnumerateArray().Select(s => s.GetString()).FirstOrDefault();
                if (long.TryParse(range?.Split('-')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var next) && next >= 0) return next;
            }
            throw new InvalidDataException(L.T("Core.Cloud.Upload.OneDriveRangeMissing"));
        }
        await EnsureSuccessAsync(response, "Core.Cloud.Upload.ChunkFailed", cancellationToken).ConfigureAwait(false);
        throw new InvalidDataException(L.T("Core.Cloud.Upload.UnexpectedResponse"));
    }

    private static void ValidateOffset(long next, long minimum, long maximum, long length)
    {
        if (next < minimum || next > maximum || next > length) throw new InvalidDataException(L.T("Core.Cloud.Upload.RangeInvalid"));
    }
    private static async Task<CloudUploadResult> ReadResultAsync(HttpResponseMessage response, string fallbackName, CancellationToken cancellationToken)
    {
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        var id = root.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException(L.T("Core.Cloud.Upload.FileIdMissing"));
        return new(id, root.TryGetProperty("name", out var name) ? name.GetString() ?? fallbackName : fallbackName,
            root.TryGetProperty("webViewLink", out var googleLink) ? googleLink.GetString() : root.TryGetProperty("webUrl", out var microsoftLink) ? microsoftLink.GetString() : null);
    }
    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken) => await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
    private static bool IsTransient(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests || (int)status >= 500;
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string messageKey, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var (_, detail) = await CloudErrorDetail.ReadAsync(response, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException(CloudErrorDetail.WithDetail(L.T(messageKey, (int)response.StatusCode), detail));
    }
}
