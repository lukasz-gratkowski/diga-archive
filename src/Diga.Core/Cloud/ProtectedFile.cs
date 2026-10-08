using L = Diga.Core.Localization.AppText;
using System.Security.Cryptography;

namespace Diga.Core.Cloud;

/// <summary>
/// A small file encrypted for the current Windows user (DPAPI), written so that a power cut leaves either the file as it was
/// or the new one. The saved sign-ins and the link to the folder for uploads are kept this way.
/// </summary>
internal static class ProtectedFile
{
    /// <summary>
    /// What the file holds, or null when there is no file or it cannot be decrypted here. The caller clears the bytes when it
    /// has read them.
    /// </summary>
    public static async Task<byte[]?> ReadAsync(string path, byte[] entropy, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(L.T("Core.Cloud.Token.WindowsRequired"));
        if (!File.Exists(path)) return null;
        var encrypted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        // A file protected for another Windows account or another PC (a restored profile, a reset password) cannot be read here.
        // It counts as not there: saving again replaces it.
        try { return ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { return null; }
    }

    /// <summary>Encrypts the bytes and puts them in place of the file. The folder must exist; the caller clears <paramref name="plain"/>.</summary>
    public static async Task WriteAsync(string destination, byte[] plain, byte[] entropy, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(L.T("Core.Cloud.Token.WindowsRequired"));
        var encrypted = ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(encrypted, cancellationToken).ConfigureAwait(false);
                // On the disk before the rename, so that a power cut cannot leave an empty file under the real name.
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            // Left only by a save that has just failed; a second failure while removing it must not take the place of the first.
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
