# Third-party notices

AMG DIGA Archive itself is distributed under **GPL-3.0-or-later**; see LICENSE. Copyright 2026 DIGA contributors.

Versions up to 0.5.2 also read Panasonic recorder disks with code derived from [panasonic-rec](https://github.com/leecher1337/panasonic-rec). That code is no longer part of the application; it remains in the repository history and on the branch `archive/disk-and-image-sources`, with its notices.

## FFmpeg

https://ffmpeg.org/ — Windows build https://www.gyan.dev/ffmpeg/builds/

**FFmpeg is not part of AMG DIGA Archive and is not distributed with it.** Neither the installer nor the portable package contains FFmpeg. The application uses it, as two separate programs (`ffmpeg.exe` and `ffprobe.exe`), only to save a recording in an MKV, MP4 or MPEG container and to prepare the preview; everything else works without it.

When the user asks for it, FFmpeg is downloaded to the user's computer directly from its distributor:

- the installer offers it as an option that the user can clear;
- the application offers it in Settings, under Media tools, and on the pages where it is needed.

What is downloaded is the "essentials" build 9.0.2 published by Gyan Doshi (https://www.gyan.dev/ffmpeg/builds/, mirrored at https://github.com/GyanD/codexffmpeg/releases), which is licensed under the GNU GPL version 3 or later. The package is accepted only if its SHA-256 equals the one recorded in `src/Diga.Core/Media/FfmpegPackage.json`, the single place where the version, the two download addresses and the checksums are kept. Of the package, only `ffmpeg.exe`, `ffprobe.exe` and the package's own `LICENSE` and `README.txt` are kept; the licence text and the README are saved beside the programs. The source code of that build is offered by its distributor at the addresses above; the FFmpeg source itself is at https://github.com/FFmpeg/FFmpeg.

A user can instead point the application to another FFmpeg and ffprobe in Settings, or have one on `PATH`.

## MediaInfo

https://github.com/MediaArea/MediaInfoLib — https://mediaarea.net/en/MediaInfo

MediaInfoLib is provided by MediaArea.net SARL under its BSD-style license. `MediaInfo.dll` (version 26.05, unmodified, with MediaArea's own signature) is distributed with the application in the `tools` folder; its license and README are in `tools/licenses`, and its version, download address, checksum and source address are recorded by `scripts/Get-Dependencies.ps1` in `tools/dependencies.json`. The application uses the MediaInfo library, while MediaInfo's separate GUI is not bundled.

## Microsoft and .NET packages

Microsoft.WindowsAppSDK, .NET, Windows SDK projections and System.Security.Cryptography.ProtectedData retain their package licenses and notices. Test packages are development-only. Review `dotnet list package --include-transitive` and generated package assets for the complete dependency inventory.
