using L = Diga.Core.Localization.AppText;
using Diga.Core.Media;
using Microsoft.UI.Xaml.Controls;

namespace Diga.App;

public sealed partial class MainWindow
{
    // Looked up each time a tool is needed, so an FFmpeg downloaded a moment ago is found without a restart.
    // A page asks several times while it is being built, and without a managed copy every question walks the whole PATH.
    // The answer is kept for a moment; what was just downloaded or saved in Settings is looked up afresh.
    private (long At, string Ffmpeg, string Ffprobe, FfmpegTools? Tools)? _ffmpegSeen;
    private FfmpegTools? Ffmpeg
    {
        get
        {
            if (_ffmpegSeen is { } seen && Environment.TickCount64 - seen.At < 2000 && seen.Ffmpeg == _settings.FfmpegPath && seen.Ffprobe == _settings.FfprobePath) return seen.Tools;
            var tools = MediaToolLocator.FindFfmpeg(_settings.FfmpegPath, _settings.FfprobePath);
            _ffmpegSeen = (Environment.TickCount64, _settings.FfmpegPath, _settings.FfprobePath, tools);
            return tools;
        }
    }
    private string? FfmpegExecutable => Ffmpeg?.Ffmpeg;
    private string? FfprobeExecutable => Ffmpeg?.Ffprobe;
    // A missing library is reported by MediaInfoService; naming its expected place keeps Windows from searching elsewhere for it.
    private string MediaInfoLibrary => MediaToolLocator.Find(_settings.MediaInfoPath, MediaToolLocator.MediaInfo)
        ?? Path.Combine(MediaToolLocator.ApplicationToolsDirectory, MediaToolLocator.MediaInfo);
    private bool FfmpegAvailable => Ffmpeg is not null;
    // A copy the application manages that is known not to be the build this version was made for; see MediaToolLocator.Examine.
    private bool FfmpegOutdated => Ffmpeg is { Pinned: false };

    private static string FfmpegDownloadLabel => L.T("Shell.Ffmpeg.Download", FfmpegPackage.Pinned.Version, FormatBytes(FfmpegPackage.Pinned.SizeBytes));

    /// <summary>What a page shows where a missing FFmpeg would stop the user, or where an FFmpeg of another version is in use; null otherwise.</summary>
    private InfoBar? FfmpegNotice()
    {
        var (missing, outdated) = (!FfmpegAvailable, FfmpegOutdated);
        if (!missing && !outdated) return null;
        return new InfoBar
        {
            IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational,
            Title = L.T(missing ? "Shell.Ffmpeg.NoticeTitle" : "Shell.Ffmpeg.UpdateTitle"),
            Message = missing ? L.T("Shell.Ffmpeg.NoticeHelp") : L.T("Shell.Ffmpeg.UpdateHelp", FfmpegPackage.Pinned.Version),
            ActionButton = ActionButton(FfmpegDownloadLabel, DownloadFfmpegAsync)
        };
    }

    private Task DownloadFfmpegAsync() => RunOperationAsync(async ct =>
    {
        SetProgress(null, L.T("Shell.Ffmpeg.Starting"));
        var progress = new Progress<FfmpegInstallProgress>(value =>
        {
            if (value.Unpacking) SetProgress(null, L.T("Shell.Ffmpeg.Unpacking"));
            else SetProgress(value.Fraction * 100, L.T("Shell.Ffmpeg.Downloading", FormatBytes(value.BytesReceived), FormatBytes(value.TotalBytes)));
        });
        await new FfmpegInstaller().InstallAsync(MediaToolLocator.UserToolsDirectory, progress, ct);
        _ffmpegSeen = null;
        ShowBanner(L.T("Shell.Ffmpeg.InstalledTitle"), L.T("Shell.Ffmpeg.InstalledHelp", FfmpegPackage.Pinned.Version), InfoBarSeverity.Success);
    }, L.T("Shell.Ffmpeg.InstalledStatus"));
}
