# Third-party notices

## Panasonic filesystem recovery

Source: https://github.com/leecher1337/panasonic-rec

Pinned commit: `42cc94044b96cbe7f9faaf7640f46a339d7b93d2`.

The MEIHDFS parser is adapted from work copyright 2012 Honza Maly and 2015 leecher, under GPL-2.0-or-later. The Panasonic UDF implementation includes GPL-3.0-or-later libudf work copyright 2005/2008 Rocky Bernstein and Panasonic modifications by leecher; portions retain BSD notices from Scott Long. Original source and notices are preserved in `third_party/panasonic-rec`.

The combined DIGA source is distributed under **GPL-3.0-or-later**; see LICENSE. New implementation changes are copyright 2026 DIGA contributors.

## FFmpeg

https://ffmpeg.org/ — Windows build https://www.gyan.dev/ffmpeg/builds/

FFmpeg and ffprobe execute as separate processes. The bundled Gyan essentials build is GPLv3. The downloaded package's LICENSE and README are retained with the tools. Version, source provenance and hashes are recorded by `scripts/Get-Dependencies.ps1` in `tools/bin/dependencies.json`. Public binary distribution must include the corresponding source and third-party build obligations for the selected FFmpeg build; a link alone is not a replacement for those obligations.

## MediaInfo

https://github.com/MediaArea/MediaInfoLib — https://mediaarea.net/en/MediaInfo

MediaInfoLib is provided by MediaArea.net SARL under its BSD-style license. The DLL distribution's license and source information are retained with the tools. The application uses the MediaInfo library, while MediaInfo's separate GUI is not bundled.

## Microsoft and .NET packages

Microsoft.WindowsAppSDK, .NET, Windows SDK projections and System.Security.Cryptography.ProtectedData retain their package licenses and notices. Test packages are development-only. Review `dotnet list package --include-transitive` and generated package assets for the complete dependency inventory.
