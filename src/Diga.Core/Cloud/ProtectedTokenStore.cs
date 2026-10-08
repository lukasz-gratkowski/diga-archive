using L = Diga.Core.Localization.AppText;
using System.Security.Cryptography;
using Diga.Core.Configuration;
using System.Text;
using System.Text.Json;

namespace Diga.Core.Cloud;

/// <summary>Tokens are encrypted for the current Windows user, separate from ordinary settings.</summary>
public sealed class ProtectedTokenStore(string? directory = null) : ICloudTokenStore
{
    private readonly string _directory = Path.GetFullPath(directory ?? AppPaths.Accounts);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DIGA Cloud Tokens v1");

    public async Task<CloudAccount?> LoadAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default)
    {
        // A file that cannot be decrypted by this Windows account counts as no saved sign-in: connecting again replaces it.
        var plain = await ProtectedFile.ReadAsync(TokenPath(provider, clientId), Entropy, cancellationToken).ConfigureAwait(false);
        if (plain is null) return null;
        try
        {
            var result = JsonSerializer.Deserialize<CloudAccount>(plain) ?? throw new InvalidDataException(L.T("Core.Cloud.Token.Invalid"));
            if (result.Provider != provider || result.ClientId != clientId) throw new InvalidDataException(L.T("Core.Cloud.Token.ClientMismatch"));
            return result;
        }
        catch (JsonException) { return null; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task SaveAsync(CloudAccount account, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(L.T("Core.Cloud.Token.WindowsRequired"));
        Directory.CreateDirectory(_directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(account);
        try { await ProtectedFile.WriteAsync(TokenPath(account.Provider, account.ClientId), plain, Entropy, cancellationToken).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public Task DeleteAsync(CloudProvider provider, string clientId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Every sign-in saved for the provider goes, not only the one for the ID in use: a file left behind by an earlier ID
        // could otherwise never be removed from within the application.
        if (Directory.Exists(_directory))
            foreach (var path in Directory.EnumerateFiles(_directory, $"{provider}-*.bin*")) File.Delete(path);
        return Task.CompletedTask;
    }

    /// <summary>
    /// True when a sign-in is saved for the provider under any ID, also one that is no longer in use: changing an ID in the
    /// settings does not remove the sign-in made with the earlier one, and <see cref="DeleteAsync"/> is what removes it.
    /// </summary>
    public bool HasAny(CloudProvider provider)
    {
        try { return Directory.Exists(_directory) && Directory.EnumerateFiles(_directory, $"{provider}-*.bin*").Any(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private string TokenPath(CloudProvider provider, string clientId) => Path.Combine(_directory, $"{provider}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientId)))}.bin");
}
