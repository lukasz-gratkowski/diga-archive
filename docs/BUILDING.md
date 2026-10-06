# Building

For contributors. This page takes you from a clone to a development build that runs on your PC, with or without a recorder. The tests are described in [Testing](TESTING.md). Packaging, signing and publishing belong to the maintainer and are described in [Releasing](RELEASING.md) and [Signing](SIGNING.md).

## What you need

| | Needed for | Notes |
|---|---|---|
| Windows 11, 64-bit (x64) | everything | The application is built for x64 only and for Windows 11 (build 22000 or later). |
| [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0) | everything | `global.json` asks for version 10.0.302 and accepts a later patch of the same feature band (10.0.3xx). `dotnet --list-sdks` shows what you have. |
| [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows) (`pwsh`) | the scripts in `scripts/` | Windows PowerShell 5.1, which is part of Windows, is not enough for them. |
| Git | cloning | |
| An internet connection, once | NuGet packages and the pinned media tools | The FFmpeg package alone is about 110 MB. |
| Python 3 | the recorder emulator only | The emulator uses the standard library; no packages are installed. No automated test needs Python. |
| [Inno Setup 6](https://jrsoftware.org/isinfo.php) | packaging only | Not needed for a development build, and not something you install: the release procedure fetches the one pinned version into `tools/innosetup` with `scripts/Get-InnoSetup.ps1`. |
| Visual Studio | optional | See [Building in Visual Studio](#building-in-visual-studio). |

You do not install the Windows App SDK or the Windows SDK build tools yourself. They are NuGet packages (`Microsoft.WindowsAppSDK` 2.5.1 and `Microsoft.Windows.SDK.BuildTools`) that `dotnet restore` fetches. The application is self-contained: the .NET runtime and the Windows App SDK runtime are copied into the build output.

One thing is not established. The project's CI builds with the `dotnet` command line on GitHub's Windows image, and that image also contains Visual Studio, according to [GitHub's list of the software on it](https://github.com/actions/runner-images). So CI does not show that a PC with nothing but the .NET SDK is enough. If the build fails on such a PC, please open an issue and say what the first error was.

## The short way

```powershell
git clone https://github.com/lukasz-gratkowski/diga-archive.git
cd diga-archive
./scripts/Build.ps1
```

`scripts/Build.ps1` does five things, in this order, and stops at the first failure:

1. Runs `scripts/Get-Dependencies.ps1` (next section).
2. `dotnet restore Diga.sln --locked-mode`: the packages must be exactly the ones recorded in the `packages.lock.json` files; see [NuGet packages](#nuget-packages).
3. Runs the unit tests (`--filter "Category!=Integration"`).
4. Runs the integration tests (`--filter "Category=Integration"`).
5. Builds the application: `dotnet build src/Diga.App/Diga.App.csproj -p:Platform=x64`.

The test results are written as `unit.trx` and `integration.trx` to `artifacts/test-results`.

| Parameter | Effect |
|---|---|
| `-Configuration Debug` or `Release` | The build configuration. The default is `Release`. |
| `-SkipDependencyDownload` | Does not run `Get-Dependencies.ps1`. Use it when `tools/bin` is already filled. |
| `-SkipTests` | Restores and builds without running any test. |

Then start the application:

```powershell
./src/Diga.App/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/Diga.exe
```

## The pinned tools: `Get-Dependencies.ps1`

The repository contains no third-party programs. `scripts/Get-Dependencies.ps1` downloads two packages and checks them before anything is unpacked or started.

| Package | Version | Downloaded from | Where the version, address and checksum are recorded |
|---|---|---|---|
| FFmpeg, the “essentials” build published by Gyan Doshi | 9.0.2 | his GitHub releases (`github.com/GyanD/codexffmpeg`), or `gyan.dev` if that fails | `src/Diga.Core/Media/FfmpegPackage.json` |
| MediaInfo library (`MediaInfo.dll`, x64) | 26.05 | `mediaarea.net` | `scripts/Get-Dependencies.ps1` itself |

`FfmpegPackage.json` is the one place that describes the FFmpeg package. The application, the installer build and this script all read it, so a developer's tests run with the same FFmpeg that a user's copy of the application downloads.

**Where the files go.** Both folders are listed in `.gitignore`.

- `tools/downloads` holds the two archives (`ffmpeg.zip`, `mediainfo.zip`) and their unpacked contents.
- `tools/bin` holds what the build and the tests use: `ffmpeg.exe`, `ffprobe.exe`, `MediaInfo.dll`, `dependencies.json` (a description of the MediaInfo package) and a `licenses` folder with the licence and README texts of both packages.

**The checks.**

1. The SHA-256 of each archive is compared with the recorded value before the archive is unpacked. An archive with another checksum is never unpacked: the script stops. An archive left from an earlier pin is deleted and downloaded again. An archive that is already there with the right checksum is not downloaded a second time.
2. After copying, `ffmpeg.exe` and `ffprobe.exe` are each compared with their own recorded SHA-256.
3. Both programs are started once with `-version`.

If the script reports a checksum mismatch, do not run the downloaded file. Delete `tools/downloads` and try again. If the mismatch stays, the file at the distributor is no longer the pinned one; please open an issue. How a pin is changed is described in [Releasing](RELEASING.md#pinned-inputs-and-how-to-change-them).

**What `tools/bin` is used for.**

- The integration tests run the real `ffmpeg.exe`, `ffprobe.exe` and `MediaInfo.dll` from there.
- The application build copies `MediaInfo.dll`, `dependencies.json` and the MediaInfo licence files from there into the `tools` folder beside `Diga.exe`. FFmpeg is deliberately left out, because FFmpeg is not part of the application.

So run `Get-Dependencies.ps1` before the first build. A build made without it has no `MediaInfo.dll`, and the technical details that MediaInfo reads from a recording are not available in it.

## Building from the command line

The single steps, for when you do not want the whole of `Build.ps1`:

```powershell
./scripts/Get-Dependencies.ps1      # once, and again after a pin has changed
dotnet restore Diga.sln
dotnet build src/Diga.App/Diga.App.csproj -c Debug -p:Platform=x64
```

The build output is in

```text
src\Diga.App\bin\x64\<Configuration>\net10.0-windows10.0.26100.0\win-x64\
```

Start `Diga.exe` from that folder. The application is unpackaged: there is no MSIX package and nothing to register. It asks for no administrator rights.

The solution has three projects:

| Project | What it is |
|---|---|
| `src/Diga.Core` | The library: recorder protocol, download, FFmpeg and MediaInfo, cloud sign-in and upload, settings, texts. It has no user interface and is what the tests test. |
| `src/Diga.App` | The window (WinUI 3). |
| `tests/Diga.Tests` | The tests (xUnit). They reference `Diga.Core` only. |

### NuGet packages

- `nuget.config` in the repository leaves nuget.org as the only package source, whatever sources your own NuGet settings name.
- Beside each project is a `packages.lock.json` with the exact version and content hash of every package, direct or transitive. `scripts/Build.ps1` and CI restore in locked mode, which fails when a package differs from the lock file.
- A plain `dotnet restore`, `dotnet build` or a build in Visual Studio is not locked. When you change a package reference, it rewrites the lock files; commit them together with the change. [Releasing](RELEASING.md) has the details, including the check for packages with known vulnerabilities.

[Architecture](ARCHITECTURE.md) describes how the code is organised. The version number comes from `Directory.Build.props`. `installer/Diga.iss` repeats it as a default. The release procedure builds the installer with the number from `Directory.Build.props` and has the default changed together with it; see [Releasing](RELEASING.md#making-a-release).

### What a development build shares with an installed copy

The application keeps its own data in one folder for the Windows account, whichever copy of the application is running:

```text
%LOCALAPPDATA%\Diga
    settings.json     the settings
    Accounts\         saved cloud sign-ins, encrypted for the Windows account
    Cache\            the default folder for temporary files
    tools\            FFmpeg, when it was downloaded from inside the application
    logs\             the error log
```

A development build therefore uses the same settings and the same saved sign-ins as a copy you installed for the same Windows account. Keep that in mind before you choose **Disconnect** or change settings in a development build. Saved recordings go to the folder shown on the **Preserve** page; the default is `DIGA Exports` in your Videos folder.

### FFmpeg in a development build

The build output contains no FFmpeg, just like a release. Without FFmpeg the application saves exact copies, shows recording details and uploads. An easy-to-play file and the preview need FFmpeg.

The application takes `ffmpeg.exe` and `ffprobe.exe` together from one folder. It looks in `%LOCALAPPDATA%\Diga\tools` and in the `tools` folder beside `Diga.exe`, and prefers the copy that is the pinned build; only if neither folder has the pair does it look through the folders on `PATH`. A file named in **Settings** replaces the program found this way. For a development build you have two simple choices:

- In **Settings**, open **Media tools · advanced** and use the button that begins **Download FFmpeg**. This is the download a user gets; it goes to `%LOCALAPPDATA%\Diga\tools`.
- In the same section, enter the two programs from the repository's `tools\bin` folder as **Your own FFmpeg executable (optional)** and **Your own FFprobe executable (optional)**, then choose **Save & check media tools**.

Both choices are stored for your Windows account, so an installed copy sees them too.

## Building in Visual Studio

Visual Studio is optional. The project's own builds, in `Build.ps1` and in CI, use the command line. The steps below follow from the project files and from Microsoft's documentation; the project has no record of them being tried.

- Microsoft's guide for WinUI development, [Build your first WinUI app](https://learn.microsoft.com/windows/apps/get-started/start-here), names Visual Studio 2026 with the workload “WinUI application development”.
- Run `./scripts/Get-Dependencies.ps1` first. Visual Studio does not run it.
- Open `Diga.sln`.
- Choose the solution platform `x64`. The application project has no other platform.
- Set `Diga.App` as the startup project and start it. Because the application is unpackaged, Visual Studio starts `Diga.exe` directly.
- The tests of `Diga.Tests` appear in Test Explorer. The integration tests need `tools/bin`; see [Testing](TESTING.md).

If something here does not match what you see, please open an issue or a pull request that corrects this page.

## Running without a recorder: the emulator

`tests/fixtures/dlna_emulator.py` plays the part of a recorder on your own PC. It is a test fixture, not a model of a Panasonic recorder: it answers at once, it knows one folder, and it serves one small video.

### 1. Make a small test video

Any small MPEG file will do. The emulator sends the whole file from memory and declares it as `video/mpeg`, so keep it short. This command makes eight seconds of a test pattern with a test tone, as MPEG-2 video with MP2 sound, with the FFmpeg from `tools/bin`. It follows the command the integration tests use for their own video, with a larger picture and a longer duration:

```powershell
New-Item -ItemType Directory -Force artifacts | Out-Null
./tools/bin/ffmpeg.exe -hide_banner -f lavfi -i testsrc2=size=320x240:rate=25 -f lavfi -i sine=frequency=1000:sample_rate=48000 -t 8 -c:v mpeg2video -c:a mp2 -f mpeg artifacts/dlna-demo.mpg
```

The file is about 700 KB. The `artifacts` folder is ignored by Git. Never use a real recording as a fixture, and never commit one.

### 2. Start the emulator

```powershell
python tests/fixtures/dlna_emulator.py --media artifacts/dlna-demo.mpg --announce
```

| Option | Meaning |
|---|---|
| `--media FILE` | Required. The one video the emulator serves. |
| `--announce` | Listen on this PC's network address and answer network searches (SSDP on `239.255.255.250:1900`). The application can find the emulator only with this option. |
| `--address ADDRESS` | The network address to use with `--announce`. Without it the emulator takes the address of the network connection that leads to the internet. Give it when the PC has no such connection or more than one network. |
| `--port NUMBER` | The HTTP port. Without it a free port is chosen. |

The emulator prints the address of its description and of the recording, and runs until you press Ctrl+C.

Without `--announce` the emulator listens on `127.0.0.1` only. The application cannot open it then, because the application connects only to recorders that its network search finds. The diagnostic script can:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-DlnaRecorder.ps1 -DescriptionUrl http://127.0.0.1:PORT/description.xml -OutputDirectory $env:TEMP -NonInteractive
```

Replace `PORT` with the port the emulator printed. The script writes a report folder and a ZIP file into the folder you name and prints where they are; an exit code of 0 means that the emulator answered the folder request.

### 3. Connect the application

Start the development build. On **Connect**, choose **Find network recorders**.

- The emulator is listed as “DIGA demo recorder”, model “Emulator”, with your PC's address.
- If it is the only device that answered the search, and **Guide me to the next step** is switched on in **Settings** (it is by default), the application connects by itself and opens **Discover**. This is what it does for any single device that calls itself a DIGA.
- If other media servers answer as well (a television, a network drive), choose “DIGA demo recorder” in the list and then **Connect to recorder**.

The emulator's one folder, “Demo recordings”, holds four entries. They exist to show the four cases the application tells apart:

| Title | What the emulator declares | What the application shows |
|---|---|---|
| Family & travel 録画 | not converted (`DLNA.ORG_CI=0`), with a size | **Can be saved** |
| Unknown conversion status | no conversion flag, no size | **Can be saved · the recorder does not say whether this is the original version** |
| Protected programme | a protection mark (`DTCP1`) | **Copy-protected · cannot be saved** |
| Converted resource | converted (`DLNA.ORG_CI=1`) | **Offered only in a converted version · not saved by this application** |

The first two are rows of the list and can be ticked; both deliver the one video you gave the emulator. The other two are named below the list, each with its reason, and cannot be ticked.

[Testing](TESTING.md#testing-by-hand-against-the-emulator) lists what is worth trying from here.

### If the application does not find the emulator

- Check that the emulator printed `Answering SSDP searches`. Without `--announce` it cannot be found.
- When the emulator starts to listen, Windows may ask whether Python may communicate on the network. The emulator needs that on your private network.
- A VPN on the PC can change which network address the emulator chooses. Stop the VPN, or name the address with `--address`.
- The address must be a private network address: `10.x.x.x`, `172.16.x.x` to `172.31.x.x`, `192.168.x.x` or `169.254.x.x`. The search lists a device only if it answers from such an IPv4 address; a device at another address is left out of the list without a message.
- Choose **Search again**. Each search listens for three seconds.

## Other scripts

| Script | Purpose |
|---|---|
| `scripts/Build.ps1` | Fetch the tools, test and build; described above. |
| `scripts/Get-Dependencies.ps1` | Fetch and check the pinned media tools; described above. |
| `scripts/Test-DlnaRecorder.ps1`, `scripts/Run-DlnaDiagnostics.cmd` | The diagnostics kit that users run on a PC in the recorder's network. It is written for Windows PowerShell 5.1 on purpose, so that it runs on any Windows PC. See [Diagnostics kit](DLNA-DIAGNOSTICS.md). |
| `scripts/Build-BrandAssets.ps1` | Regenerates the icon and logo files from the master image. See [Branding](BRANDING.md). |
| The other scripts in `scripts/` | Packaging, signing and the checks of a release. A development build needs none of them. See [Releasing](RELEASING.md) and [Signing](SIGNING.md). |

## Packaging and signing

The installer, the portable package, the diagnostics kit and the source archive are made by the release procedure, not by a development build. Only that procedure needs Inno Setup 6, which it downloads itself in one pinned version. [Releasing](RELEASING.md) describes it, and [Signing](SIGNING.md) describes the signing setup. Releases are not signed yet; the signing pipeline is prepared and will be switched on later.
