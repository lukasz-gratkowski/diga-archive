# Verifying your download

You do not have to do any of this to use AMG DIGA Archive. It is here for when you want to be sure that the file you downloaded is the file this project built. Each check takes a minute. In the examples, replace `0.6.2` with the version you downloaded.

## 1. The checksum: is the file complete and unaltered?

Every release lists a file `SHA256SUMS.txt` with the SHA-256 fingerprint of each download. Open PowerShell in the folder with the download:

```powershell
Get-FileHash .\DIGA-0.6.2-win-x64-setup.exe -Algorithm SHA256
```

Compare the result with the line for that file in `SHA256SUMS.txt` on the release page. Letter case does not matter. If they differ, the download is damaged or is not the published file: delete it and download again from the project's [releases page](https://github.com/lukasz-gratkowski/diga-archive/releases).

A matching checksum shows that you have the published file. It does not show who published it; the next two checks do.

## 2. The signature: who published it?

The release notes say whether a release is signed and by whom.

**A signed release.** Right-click the installer → **Properties** → **Digital Signatures**. The list shows the signer; **Details** must say *This digital signature is OK*. Or in PowerShell:

```powershell
Get-AuthenticodeSignature .\DIGA-0.6.2-win-x64-setup.exe | Format-List Status, SignerCertificate, TimeStamperCertificate
```

`Status` must be `Valid` and the signer must be the name given in the release notes. The same signature is on `Diga.exe`, `Diga.dll` and `Diga.Core.dll` in the installed application and in the portable package, and on the script `Test-DlnaRecorder.ps1` in the diagnostics kit. In a signed release the kit's launcher lets PowerShell run the script only while that signature is valid.

Even with a valid signature Windows may show *Windows protected your PC* for a while. That screen reflects how many people have already run files from this publisher, not a problem with the file; it then names the publisher. Choose **More info** to see it.

**An unsigned release.** Windows shows *Windows protected your PC* with *Unknown publisher*. That is what Windows shows for every unsigned program. Do checks 1 and 3; if both pass, choose **More info → Run anyway**.

## 3. The build attestation: was it built from this source?

For every release, GitHub records a signed statement that these exact files were produced by this repository's release workflow from the tagged commit. With the [GitHub CLI](https://cli.github.com/) (version 2.67 or later, signed in with `gh auth login`):

```powershell
gh attestation verify .\DIGA-0.6.2-win-x64-setup.exe --repo lukasz-gratkowski/diga-archive
```

A stricter form also pins the workflow and the tag:

```powershell
gh attestation verify .\DIGA-0.6.2-win-x64-setup.exe --repo lukasz-gratkowski/diga-archive `
  --signer-workflow lukasz-gratkowski/diga-archive/.github/workflows/release.yml --source-ref refs/tags/v0.6.2
```

The command succeeds only if the file is byte for byte one that the workflow built. It works for all five release files. It proves where a file came from; it is not a statement that the software is free of faults.

## FFmpeg

FFmpeg is not part of the release. When you ask for it, the installer or the application downloads one specific package from FFmpeg's Windows distributor and uses it only if its SHA-256 equals the value recorded in the application's source, in [`src/Diga.Core/Media/FfmpegPackage.json`](../src/Diga.Core/Media/FfmpegPackage.json). The same file lists the fingerprints of `ffmpeg.exe` and `ffprobe.exe`, so you can check the installed copies yourself:

```powershell
Get-FileHash "$env:LOCALAPPDATA\Programs\DIGA\tools\ffmpeg.exe" -Algorithm SHA256
```

(An FFmpeg downloaded from inside the application is in `%LOCALAPPDATA%\Diga\tools`.)

## Building it yourself

The strongest check is to build from source: [Building](BUILDING.md). Each release includes `DIGA-…-source.zip`, which Git wrote from the tagged commit (`git archive`), so it holds the tracked files of that commit and nothing else. The ID of the commit is stored in the archive as its comment, which PowerShell 7 shows with:

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path .\DIGA-0.6.2-source.zip)); $zip.Comment; $zip.Dispose()
```

The file `BUILDINFO.json` in the installed application and in the portable package names the commit again, and what the release was built with: the .NET SDK that built it, the .NET runtime and the Windows App SDK that are inside the application, the Inno Setup version that compiled the installer, and whether the files were signed.
