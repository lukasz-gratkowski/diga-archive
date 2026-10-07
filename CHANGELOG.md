# Changelog

What changed in AMG DIGA Archive, version by version, written for the people who use it. The newest version comes first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

A changelog says what changed, not what was tried. How far each part of the application has been tested, and on what, is in the [README](README.md#how-far-it-has-been-tested).

## [Unreleased]

### Fixed

- **Typing an order number no longer makes the page jump.** The strip of steps at the top was put together anew with every character, and the card that explains the file names grew from one line to several as soon as a number was typed, which pushed everything below it down. The strip now only changes its words, and the card keeps its size. A very long number ends in an ellipsis in the strip and in the menu instead of adding line after line.
- **The name and logo in the menu line up with the menu.** They stand on a row of their own under the menu button: the logo over the column of the icons, the name where the labels begin.

## [0.6.0] - 2026-10-06

The first public release. Its changes are described as changes since version 0.5.2, the last development prerelease.

Version 0.6.0 has run from start to finish only against the project's recorder emulator, not against a real recorder. Its installer is not digitally signed, so Windows shows its SmartScreen warning; see [Verifying your download](docs/VERIFYING-DOWNLOADS.md).

### Added

- **Google Drive is back as a place to upload to.** It needs your own Google Cloud client (a client ID and a client secret), because Google's terms do not allow an open-source project to ship credentials. The application asks Google only for access to the files it creates itself. The client secret is kept with the saved sign-in, encrypted for your Windows account, and is never written to the settings file. The [cloud setup guide](docs/CLOUD-SETUP.md) takes you through the setup. The project has not run Google Drive against Google's real servers.
- **You choose where uploads go.** One list, **Cloud destination**, on **Settings**, **Archive** and **Cloud**, shows OneDrive and Google Drive with the state of each: **connected**, **not connected** or **sign-in ended**. The choice is remembered.
- **The Cloud page.** A new page, **Cloud**, lists what is already stored: the top folder of your OneDrive, or the files this application uploaded to your Google Drive. It only looks. Nothing is downloaded and nothing is changed.
- **Files saved earlier can be uploaded.** **Add files from this PC…** on **Archive** adds a file that was not saved in this session.
- **More care around uploads.** Before an upload the application checks the free space in the cloud. It reports each file, asks before it uploads the same file a second time in one session, and shows uploaded files on **Archive** with a link that opens them in the cloud.
- **Recordings that cannot be saved are listed with the reason**, instead of appearing as rows that cannot be used. Each recording shows its date, length and size, and a recording saved in this session is marked.
- **Help on the Connect page.** The checklist **If your recorder is not found** says what the recorder needs and opens by itself after a search that found nothing. The list of devices shows each device's address. When exactly one device answers the search and it calls itself a DIGA, the application connects to it at once, unless **Guide me to the next step** is switched off in **Settings**.
- **A recorder can be asked by its address.** Some home networks do not pass the search between Wi-Fi and cable. The checklist now has the field **Recorder's address (optional)** and the button **Ask this address**. Only an address of a home network, written as four numbers, is accepted. This has been tried against simulated devices only.
- **DVB subtitles are kept in an MKV file.** Before, a recording with DVB subtitles could only be kept as an exact copy. This was checked with FFmpeg on a generated recording, not on a real broadcast. MP4 and MPEG still cannot hold them, and a recording with teletext is still kept as an exact copy.
- **Progress with speed and time.** Downloads and uploads show how fast they go and about how long they still take. While work is running, the application asks Windows not to put the idle PC to sleep.
- **A question before closing.** Closing the window while work is running asks first, because a download or an upload that is stopped has to start again from the beginning.
- **More in the About section of Settings**: a link to the releases page, the versions of .NET, the Windows App SDK and MediaInfoLib inside this copy, where the error log is, and buttons to open its folder and to delete it. The application still never looks for updates by itself.
- **Guides in English and Polish**: a user guide, recorder setup and troubleshooting, and cloud setup. A guide opened from inside the application is the one that belongs to the installed version.
- The licence texts of .NET and the Windows App SDK are installed with the application, in the `licenses` folder.
- For contributors: the recorder emulator can answer the application's network search, so the whole application can be tried without a recorder. See [Building](docs/BUILDING.md).

### Changed

- **FFmpeg is no longer part of the application.** Neither the installer nor the portable package contains it. The installer offers to download it; the option is ticked to begin with and can be cleared. The application offers the same download in **Settings**, under **Media tools · advanced**, and on the pages where FFmpeg is needed. In both cases one specific package (FFmpeg 9.0.2, the “essentials” build of its distributor) is downloaded and used only if its SHA-256 checksum is the one recorded in the application. Only `ffmpeg.exe`, `ffprobe.exe` and their licence texts are kept. Exact copies, recording details and cloud upload work without FFmpeg; an easy-to-play file and the preview need it.
- **An FFmpeg of another version is pointed out.** This concerns the FFmpeg in the application's own folders: one that the installer or the application downloaded, or one that an earlier version left there. If it is not the version this version of the application is made for, the notice **FFmpeg update available** offers the download. A copy of your own that you named in **Settings**, or one found on PATH, is not compared. The fields for tool locations in **Settings** are empty unless you choose your own copy.
- **The saving options have plain names.** On **Preserve**, under **How to save**: “Keep delivered stream unchanged” is now **Exact copy, as the recorder delivers it**, and “Save as a stream-copy container” is now **The same picture and sound in an easy-to-play file**. The list “Container” is now **File type**. What happens to a recording has not changed: picture and sound are never re-encoded. The way of saving you chose last is remembered. Other pages and messages use the same words.
- **Saving several recordings goes on when one of them fails.** Before, the first failure ended the whole save. Now the save continues and stops only when two recordings in a row fail without anything arriving. The message at the end says how many recordings were saved and names the ones that were not, each with its reason. Saved recordings are unticked, so the next save does not fetch them again; the others stay ticked.
- **A recording that does not fit the chosen file type no longer ends the save.** It is kept as an exact copy, as before; the message now says so, and the other recordings are saved.
- **A recording downloaded for the preview or for its details is not downloaded again** when you save it.
- **Free space.** A save now leaves a reserve free: 1 GB on the drive Windows runs from, 64 MB on any other drive. Folders on a network share are checked too, where Windows says how much room they have. Each recording is checked again when its download starts, and a recording whose size the recorder does not give is stopped before the reserve is used up.
- **An exact copy of a recording whose type the recorder does not name** gets the extension that fits its content (`.ts`, `.m2ts`, `.mpg`, `.mp4` or `.mkv`) instead of `.bin`, when the content is recognised.
- **FFmpeg is not left running.** FFprobe and the preview have two minutes. While an easy-to-play file is written, FFmpeg is stopped when it makes no progress for five minutes, and the download is kept as an exact copy. FFmpeg also ends when the application is closed or crashes.
- **Uploads wait out an interruption.** While nothing answers at all, an upload keeps trying for a quarter of an hour instead of about a minute, and the status line says that it is trying again.
- **OneDrive uses a new built-in Microsoft registration** (application ID `bfd21bf0-32a9-4520-8bbb-d525e3d34aea`). A sign-in belongs to the application ID it was made with. If you connected OneDrive in version 0.5.2 with the built-in ID, choose **Connect OneDrive** once more. A sign-in made with your own application ID is not affected. Nobody has reported connecting or uploading through the new registration yet. The only thing checked for it is that Microsoft's sign-in service knows the ID and accepts the `http://localhost` redirect; this was checked without signing in. See the [README](README.md#how-far-it-has-been-tested).
- **Sign-in.** You have ten minutes to finish a sign-in in the browser, instead of five. Running out of time has its own message, and a cancelled sign-in says that nothing was connected. When a saved sign-in is no longer accepted, the application opens **Settings** with the button for connecting again highlighted, and offers the way back to the page you came from.
- **Settings.** There is one **Save preferences** button, in a bar that stays in view. Changing an application ID or client ID no longer ends a sign-in; only **Disconnect** does. A settings file that cannot be used is set aside under another name and reported, instead of being overwritten.
- **Temporary files.** Recordings downloaded for a preview are removed when the application closes, and what an earlier session left behind is removed at the next start. Unfinished working files of an interrupted save are removed at the next save into the same folder; complete downloads left under working names are reported, not deleted.
- **The error log** is now in the same folder as the settings (`%LOCALAPPDATA%\Diga\logs`) and is kept small.
- **Messages in your language.** Failures that Windows reports about files and folders (no access, a missing drive, a file in use, a full disk) are shown in the chosen language, with the technical detail after them.
- **Accessibility.** Sections of a page are marked as headings for screen readers. Texts that change, such as the selection count and the status line, are announced without moving the focus, and progress is announced less often. Buttons are named without their decorative arrows. Explanatory notes are no smaller than 12 px. After an action the page stays where it was and the focus returns to the control you used.
- An update from version 0.5.2 or earlier removes the disk-reader programs and the documents of the earlier version from the installation folder.

### Removed

- **Reading recorder disks and disk images.** The application now saves recordings from a recorder on the home network only. The disk reader, its helper programs, its pages and the restart with administrator rights are gone. The code is kept on the branch [`archive/disk-and-image-sources`](https://github.com/lukasz-gratkowski/diga-archive/tree/archive/disk-and-image-sources).
- **FFmpeg from the installer and the portable package**; see *Changed*.

### Security

- **Uninstalling removes saved sign-ins.** The uninstaller removes the saved cloud sign-ins, the default folder for temporary files, the error log and an FFmpeg that the installer or the application downloaded. The settings file and your saved recordings stay.
- **Sign-in hardening.**
  - The page that receives the OneDrive sign-in listens on this PC's own addresses only (`127.0.0.1` and `::1`), not on every network connection.
  - A saved sign-in that cannot be decrypted counts as no sign-in. The file is written through to disk before it replaces the previous one.
  - **Disconnect** removes every sign-in saved on this PC for that service. For Google Drive the application also asks Google to end the sign-in, and it asks you first, because the saved client secret goes with it.
  - Answers from the sign-in and upload services are limited to 4 MiB, no cookies are kept, and an answer in an unexpected shape is reported in the application's own words.
  - An upload address written with characters outside ASCII is refused. The list of cloud files asks only the service's own server for its next page.
- **The FFmpeg download is checked twice.** The package must have the recorded size and SHA-256, and `ffmpeg.exe` and `ffprobe.exe` must each have their recorded SHA-256 before they replace anything. Only HTTPS addresses are contacted. Settings and the installer say where the download comes from.
- **What a device on the network sends.**
  - An answer to the search counts only when it comes from the network of the adapter that received it, so a device cannot send the application to a host of another network the PC is on, such as a VPN.
  - A device that answers the search with many addresses no longer takes the places or the time of the others. A device that forges its answers can still keep the recorder out of the list; the list shows each device's address so that you can compare it with the recorder's.
  - FFmpeg and FFprobe read four kinds of file only (MPEG transport stream, MPEG programme stream, MP4, Matroska). A download that is really a script for another of FFmpeg's readers is not opened.
  - Invisible characters and characters that change the direction of text are taken out of a recording's title before it becomes a file name.
- **Saving.** A recording copied from the download made for the preview is written under a working name and gets its real name only when its fingerprint matches. Opening a saved file's folder cannot start a program that happens to have the same name.
- **Releases.** The release pipeline now records a build attestation for the files it publishes, which ties them to this repository's source, in addition to their SHA-256 checksums. It never replaces a published release. Digital signing with Microsoft Azure Artifact Signing is prepared in the pipeline but **not switched on yet**: installers are unsigned until the notes of a release say otherwise. See [Verifying your download](docs/VERIFYING-DOWNLOADS.md).

### Fixed

- **Cancel while FFmpeg is working** could take effect seconds late on a PC with few processors, because reading FFmpeg's output held the threads that the cancellation needed. The output is now read on threads of its own. An automated test covers this with the runtime limited to two processors; it was not tried in the window on such a PC.
- A fault while a page is being put together shows a message instead of closing the application.
- A download that breaks because the recorder was switched off or the network dropped is reported as a lost connection, in the chosen language, and no longer as a failure to read or write a file.
- A search on a PC that is connected to no network says so, instead of reporting that no recorder was found.
- A title that is a name Windows reserves for devices gets the prefix in the language of the application (`Nagranie_` in Polish) instead of always `Recording_`.
- The free-space check before a save allows for the download that exists beside its easy-to-play file until that file has been checked.
- A save folder must be given with its full path. A name without a drive is no longer taken to mean a folder beside the program.
- A sign-in service that is busy is reported as busy and tried again, instead of being blamed on the registration.
- Saving the **Settings** page keeps the folder and the file type you chose for this session, unless you edited their defaults on the page. What you typed on the page is kept when the page is redrawn.
- A Google sign-in saved by an earlier version is no longer deleted when the application starts.

## Earlier versions

Versions up to 0.5.2 were development prereleases and were not published. They are listed with one line each; the dates are those on which they were made.

### [0.5.2] - 2026-10-02

OneDrive needs no setup: a Microsoft application ID is built in.

### [0.5.1] - 2026-10-02

A recorder is connected through the network search only, and uploads go to OneDrive only; Google Drive was removed. The OneDrive sign-in shows the account chooser and names the drive.

### [0.5.0] - 2026-10-02

The **Order No** step names the saved files. Saving as a container keeps one file. A device that calls itself a DIGA is preselected. The window opens maximised.

### [0.4.3] - 2026-10-02

Folder requests to the recorder use the `u:Browse` form, after a recorder answered the earlier form with HTTP 412. A recorder that does not answer in time is reported as an error, not as a cancellation.

### [0.4.2] - 2026-09-30

A diagnostics kit reports what a recorder answers. HTTP errors from a recorder are shown with their status.

### [0.4.1] - 2026-09-30

Fixes to the Polish interface: the narrow navigation bar and the language of the playback controls.

### [0.4.0] - 2026-09-30

Polish interface. The language follows Windows or the choice in Settings.

### [0.3.0] - 2026-09-30

Saving recordings from a recorder on the home network (DLNA).

### [0.2.1] - 2026-09-29

The keyboard focus is kept when the disk filter changes.

### [0.2.0] - 2026-09-28

A new name and logo, and the guided steps Connect, Discover, Preserve and Archive.

### [0.1.0] - 2026-09-28

First development prerelease: reading recordings from Panasonic recorder disks and disk images and saving them without re-encoding.
