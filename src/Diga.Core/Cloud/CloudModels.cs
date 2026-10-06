using L = Diga.Core.Localization.AppText;
using System.Text.Json.Serialization;

namespace Diga.Core.Cloud;

public enum CloudProvider { GoogleDrive, OneDrive }
public sealed record OAuthClientOptions(CloudProvider Provider, string ClientId, string? ClientSecret = null)
{
    public override string ToString() => $"{Provider} ({ClientId})";
}

public sealed record CloudAccount
{
    public CloudProvider Provider { get; init; }
    public string ClientId { get; init; } = "";
    public string AccessToken { get; init; } = "";
    public string? RefreshToken { get; init; }
    // Stored only with this account in the DPAPI-protected token store, never normal settings.
    public string? ClientSecret { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    // What the provider reported at sign-in, so the app can show which drive uploads go to. Either may be missing.
    public string? DriveType { get; init; }
    public string? OwnerName { get; init; }
    /// <summary>The provider refused this sign-in (it expired or was withdrawn). Remembered so that the next start does not call it connected.</summary>
    public bool SignInEnded { get; init; }
    /// <summary>Something the user should know about a sign-in that succeeded, such as a service that is not switched on yet. Not saved.</summary>
    [JsonIgnore] public string? Notice { get; init; }
    /// <summary>How the application names the connected drive. Worked out each time, in the language in use; never stored.</summary>
    [JsonIgnore] public string DisplayName
    {
        get
        {
            if (Provider == CloudProvider.GoogleDrive) return string.IsNullOrWhiteSpace(OwnerName) ? L.T("Core.Cloud.Account.Google") : L.T("Core.Cloud.Account.Google") + " · " + OwnerName;
            var drive = DriveType switch
            {
                "personal" => L.T("Core.Cloud.Account.Personal"),
                "business" or "documentLibrary" => L.T("Core.Cloud.Account.Business"),
                _ => "OneDrive"
            };
            return string.IsNullOrWhiteSpace(OwnerName) ? drive : drive + " · " + OwnerName;
        }
    }
    // A record would otherwise print every property, the tokens included.
    public override string ToString() => DisplayName;
}

/// <summary>The saved sign-in is no longer accepted by the provider: it expired, or the user or the provider withdrew it. Signing in again cures it.</summary>
public sealed class CloudSignInExpiredException(CloudProvider provider, string message) : InvalidOperationException(message)
{
    public CloudProvider Provider { get; } = provider;
}

/// <summary>One file to upload. It goes to the top folder of the drive under its own name; the service adds a number when the name is taken.</summary>
public sealed record CloudUploadRequest(CloudAccount Account, string SourcePath);
public sealed record CloudUploadResult(string Id, string Name, string? WebUrl);
/// <summary><paramref name="Retry"/> is above zero while the upload waits to try again after a failure: the number of failures in a row.</summary>
public sealed record CloudUploadProgress(long BytesUploaded, long TotalBytes, int Retry = 0)
{
    public double Fraction => TotalBytes == 0 ? 0 : (double)BytesUploaded / TotalBytes;
}

public enum CloudItemKind { File, Folder, Other }
/// <summary>One entry of a cloud listing. The web link, when present, is an https address that opens the entry in a browser.</summary>
public sealed record CloudItem(string Id, string Name, CloudItemKind Kind, long? Size, DateTimeOffset? Modified, Uri? WebLink);
/// <summary><see cref="Truncated"/> is true when the provider had more entries than a peek reads.</summary>
public sealed record CloudListing(IReadOnlyList<CloudItem> Items, bool Truncated);

public interface ICloudTokenStore
{
    Task<CloudAccount?> LoadAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default);
    Task SaveAsync(CloudAccount account, CancellationToken cancellationToken = default);
    Task DeleteAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default);
}
