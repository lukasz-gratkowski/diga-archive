using L = Diga.Core.Localization.AppText;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Diga.Core.Configuration;

public sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string FilePath { get; }
    public JsonSettingsStore(string? filePath = null) => FilePath = Path.GetFullPath(filePath ?? AppPaths.SettingsFile);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Loads the settings for a start of the application. A file that cannot be used (damaged, or written by a later version) is
    /// set aside under another name and the defaults are returned with that name: left in place, the file would be overwritten
    /// by the next thing the application saves, and whatever it held would be gone without a word.
    /// </summary>
    public async Task<(AppSettings Settings, string? SetAside)> LoadOrSetAsideAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try { return (await ReadAsync(cancellationToken).ConfigureAwait(false), null); }
            catch (InvalidDataException)
            {
                var stem = Path.Combine(Path.GetDirectoryName(FilePath)!, Path.GetFileNameWithoutExtension(FilePath) + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                var aside = stem + ".json";
                for (var number = 2; File.Exists(aside); number++) aside = stem + "-" + number.ToString(CultureInfo.InvariantCulture) + ".json";
                File.Move(FilePath, aside, overwrite: false);
                return (new(), aside);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteAsync(settings, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Changes what the file holds now, not what some part of the application read earlier: the file is read, changed and
    /// written in one step. A single remembered choice (the cloud destination, the way of saving) saved from a copy held in
    /// memory would undo whatever was saved to the file since that copy was read.
    /// </summary>
    public async Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var changed = change(await ReadAsync(cancellationToken).ConfigureAwait(false));
            Validate(changed);
            await WriteAsync(changed, cancellationToken).ConfigureAwait(false);
            return changed;
        }
        finally { _gate.Release(); }
    }

    private async Task<AppSettings> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            await using var input = File.OpenRead(FilePath);
            var result = await JsonSerializer.DeserializeAsync<AppSettings>(input, Options, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException(L.T("Core.Configuration.Empty"));
            // A file edited by hand can say null where a text belongs. The application never writes that; it is read as the
            // empty default, so that nothing later meets a null. The two folders are not given a default: Validate refuses them.
            result = result with
            {
                Language = result.Language ?? "system", FfmpegPath = result.FfmpegPath ?? "", FfprobePath = result.FfprobePath ?? "",
                MediaInfoPath = result.MediaInfoPath ?? "", GoogleClientId = result.GoogleClientId ?? "", OneDriveClientId = result.OneDriveClientId ?? ""
            };
            Validate(result);
            return result;
        }
        catch (JsonException ex) { throw new InvalidDataException(L.T("Core.Configuration.Invalid", FilePath), ex); }
    }

    private async Task WriteAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, settings, Options, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            // The temporary file is still there only when the save has just failed. A second failure while removing it (a virus
            // scanner holding it) must not take the place of the first.
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Throws for settings that cannot be saved, without writing anything.</summary>
    public static void Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Version != 1) throw new InvalidDataException(L.T("Core.Configuration.Version"));
        if (!Enum.IsDefined(settings.DefaultFormat)) throw new InvalidDataException(L.T("Core.Configuration.Format"));
        if (!Enum.IsDefined(settings.UploadProvider)) throw new InvalidDataException(L.T("Core.Configuration.Provider"));
        if (string.IsNullOrWhiteSpace(settings.OutputDirectory) || string.IsNullOrWhiteSpace(settings.CacheDirectory)) throw new InvalidDataException(L.T("Core.Configuration.Folders"));
        if (!Path.IsPathFullyQualified(settings.OutputDirectory) || !Path.IsPathFullyQualified(settings.CacheDirectory)) throw new InvalidDataException(L.T("Core.Configuration.Absolute"));
        // Tool locations are not checked here: settings saved by an earlier version may name a copy that is gone, and that must not stop the application from starting.
    }
}
