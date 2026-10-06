using System.Text.Json.Serialization;
using Diga.Core.Cloud;
using Diga.Core.Media;

namespace Diga.Core.Configuration;

public sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public string Language { get; init; } = "system";
    public string OutputDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "DIGA Exports");
    public string CacheDirectory { get; init; } = AppPaths.Cache;
    /// <summary>The user's own copy of a media tool. Empty, or a file that no longer exists, means the copy <see cref="MediaToolLocator"/> finds.</summary>
    public string FfmpegPath { get; init; } = "";
    public string FfprobePath { get; init; } = "";
    public string MediaInfoPath { get; init; } = "";
    public OutputFormat DefaultFormat { get; init; } = OutputFormat.Matroska;
    /// <summary>The way recordings were saved last: as a container of <see cref="DefaultFormat"/>, or unchanged. Unchanged needs no FFmpeg, so it is the start.</summary>
    public bool SaveAsContainer { get; init; }
    /// <summary>Where Archive uploads to, and what the cloud view lists, until the user chooses the other one.</summary>
    public CloudProvider UploadProvider { get; init; } = CloudProvider.OneDrive;
    /// <summary>The user's own Google Cloud client. Google has no built-in one: its terms do not allow shipping credentials in open source.</summary>
    public string GoogleClientId { get; init; } = "";
    /// <summary>Application (client) ID of the project's own Microsoft registration for OneDrive upload: an identifier, not a secret.</summary>
    public const string BuiltInOneDriveClientId = "bfd21bf0-32a9-4520-8bbb-d525e3d34aea";
    /// <summary>A user's own registration; empty means the built-in one, so a later built-in ID reaches users who never set their own.</summary>
    public string OneDriveClientId { get; init; } = "";
    [JsonIgnore] public string EffectiveOneDriveClientId => string.IsNullOrWhiteSpace(OneDriveClientId) ? BuiltInOneDriveClientId : OneDriveClientId.Trim();
    public bool UseWizard { get; init; } = true;
}
