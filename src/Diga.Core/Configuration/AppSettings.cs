using System.Text.Json.Serialization;
using Diga.Core.Media;

namespace Diga.Core.Configuration;

public sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public string Language { get; init; } = "system";
    public string OutputDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "DIGA Exports");
    public string CacheDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Diga", "Cache");
    public string FfmpegPath { get; init; } = ToolPath("ffmpeg.exe");
    public string FfprobePath { get; init; } = ToolPath("ffprobe.exe");
    public string MediaInfoPath { get; init; } = ToolPath("MediaInfo.dll");
    public OutputFormat DefaultFormat { get; init; } = OutputFormat.Matroska;
    public string GoogleClientId { get; init; } = "";
    /// <summary>Application (client) ID of the project's own Microsoft registration for OneDrive upload: an identifier, not a secret.</summary>
    public const string BuiltInOneDriveClientId = "aa6acb80-dd42-45d3-b260-e44f327c4c42";
    /// <summary>A user's own registration; empty means the built-in one, so a later built-in ID reaches users who never set their own.</summary>
    public string OneDriveClientId { get; init; } = "";
    [JsonIgnore] public string EffectiveOneDriveClientId => string.IsNullOrWhiteSpace(OneDriveClientId) ? BuiltInOneDriveClientId : OneDriveClientId.Trim();
    public bool UseWizard { get; init; } = true;

    private static string ToolPath(string name)
    {
        foreach (var folder in new[] { Path.Combine(AppContext.BaseDirectory, "tools"), Path.Combine(AppContext.BaseDirectory, "tools", "bin"), Path.Combine(Environment.CurrentDirectory, "tools", "bin") })
        {
            var candidate = Path.Combine(folder, name);
            if (File.Exists(candidate)) return candidate;
        }
        return name;
    }
}
