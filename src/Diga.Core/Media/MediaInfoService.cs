using L = Diga.Core.Localization.AppText;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Diga.Core.Media;

/// <summary>
/// Loads the official MediaInfo C DLL. Each call owns a separate native handle.
/// A known limit: the library reads the recording inside this process, and the call that does so cannot be interrupted. A
/// cancellation is noticed before and after that call, not during it, and no time limit applies to it. FFmpeg and FFprobe
/// are separate programs and are stopped when they take too long (<see cref="ProcessLimits"/>); MediaInfo is not.
/// </summary>
public sealed class MediaInfoService(string libraryPath = "MediaInfo.dll")
{
    public Task<MediaInfoResult> InspectAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() => Inspect(path, cancellationToken), cancellationToken);

    private MediaInfoResult Inspect(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) throw new FileNotFoundException(L.T("Core.Media.Info.FileMissing"), path);
        if (!OperatingSystem.IsWindows()) return new(false, "", L.T("Core.Media.Info.WindowsRequired"));
        nint library = 0;
        nint handle = 0;
        DeleteDelegate? delete = null;
        CloseDelegate? close = null;
        try
        {
            library = NativeLibrary.Load(libraryPath);
            var create = Export<NewDelegate>(library, "MediaInfo_New");
            delete = Export<DeleteDelegate>(library, "MediaInfo_Delete");
            close = Export<CloseDelegate>(library, "MediaInfo_Close");
            var open = Export<OpenDelegate>(library, "MediaInfo_Open");
            var option = Export<OptionDelegate>(library, "MediaInfo_Option");
            var inform = Export<InformDelegate>(library, "MediaInfo_Inform");
            handle = create();
            if (handle == 0) return new(false, "", L.T("Core.Media.Info.AllocationFailed"));
            option(handle, "Internet", "No");
            option(handle, "Complete", "1");
            option(handle, "Output", "JSON");
            if (open(handle, Path.GetFullPath(path)) == 0) return new(false, "", L.T("Core.Media.Info.OpenFailed"));
            cancellationToken.ThrowIfCancellationRequested();
            var json = Marshal.PtrToStringUni(inform(handle, 0)) ?? "";
            using var parsed = JsonDocument.Parse(json);
            return new(true, JsonSerializer.Serialize(parsed.RootElement, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        { return new(false, "", L.T("Core.Media.Info.Unavailable", ex.Message)); }
        catch (JsonException) { return new(false, "", L.T("Core.Media.Info.InvalidJson")); }
        finally
        {
            if (handle != 0) { close?.Invoke(handle); delete?.Invoke(handle); }
            if (library != 0) NativeLibrary.Free(library);
        }
    }

    private static T Export<T>(nint library, string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint NewDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DeleteDelegate(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void CloseDelegate(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)] private delegate nuint OpenDelegate(nint handle, [MarshalAs(UnmanagedType.LPWStr)] string file);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)] private delegate nint OptionDelegate(nint handle, [MarshalAs(UnmanagedType.LPWStr)] string option, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint InformDelegate(nint handle, nuint reserved);
}
