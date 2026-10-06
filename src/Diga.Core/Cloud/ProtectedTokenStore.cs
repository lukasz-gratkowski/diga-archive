using L = Diga.Core.Localization.AppText;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Diga.Core.Cloud;

/// <summary>Tokens are encrypted for the current Windows user, separate from ordinary settings.</summary>
public sealed class ProtectedTokenStore(string? directory = null) : ICloudTokenStore
{
    private readonly string _directory = Path.GetFullPath(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Diga", "Accounts"));
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DIGA Cloud Tokens v1");

    public async Task<CloudAccount?> LoadAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(L.T("Core.Cloud.Token.WindowsRequired"));
        var path = TokenPath(provider, clientId);
        if (!File.Exists(path)) return null;
        var encrypted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var result = JsonSerializer.Deserialize<CloudAccount>(plain) ?? throw new InvalidDataException(L.T("Core.Cloud.Token.Invalid"));
            if (result.Provider != provider || result.ClientId != clientId) throw new InvalidDataException(L.T("Core.Cloud.Token.ClientMismatch"));
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task SaveAsync(CloudAccount account, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(L.T("Core.Cloud.Token.WindowsRequired"));
        Directory.CreateDirectory(_directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(account);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var destination = TokenPath(account.Provider, account.ClientId);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Task DeleteAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = TokenPath(provider, clientId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string TokenPath(CloudProvider provider, string clientId) => Path.Combine(_directory, $"{provider}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientId)))}.bin");
}
