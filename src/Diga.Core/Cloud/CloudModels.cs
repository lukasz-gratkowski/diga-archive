using L = Diga.Core.Localization.AppText;

namespace Diga.Core.Cloud;

public enum CloudProvider { GoogleDrive, OneDrive }
public sealed record OAuthClientOptions(CloudProvider Provider, string ClientId, string? ClientSecret = null)
{
    public override string ToString() => $"{Provider} ({ClientId})";
}

public sealed class CloudAccount
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
    public string DisplayName
    {
        get
        {
            if (Provider == CloudProvider.GoogleDrive) return "Google Drive";
            var drive = DriveType switch
            {
                "personal" => L.T("Core.Cloud.Account.Personal"),
                "business" or "documentLibrary" => L.T("Core.Cloud.Account.Business"),
                _ => "OneDrive"
            };
            return string.IsNullOrWhiteSpace(OwnerName) ? drive : drive + " · " + OwnerName;
        }
    }
    public override string ToString() => DisplayName;
}

public sealed record CloudUploadRequest(CloudAccount Account, string SourcePath, string? RemoteFileName = null, string? FolderId = null);
public sealed record CloudUploadResult(string Id, string Name, string? WebUrl);
public sealed record CloudUploadProgress(long BytesUploaded, long TotalBytes)
{
    public double Fraction => TotalBytes == 0 ? 0 : (double)BytesUploaded / TotalBytes;
}

public interface ICloudTokenStore
{
    Task<CloudAccount?> LoadAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default);
    Task SaveAsync(CloudAccount account, CancellationToken cancellationToken = default);
    Task DeleteAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default);
}
