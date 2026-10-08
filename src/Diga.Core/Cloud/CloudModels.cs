using L = Diga.Core.Localization.AppText;
using System.Text.Json.Serialization;

namespace Diga.Core.Cloud;

public enum CloudProvider { GoogleDrive, OneDrive }
/// <summary>
/// <paramref name="SharedFiles"/> makes a OneDrive sign-in ask for the files other people shared with the account as well as
/// the account's own. Microsoft describes the narrower permission as the user's own files, so a work or school account needs
/// this one before it may write into someone else's folder.
/// </summary>
public sealed record OAuthClientOptions(CloudProvider Provider, string ClientId, string? ClientSecret = null, bool SharedFiles = false)
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
    /// <summary>Microsoft gave this sign-in the files shared with the account too, not only the account's own. Kept, because a renewal must ask for what was given.</summary>
    public bool SharedFiles { get; init; }
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

/// <summary>
/// A folder that uploads go to instead of the top folder of the drive: the folder a sharing link leads to, as OneDrive
/// described it when the link was looked up. <see cref="Link"/> is that sharing link. In a browser it opens the folder for
/// whoever the link works for, which was decided when the folder was shared; the address of a file in the folder would
/// open only for an account that can already reach the file.
/// </summary>
public sealed record CloudFolder(string DriveId, string ItemId, string Name, Uri Link);

/// <summary>
/// One file to upload, under its own name; the service adds a number when the name is taken. It goes to the top folder of the
/// drive, or into <paramref name="Folder"/> when one is given (OneDrive only).
/// </summary>
public sealed record CloudUploadRequest(CloudAccount Account, string SourcePath, CloudFolder? Folder = null);
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
