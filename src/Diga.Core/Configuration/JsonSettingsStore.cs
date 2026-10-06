using L = Diga.Core.Localization.AppText;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Diga.Core.Configuration;

public sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string FilePath { get; }
    public JsonSettingsStore(string? filePath = null) => FilePath = Path.GetFullPath(filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Diga", "settings.json"));

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath)) return new();
            await using var input = File.OpenRead(FilePath);
            var result = await JsonSerializer.DeserializeAsync<AppSettings>(input, Options, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException(L.T("Shell.Configuration.Empty"));
            Validate(result);
            return result;
        }
        catch (JsonException ex) { throw new InvalidDataException(L.T("Shell.Configuration.Invalid", FilePath), ex); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        finally { if (File.Exists(temporary)) File.Delete(temporary); _gate.Release(); }
    }

    /// <summary>Throws for settings that cannot be saved, without writing anything.</summary>
    public static void Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Version != 1) throw new InvalidDataException(L.T("Shell.Configuration.Version"));
        if (!Enum.IsDefined(settings.DefaultFormat)) throw new InvalidDataException(L.T("Shell.Configuration.Format"));
        if (string.IsNullOrWhiteSpace(settings.OutputDirectory) || string.IsNullOrWhiteSpace(settings.CacheDirectory)) throw new InvalidDataException(L.T("Shell.Configuration.Folders"));
        if (!Path.IsPathFullyQualified(settings.OutputDirectory) || !Path.IsPathFullyQualified(settings.CacheDirectory)) throw new InvalidDataException(L.T("Shell.Configuration.Absolute"));
        if (string.IsNullOrWhiteSpace(settings.FfmpegPath) || string.IsNullOrWhiteSpace(settings.FfprobePath) || string.IsNullOrWhiteSpace(settings.MediaInfoPath)) throw new InvalidDataException(L.T("Shell.Configuration.Tools"));
    }
}
