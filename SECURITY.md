# Security policy

## Supported versions

Only the latest release receives fixes. Please update before reporting a problem.

## Reporting a vulnerability

Please report security problems privately, not in a public issue:

1. Open the repository's “Security” tab on GitHub.
2. Choose “Report a vulnerability” and describe the problem. This link opens the form directly: [report a vulnerability](https://github.com/lukasz-gratkowski/diga-archive/security/advisories/new).

Include the application version (shown in **Settings**, in the section **About AMG DIGA Archive**), what you did, what happened, and what you expected. A proof of concept helps. Do not attach recordings, sign-in tokens, client secrets or unedited logs. The application's error log can name folders, recording titles and the recorder's address.

This is a small project maintained in spare time. The maintainer aims to acknowledge a report within a week and will say what happens next: a fix, a request for more detail, or the reason the report is not treated as a vulnerability. Please allow time for a fix to be released before publishing details.

## What is in scope

- The application (`src/`), its installer (`installer/`), the build and release scripts (`scripts/`) and the workflows (`.github/workflows/`).
- The diagnostic script `scripts/Test-DlnaRecorder.ps1` and what its report contains.

Problems in FFmpeg, MediaInfo, .NET, the Windows App SDK, Inno Setup or a recorder's firmware belong with their own projects or vendors. If such a problem can be reached through this application in a way the application could prevent, report it here as well.

One thing is not a vulnerability: the Microsoft application ID in the source code (`AppSettings.BuiltInOneDriveClientId`). It identifies the application to Microsoft and is not a secret; the registration has no client secret, and every user signs in to their own account. The repository contains no Google credentials at all.

**How the built-in registration is meant to be set up.** These settings are kept at Microsoft, not in this repository, so the repository cannot show them. They are listed here so that a difference can be reported:

- accounts: personal Microsoft accounts and accounts of any organisation;
- one platform, "Mobile and desktop applications", with the single redirect address `http://localhost`; no "Web" and no "Single-page application" platform;
- no client secret and no certificate;
- "Allow public client flows" set to **No**, so that the device-code flow and the password flow are not available under the application's name;
- no application permissions. The application asks only for the delegated permissions named below, and it uses only the authorization-code flow with PKCE.

If the registration behaves differently, for example if it hands out a sign-in by device code, please report it as described above.

## How the application limits risk

A short summary; [How it works](docs/HOW-IT-WORKS.md) and [Privacy](docs/PRIVACY.md) have the details.

- **Recorder.** Everything a device on the local network sends is treated as untrusted. Addresses must be local network addresses, and an address on another host is not contacted. Answers are limited in size and in time. XML with a DTD or external entities is refused. Requests for the device description and for folders follow no redirect; a download follows at most three, and only to the same recorder. Recorder titles never choose a path on disk. Recordings advertised as protected or converted are not downloaded, and nothing is decrypted.
- **Files.** An existing file is never replaced. The application deletes only what it made itself, which it recognises by name and place: the temporary files in the folder of its own session, the folders that earlier sessions of the application left behind, and its own unfinished working files in a save folder.
- **FFmpeg.** FFmpeg is not part of the application. When the user asks for it, one specific package is downloaded over HTTPS and used only if its size and SHA-256 equal the values recorded in the source code, and if `ffmpeg.exe` and `ffprobe.exe` each have their recorded SHA-256.
- **Cloud.** Sign-in happens in the user's browser, with the authorization-code flow and PKCE. The answer comes back to an address of this PC only (`127.0.0.1` or `::1`). Sign-ins, the user's Google client secret, and the link to a shared folder for uploads are stored encrypted for the current Windows user (DPAPI); they are never written to the settings file or to logs. Uploads go only to the service's own servers. **Disconnect** removes the saved sign-ins of that service and, for Google Drive, asks Google to end the sign-in. Uninstalling removes all saved sign-ins.
- **What the application may access.** For OneDrive it asks for Microsoft's permissions `Files.ReadWrite`, which covers the user's files, and `offline_access`, which lets it renew the sign-in without asking again. It uses them to add new files to the top folder of the OneDrive, to list that folder, and to read the kind of drive, the name of its owner and the free space. When the user has set a shared folder for uploads, a new sign-in asks for `Files.ReadWrite.All` in place of `Files.ReadWrite`: it covers every file the account can access. Microsoft describes `Files.ReadWrite` as the user's own files, so a work or school account needs the wider permission before the application can write into a folder that belongs to someone else. The application uses it only to look up the folder behind the link the user pasted, to add new files to that folder, to list it, and to read the free space of the drive that holds it. The identifiers Microsoft returns for the folder are checked before they become part of an address, and the link is sent to Microsoft Graph only. For Google Drive it asks only for access to the files it creates itself (`drive.file`); with that it also reads the address or name of the account and the free space.
- **No telemetry and no update check.** The application contacts the recorder; Microsoft or Google only when the user connects or disconnects an account, uploads, lists cloud files, or checks a shared folder for uploads; and the FFmpeg download addresses only when the user asks for FFmpeg.

These points describe what the code does. How far the application has been tried against real recorders and real cloud accounts is in the [README](README.md#how-far-it-has-been-tested).

## Verifying a download

Release files come with SHA-256 checksums and, from version 0.6.0 on, with a build attestation. Releases are not digitally signed yet: signing with Microsoft Azure Artifact Signing is prepared in the release pipeline and will be switched on later, and the notes of each release say whether that release is signed. Until then Windows shows its SmartScreen warning for the installer. See [Verifying your download](docs/VERIFYING-DOWNLOADS.md).
