using L = Diga.Core.Localization.AppText;
using Diga.Core.Dlna;
using Diga.Core.Files;
using System.Text;
using System.Text.Json;
using Diga.Core.Media;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Playback;

namespace Diga.App;

public sealed partial class MainWindow
{
    private void BuildLibraryPage(StackPanel page)
    {
        if (HasDlnaSource) { BuildDlnaLibraryPage(page); return; }
        AddHeading(page, L.T("Journey.Library.Title"), L.T("Journey.Library.Subtitle"), L.T("Journey.Stage.Discover"));
        page.Children.Add(TintCard(new StackPanel { Spacing = 18, Children = {
            StageBadge(L.T("Journey.Library.Start"), "Amber"),
            SectionTitle(L.T("Journey.Library.Find"), Symbol.Video),
            Body(L.T("Journey.Library.ConnectHelp")),
            ActionButton(L.T("Journey.Library.Connect"), () => { Navigate("source"); return Task.CompletedTask; }, true)
        } }, "Amber"));
    }

    private bool EnsurePreviewPlayer()
    {
        // Windows N and Server installations may not provide Media Foundation.
        // Playback is optional: activate it only after an explicit preview request.
        try
        {
            if (_player is null)
            {
                var player = new MediaPlayer();
                _player = player;
                player.AutoPlay = false;
                player.MediaFailed += (_, args) => DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_closed && ReferenceEquals(_player, player))
                        ShowBanner(L.T("Journey.Preview.PlaybackError"), L.T("Journey.Preview.PlaybackErrorHelp", args.ErrorMessage), InfoBarSeverity.Warning);
                });
            }
            _previewUnavailable = null;
            return AttachPreviewElement();
        }
        catch (Exception ex)
        {
            ShowPreviewUnavailable(ex);
            return false;
        }
    }

    private bool AttachPreviewElement()
    {
        if (_previewHost is null) return _player is not null;
        if (_player is null)
        {
            _previewHost.Content = PreviewPlaceholder();
            return false;
        }
        try
        {
            if (_mediaElement is null)
            {
                _mediaElement = new MediaPlayerElement
                {
                    AreTransportControlsEnabled = true, AutoPlay = false, Height = 300,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                _mediaElement.SetMediaPlayer(_player);
                AutomationProperties.SetName(_mediaElement, L.T("Journey.Preview.Accessible"));
            }
            _previewHost.Content = _mediaElement;
            return true;
        }
        catch (Exception ex)
        {
            ShowPreviewUnavailable(ex);
            return false;
        }
    }

    private Border PreviewPlaceholder()
    {
        var content = new StackPanel { Spacing = 15, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new SymbolIcon(Symbol.Play) { Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock { Text = L.T("Journey.Preview.PlaceholderTitle"), FontSize = 18, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var detail = Muted(_previewUnavailable ?? L.T("Journey.Preview.PlaceholderHelp"), 12);
        detail.TextAlignment = TextAlignment.Center;
        content.Children.Add(detail);
        return new Border { Child = content, MinHeight = 240, Padding = new Thickness(24), CornerRadius = new CornerRadius(12), Style = AppStyle("DigaPillStyle") };
    }

    private void ShowPreviewUnavailable(Exception exception)
    {
        App.LogException("Preview initialization or playback", exception);
        ReleasePreviewPlayer();
        _previewUnavailable = L.T("Journey.Preview.UnavailableHelp", exception.Message);
        if (_previewHost is not null) _previewHost.Content = Body(_previewUnavailable);
        if (!_closed) ShowBanner(L.T("Journey.Preview.Unavailable"), _previewUnavailable, InfoBarSeverity.Warning);
    }

    private void DetachPreviewElement()
    {
        var element = _mediaElement;
        _mediaElement = null;
        try { element?.SetMediaPlayer(null); }
        catch (Exception ex) { App.LogException("Detaching preview", ex); }
    }

    private void ReleasePreviewPlayer()
    {
        DetachPreviewElement();
        var player = _player;
        _player = null;
        if (player is null) return;
        try { player.Source = null; }
        catch (Exception ex) { App.LogException("Releasing preview source", ex); }
        try { player.Dispose(); }
        catch (Exception ex) { App.LogException("Disposing preview player", ex); }
    }

    // The folder a session starts with is the one it keeps: a recording downloaded for a preview must not be orphaned by a
    // change in Settings. A changed folder is used from the next start.
    private SessionCache? _sessionCache;
    private SessionCache SessionFiles => _sessionCache ??= new(_settings.CacheDirectory, _sessionId);
    private string SessionCacheDirectory => SessionFiles.Directory;

    /// <summary>Creates this session's folder and holds its lock file, which tells a later start that the folder is in use.</summary>
    private void PrepareSessionCache() => SessionFiles.Prepare();

    /// <summary>Whole recordings are downloaded here for preview and inspection; nothing in it is worth keeping after the session.</summary>
    private void RemoveSessionCache()
    {
        try { _sessionCache?.Remove(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { App.LogException("Removing the session cache", ex); }
    }

    /// <summary>Removes the folders of sessions that ended without cleaning up (a crash, a power cut). A running instance keeps its lock.</summary>
    private static void RemoveStaleSessionCaches(string cacheDirectory, string currentSessionId)
    {
        try { SessionCache.RemoveStale(cacheDirectory, currentSessionId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { App.LogException("Removing earlier session caches", ex); }
    }

    private void ReleaseStagedPath(string path)
    {
        SessionFiles.Delete(path);
        _ownedCacheFiles.Remove(path);
        _stagedCacheFiles.Remove(path);
    }

    private async Task<string> InspectMediaAsync(string path, CancellationToken ct)
    {
        var media = await new MediaInfoService(MediaInfoLibrary).InspectAsync(path, ct);
        if (media.Available) return ReadableMediaInfo(media.Json);
        // Keep the missing native library visible; ffprobe, when it is installed, is a useful secondary diagnostic.
        var ffprobe = FfprobeExecutable ?? throw new InvalidOperationException(media.Error);
        var probe = await new MediaProbeService(ffprobe).ProbeAsync(path, ct);
        var result = new StringBuilder(L.T("Journey.Media.FfprobeDetails", media.Error, Path.GetFileName(path), FormatBytes(probe.SizeBytes), probe.Duration?.ToString() ?? L.T("Journey.Media.Unknown")));
        foreach (var stream in probe.Streams)
            result.AppendLine($"{MediaTrackLabel(stream.Type)} #{stream.Index}: {stream.Codec}  {stream.Width}×{stream.Height}  {stream.Language}");
        return result.ToString();
    }

    private static string ReadableMediaInfo(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var builder = new StringBuilder();
            if (!document.RootElement.TryGetProperty("media", out var media) || !media.TryGetProperty("track", out var tracks)) return json;
            string[] keys = ["Format", "Format_Profile", "CodecID", "Duration", "FileSize", "OverallBitRate", "BitRate", "Width", "Height", "FrameRate", "ScanType", "DisplayAspectRatio", "Channels", "ChannelLayout", "SamplingRate", "Language", "Title"];
            foreach (var track in tracks.EnumerateArray())
            {
                builder.AppendLine(MediaTrackLabel(track.TryGetProperty("@type", out var kind) ? kind.GetString() : null));
                foreach (var key in keys)
                    if (track.TryGetProperty(key, out var value)) builder.AppendLine($"{L.T("Journey.Media.Field." + key)}: {value}");
                builder.AppendLine();
            }
            builder.Append(L.T("Journey.Media.Units"));
            return builder.ToString();
        }
        catch (JsonException) { return json; }
    }

    private static string MediaTrackLabel(string? type) => type?.ToLowerInvariant() switch
    {
        "general" => L.T("Journey.Media.Track.General"),
        "video" => L.T("Journey.Media.Track.Video"),
        "audio" => L.T("Journey.Media.Track.Audio"),
        "text" or "subtitle" => L.T("Journey.Media.Track.Text"),
        "menu" => L.T("Journey.Media.Track.Menu"),
        "image" => L.T("Journey.Media.Track.Image"),
        "other" or "data" or "attachment" => L.T("Journey.Media.Track.Other"),
        null or "" => L.T("Journey.Media.Track.Track"),
        _ => type ?? L.T("Journey.Media.Track.Track")
    };

    private static TextBlock JourneyHeader(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static TextBox InformationBox(string text, string? accessibleLabel = null)
    {
        var box = new TextBox
        {
            AcceptsReturn = true, Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 130, MaxHeight = 400, FontSize = 12, BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        if (accessibleLabel is not null) AutomationProperties.SetName(box, accessibleLabel);
        // A report longer than the box must show that there is more: the default text box hides its scroll bar.
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        return box;
    }

    /// <summary>
    /// The room a whole batch needs, asked before the first recording is fetched, by the rule the download applies to each one:
    /// the library's reserve stays free, a share is asked like a drive, and space the system does not tell is not a refusal.
    /// </summary>
    private static void CheckAvailableSpace(string directory, long requiredBytes)
    {
        if (DiskSpace.Available(directory) is { } available && available < requiredBytes + DiskSpace.Reserve(directory))
            throw new IOException(L.T("Journey.Cache.SpaceRequired", directory, FormatBytes(requiredBytes + DiskSpace.Reserve(directory))));
    }
}
