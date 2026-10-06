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
    /// <summary>How many times in a row an answer that asks to wait (too many requests, a server fault) is tried again.</summary>
    public const int MaximumRetries = 5;
    /// <summary>
    /// How long an upload keeps trying while nothing answers at all (no connection, a time limit). A router that restarts or a
    /// line that resynchronises takes minutes, and an upload that gave up would have to send the whole file again.
    /// </summary>
    public TimeSpan NetworkPatience { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>Shortest wait before another try; the provider's Retry-After can only lengthen it, up to 30 seconds.</summary>
    public TimeSpan MinimumRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    private readonly HttpClient _http = http ?? CloudAuthService.DefaultHttp;
    private readonly CloudAuthService _auth = auth ?? new CloudAuthService(http);

    public async Task<CloudUploadResult> UploadAsync(CloudUploadRequest request, IProgress<CloudUploadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request.Account);
        if (!Enum.IsDefined(request.Account.Provider)) throw new ArgumentOutOfRangeException(nameof(request));
        var name = Path.GetFileName(request.SourcePath);
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\', '\0']) >= 0 || name is "." or "..") throw new ArgumentException(L.T("Core.Cloud.Upload.FileNameRequired"), nameof(request));
        await using var file = new FileStream(Path.GetFullPath(request.SourcePath), FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length == 0) throw new InvalidDataException(L.T("Core.Cloud.Upload.EmptyFile"));
        var account = request.Account;
        long offset = 0;
        // Failures in a row, of either kind; answers that asked to wait; and how long nothing has answered at all.
        var (failures, refusals) = (0, 0);
        var outage = new System.Diagnostics.Stopwatch();

        // Decides whether a failure is worth another try, says so, and waits. False means the failure stands.
        async Task<bool> TryAgainAsync(Exception failure)
        {
            // Once the caller has cancelled, a failure of the request in flight is the cancellation, not a reason to report.
            cancellationToken.ThrowIfCancellationRequested();
            failures++;
            if (failure is HttpRequestException { StatusCode: not null })
            {
                // The provider answered, so the connection is there; it is asked again a few times only.
                outage.Reset();
                if (++refusals > MaximumRetries) return false;
            }
            else
            {
                if (!outage.IsRunning) outage.Start();
                if (outage.Elapsed >= NetworkPatience) return false;
            }
            progress?.Report(new(offset, file.Length, failures));
            var asked = failure.Data["RetryAfter"] as TimeSpan?;
            var seconds = Math.Clamp(asked?.TotalSeconds ?? MinimumRetryDelay.TotalSeconds * Math.Pow(2, Math.Min(failures, 16)), MinimumRetryDelay.TotalSeconds, Math.Max(30, MinimumRetryDelay.TotalSeconds));
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
            return true;
        }

        // Renewing the sign-in and opening the upload session are tried again like every later step: a batch that runs overnight
        // must not lose all its remaining files to one interruption.
        Uri session;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                account = await _auth.RefreshAsync(account, cancellationToken).ConfigureAwait(false);
                session = await CreateSessionAsync(account, name, file.Length, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (IsRetryable(ex, cancellationToken))
            {
                if (!await TryAgainAsync(ex).ConfigureAwait(false)) throw;
            }
        }
        (failures, refusals) = (0, 0);
        outage.Reset();
        var buffer = new byte[ChunkSize];
        // After a failure the provider is asked what it holds before anything more is sent.
        var resync = false;
        progress?.Report(new(0, file.Length));
        while (offset < file.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Google's upload address is used together with the sign-in, which can run out during an upload of hours.
                if (account.Provider == CloudProvider.GoogleDrive) account = await _auth.RefreshAsync(account, cancellationToken).ConfigureAwait(false);
                if (resync)
                {
                    using var held = await QuerySessionAsync(account, session, file.Length, cancellationToken).ConfigureAwait(false);
                    if (IsTransient(held.StatusCode)) throw await TransientAsync(held, cancellationToken).ConfigureAwait(false);
                    if (held.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException(L.T("Core.Cloud.Upload.SessionLost"));
                    if (held.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created && account.Provider == CloudProvider.GoogleDrive)
                    {
                        var finished = await ReadResultAsync(held, name, cancellationToken).ConfigureAwait(false);
                        progress?.Report(new(file.Length, file.Length));
                        return finished;
                    }
                    var received = await ReadOffsetAsync(account.Provider, held, cancellationToken).ConfigureAwait(false);
                    ValidateOffset(received, 0, file.Length, file.Length);
                    offset = received;
                    resync = false;
                    continue;
                }
                file.Position = offset;
                var count = (int)Math.Min(buffer.Length, file.Length - offset);
                await file.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                using var message = new HttpRequestMessage(HttpMethod.Put, session);
                if (account.Provider == CloudProvider.GoogleDrive) message.Headers.Authorization = new("Bearer", account.AccessToken);
                message.Content = new ByteArrayContent(buffer, 0, count);
                message.Content.Headers.ContentType = new("application/octet-stream");
                message.Content.Headers.ContentRange = new(offset, offset + count - 1, file.Length);
                using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
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
                // An answer that acknowledges nothing is an answer: it is asked again a few times, not for a quarter of an hour.
                if (next == offset) throw new HttpRequestException(L.T("Core.Cloud.Upload.ChunkNotAcknowledged"), null, response.StatusCode);
                offset = next;
                (failures, refusals) = (0, 0);
                outage.Reset();
                progress?.Report(new(offset, file.Length));
            }
            catch (Exception ex) when (IsRetryable(ex, cancellationToken))
            {
                if (!await TryAgainAsync(ex).ConfigureAwait(false)) throw;
                resync = true;
            }
        }
        throw new InvalidDataException(L.T("Core.Cloud.Upload.CompletionUnconfirmed"));
    }

    /// <summary>
    /// An answer that asks to wait. Its message is seen only when the tries run out, so it carries the status and the provider's
    /// reason; the wait the provider asked for travels with it.
    /// </summary>
    private static async Task<HttpRequestException> TransientAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var (_, detail) = await CloudErrorDetail.ReadAsync(response, cancellationToken).ConfigureAwait(false);
        var failure = new HttpRequestException(CloudErrorDetail.WithDetail(L.T("Core.Cloud.Upload.Unavailable", (int)response.StatusCode), detail), null, response.StatusCode);
        var wait = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (wait is not null) failure.Data["RetryAfter"] = wait.Value;
        return failure;
    }

    private static string MediaType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".mkv" => "video/x-matroska",
        ".mp4" or ".m4v" => "video/mp4",
        ".mpg" or ".mpeg" or ".vob" => "video/mpeg",
        ".ts" or ".m2ts" or ".mts" or ".tts" => "video/mp2t",
        _ => "application/octet-stream"
    };

    // A failed connection or request, or a time limit of the HTTP client; never the caller's own cancellation.
    private static bool IsRetryable(Exception ex, CancellationToken cancellationToken) => ex is HttpRequestException || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested;

    private async Task<Uri> CreateSessionAsync(CloudAccount account, string name, long length, CancellationToken cancellationToken)
    {
        string endpoint;
        object metadata;
        if (account.Provider == CloudProvider.GoogleDrive)
        {
            endpoint = "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=id,name,webViewLink";
            metadata = new Dictionary<string, object> { ["name"] = name };
        }
        else
        {
            endpoint = $"https://graph.microsoft.com/v1.0/me/drive/root:/{Uri.EscapeDataString(name)}:/createUploadSession";
            metadata = new Dictionary<string, object> { ["item"] = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = "rename", ["name"] = name } };
        }
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.Authorization = new("Bearer", account.AccessToken);
        message.Content = new StringContent(JsonSerializer.Serialize(metadata), Encoding.UTF8, "application/json");
        if (account.Provider == CloudProvider.GoogleDrive)
        {
            // Without the real type Google stores the recording as a generic binary file and offers no player for it.
            message.Headers.TryAddWithoutValidation("X-Upload-Content-Type", MediaType(name));
            message.Headers.TryAddWithoutValidation("X-Upload-Content-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        // A sign-in the provider no longer accepts is the same fault here as when the files are listed: connect again.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var (_, refusal) = await CloudErrorDetail.ReadAsync(response, cancellationToken).ConfigureAwait(false);
            await _auth.RememberEndedAsync(account, cancellationToken).ConfigureAwait(false);
            throw new CloudSignInExpiredException(account.Provider, CloudErrorDetail.WithDetail(CloudAuthService.ExpiredMessage(account.Provider), refusal));
        }
        if (IsTransient(response.StatusCode)) throw await TransientAsync(response, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "Core.Cloud.Upload.StartFailed", cancellationToken).ConfigureAwait(false);
        string? url;
        if (account.Provider == CloudProvider.GoogleDrive) url = response.Headers.Location is { IsAbsoluteUri: true } location ? location.AbsoluteUri : null;
        else
        {
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            url = Text(json.RootElement, "uploadUrl");
        }
        return ValidateSessionUri(account.Provider, url);
    }

    public static Uri ValidateSessionUri(CloudProvider provider, string? url)
    {
        if (!Enum.IsDefined(provider)) throw new ArgumentOutOfRangeException(nameof(provider));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0) throw new InvalidDataException(L.T("Core.Cloud.Upload.UnsafeSession"));
        var suffixes = provider == CloudProvider.GoogleDrive ? new[] { "googleapis.com" } : new[] { "1drv.com", "onedrive.com", "microsoftpersonalcontent.com", "sharepoint.com", "sharepoint.cn", "sharepoint.us", "storage.live.com" };
        // The connection goes to the name in its ASCII form, so that is the form compared. A name that has none, or that is
        // written with characters outside ASCII, is not an address either provider hands out.
        // Asked in this order: for some such names the ASCII form cannot even be read.
        if (!Ascii.IsValid(uri.Host)) throw new InvalidDataException(L.T("Core.Cloud.Upload.UnsafeSession"));
        var host = uri.IdnHost;
        if (host.Length == 0) throw new InvalidDataException(L.T("Core.Cloud.Upload.UnsafeSession"));
        if (!suffixes.Any(s => host.Equals(s, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + s, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException(L.T("Core.Cloud.Upload.UnexpectedHost", host));
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
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("nextExpectedRanges", out var ranges) && ranges.ValueKind == JsonValueKind.Array)
            {
                var range = ranges.EnumerateArray().Select(s => s.ValueKind == JsonValueKind.String ? s.GetString() : null).FirstOrDefault();
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
        var id = Text(root, "id");
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException(L.T("Core.Cloud.Upload.FileIdMissing"));
        return new(id, Text(root, "name") ?? fallbackName, Text(root, "webViewLink") ?? Text(root, "webUrl"));
    }

    /// <summary>A text field of an answer; null when the answer is not an object or the field is not text.</summary>
    private static string? Text(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try { return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false); }
        // What is not JSON at all (a proxy's page, a cut answer) is reported in the application's words, not the parser's.
        catch (JsonException ex) { throw new InvalidDataException(L.T("Core.Cloud.Upload.UnexpectedResponse"), ex); }
    }
    // A full drive (507) does not empty itself while the upload waits; it is reported at once with the provider's reason.
    private static bool IsTransient(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests || (int)status >= 500 && status != HttpStatusCode.InsufficientStorage;
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string messageKey, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var (_, detail) = await CloudErrorDetail.ReadAsync(response, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException(CloudErrorDetail.WithDetail(L.T(messageKey, (int)response.StatusCode), detail));
    }
}
