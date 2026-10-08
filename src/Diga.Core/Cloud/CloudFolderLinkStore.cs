using L = Diga.Core.Localization.AppText;
using System.Security.Cryptography;
using System.Text;
using Diga.Core.Configuration;

namespace Diga.Core.Cloud;

/// <summary>
/// The link to the folder that uploads go to, kept beside the saved sign-ins and encrypted like them. A sharing link can be
/// all it takes to change the folder it leads to, so it is not written to the settings file, and uninstalling removes it
/// together with the sign-ins.
/// </summary>
public sealed class CloudFolderLinkStore(string? directory = null)
{
    private readonly string _directory = Path.GetFullPath(directory ?? AppPaths.Accounts);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DIGA Cloud Folder v1");

    /// <summary>The saved link, or null when none is saved or the file cannot be read by this Windows account.</summary>
    public async Task<string?> LoadAsync(CloudProvider provider, CancellationToken cancellationToken = default)
    {
        var plain = await ProtectedFile.ReadAsync(LinkPath(provider), Entropy, cancellationToken).ConfigureAwait(false);
        if (plain is null) return null;
        try
        {
            var link = Encoding.UTF8.GetString(plain).Trim();
            // What is in the file is opened in a browser later; anything but a link of the accepted form counts as nothing saved.
            return CloudBrowseService.TryParseFolderLink(link, out _) ? link : null;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>Saves the link. An empty one removes the saved link, so that uploads go to the top folder again.</summary>
    public async Task SaveAsync(CloudProvider provider, string? link, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = link?.Trim() ?? "";
        var path = LinkPath(provider);
        if (text.Length == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        if (!CloudBrowseService.TryParseFolderLink(text, out _)) throw new ArgumentException(L.T("Core.Cloud.Folder.LinkInvalid"), nameof(link));
        Directory.CreateDirectory(_directory);
        var plain = Encoding.UTF8.GetBytes(text);
        try { await ProtectedFile.WriteAsync(path, plain, Entropy, cancellationToken).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    // Not "<provider>-...": Disconnect removes every file of that name, and the folder is a setting, not a sign-in.
    private string LinkPath(CloudProvider provider) => Path.Combine(_directory, $"folder-{provider}.bin");
}
