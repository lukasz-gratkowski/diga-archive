# Privacy: what AMG DIGA Archive stores and what it sends

This document lists everything the application keeps on your PC, every address it contacts and when, what uninstalling removes, and how to take back the access you gave it to your cloud storage.

It describes version 0.6.4 and is taken from the source code. Each part names the files it is based on, so that the statements can be checked. It covers what the application's own code does. What Windows, your browser, your recorder, Microsoft and Google do with what reaches them is theirs to describe.

## Contents

- [The short version](#short)
- [What is stored on your PC](#stored)
  - [The settings file](#settings)
  - [Saved sign-ins](#sign-ins)
  - [Temporary files](#temporary)
  - [FFmpeg](#ffmpeg)
  - [The error log](#log)
  - [Saved recordings](#recordings)
  - [Kept only while the application runs](#memory)
  - [The application itself](#program)
- [What is sent, where and when](#sent)
  - [When the application starts](#start)
  - [Your home network and the recorder](#recorder)
  - [Microsoft, for OneDrive](#microsoft)
  - [Google, for Google Drive](#google)
  - [The FFmpeg download](#ffmpeg-download)
  - [Links that open in your browser](#links)
  - [Action by action](#actions)
- [What the application never does](#never)
- [Uninstalling: what goes and what stays](#uninstall)
- [Removing everything by hand](#by-hand)
- [Withdrawing the cloud permissions](#withdraw)

<a name="short"></a>
## The short version

- The application has **no telemetry, no update check, no account and no server of its own**. The project receives nothing from it.
- It contacts **nothing when it starts**. Every connection follows something you chose: searching for the recorder, opening a folder, saving, downloading FFmpeg, connecting a cloud account, uploading, listing your cloud files.
- Its own data is in **one folder**, `%LOCALAPPDATA%\Diga`: a settings file, the saved cloud sign-ins and the link to a shared folder if you set one, temporary files, a downloaded FFmpeg and an error log. Your recordings are where you chose to save them.
- Cloud sign-ins are **encrypted by Windows for your Windows account**. The application never sees your Microsoft or Google password.
- **Nothing is uploaded** until you tick files and choose to upload them, and then only to your own OneDrive or Google Drive, or to the shared OneDrive or SharePoint folder whose link you saved in **Settings**.

<a name="stored"></a>
## What is stored on your PC

Code: `src/Diga.Core/Configuration/AppPaths.cs`.

Everything the application keeps for itself is below one folder of your Windows profile:

```text
%LOCALAPPDATA%\Diga
```

That is usually `C:\Users\<your name>\AppData\Local\Diga`. Paste `%LOCALAPPDATA%\Diga` into the address bar of File Explorer to open it. The folder does not exist until the application has something to put there.

| Path below `%LOCALAPPDATA%\Diga` | What it holds | Created | Removed |
|---|---|---|---|
| `settings.json` | Your settings | When you first save settings or the application remembers a choice | By hand |
| `settings.invalid-<date>-<time>.json` | A settings file that could not be used and was set aside | At a start that finds such a file | By hand |
| `Accounts` (folder) | Saved cloud sign-ins and the link to a shared folder for uploads, encrypted | When you connect a cloud account or save such a link | The sign-ins with **Disconnect**; the link by clearing its field in **Settings**; the folder by uninstalling |
| `Cache` (folder) | Temporary files, unless you chose another folder | At the first preview or details of a session | Its content when the application closes; the folder by uninstalling |
| `tools` (folder) | FFmpeg, if you downloaded it inside the application | When you choose the FFmpeg download | Uninstalling |
| `logs` (folder) | The error log | At the first failure | Its files with **Delete the error log**; the folder by uninstalling |

Outside that folder:

- your saved recordings, in the folder you chose;
- while a save runs, working files in that same folder;
- temporary files in another folder, if you named one in **Settings**;
- the application itself.

The application's own code writes nothing to the Windows registry. The installer adds one entry there, so that Windows can list and uninstall the application; see [The application itself](#program).

<a name="settings"></a>
### The settings file

Code: `src/Diga.Core/Configuration/AppSettings.cs`, `JsonSettingsStore.cs`.

`settings.json` is a readable text file. These are all its fields:

| Field | What it holds | At the start |
|---|---|---|
| `Version` | The format of the file | `1` |
| `Language` | **Application language**: `system`, `en` or `pl` | `system` |
| `OutputDirectory` | **Default output folder** | `DIGA Exports` in your Videos folder |
| `CacheDirectory` | **Temporary files folder** | `%LOCALAPPDATA%\Diga\Cache` |
| `FfmpegPath`, `FfprobePath`, `MediaInfoPath` | The locations of your own copies of the media tools | empty |
| `DefaultFormat` | **Default file type for easy-to-play files**: `Matroska`, `Mpeg2` or `Mp4` | `Matroska` |
| `SaveAsContainer` | The way of saving you used last: exact copy (`false`) or easy-to-play file (`true`) | `false` |
| `UploadProvider` | **Cloud destination**: `OneDrive` or `GoogleDrive` | `OneDrive` |
| `OneDriveClientId` | Your own Microsoft application ID, if you entered one | empty |
| `GoogleClientId` | Your Google client ID, if you entered one | empty |
| `UseWizard` | **Guide me to the next step** | `true` |

What the file never holds: a password, a sign-in, the Google client secret, the order number, the recorder's address, a list of recordings or of saved files. The built-in Microsoft application ID is not written to it either; an empty `OneDriveClientId` means "use the built-in one".

The folder paths contain your Windows user name, as every path in your profile does.

The file is written when you choose **Save preferences** or **Save & check media tools**, when you connect or disconnect a cloud account (both also save the page), when you change **Cloud destination**, and when you save recordings in another way than the last time. It is not written when the application starts. Each write goes to a temporary file beside it, which then takes its place.

If the file is damaged, or was written by a later version of the application, the application does not overwrite it. It renames it to `settings.invalid-<date>-<time>.json`, starts with the default settings and tells you so.

<a name="sign-ins"></a>
### Saved sign-ins

Code: `src/Diga.Core/Cloud/ProtectedTokenStore.cs`, `CloudModels.cs` (`CloudAccount`), `CloudAuthService.cs`.

When you connect OneDrive or Google Drive, the service gives the application a sign-in: two tokens that stand for your permission. The application never receives your password; you type it into your browser, on Microsoft's or Google's page.

**Where.** One file for each service and each application or client ID, in `%LOCALAPPDATA%\Diga\Accounts`:

```text
OneDrive-<64 hexadecimal digits>.bin
GoogleDrive-<64 hexadecimal digits>.bin
```

The digits are the SHA-256 of the application or client ID the sign-in was made with.

**What a file holds**, before it is encrypted:

- the service, and the application or client ID;
- the access token, the time it runs out, and the refresh token with which the application renews it;
- for Google Drive, the client secret of your own Google client;
- what the service said about the drive when you signed in: for OneDrive the kind of drive (personal, or work or school) and the owner's name; for Google Drive the account's e-mail address, or its name if no address was given;
- a mark, once the service has refused the sign-in, so that the next start does not call it connected.

**How it is protected.** The content is encrypted with the Windows Data Protection API for the current user, together with a fixed extra value that is part of the source code. Microsoft's documentation of this function says that only code running as the same Windows user can decrypt such data. So another Windows account on the PC cannot read the file, and copying the file alone to another PC does not make it readable there. A program that runs under your own Windows account can ask Windows to decrypt it; the protection is as strong as the protection of your Windows account. A file that cannot be decrypted, for example in a profile restored on another PC, counts as "no saved sign-in": you connect again.

**When a file is written.** After a sign-in, each time the application renews the access token during an upload, a listing or a check of the shared folder, and when the service refuses the sign-in.

**When a file is removed.** **Disconnect** removes every sign-in file of that service, including one made with an ID you used earlier. Uninstalling removes the whole `Accounts` folder. Changing the application or client ID in **Settings** does not remove the file of the previous ID: it stays, encrypted and unused, and is used again if you return to that ID.

**The built-in Microsoft application ID changed on 5 October 2026.** The built-in ID is now `bfd21bf0-32a9-4520-8bbb-d525e3d34aea`. A sign-in saved with the earlier built-in ID has a different file name and is not used by the new one, so you connect once more. The earlier file stays in `Accounts` until one of these happens: you choose **Disconnect**, which removes every OneDrive sign-in on the PC; you uninstall; or you delete the files `OneDrive-*.bin` by hand. **Disconnect** can be chosen while any OneDrive sign-in is saved, also when only the earlier one is. The permission you gave to the earlier registration is a separate matter; see [Withdrawing the cloud permissions](#withdraw). What has and has not been tried with the new registration is stated in [How it works](HOW-IT-WORKS.md#tested).

<a name="temporary"></a>
### Temporary files

Code: `src/Diga.Core/Files/SessionCache.cs`, `WorkingFiles.cs`; `src/Diga.App/MainWindow.Library.cs`, `MainWindow.Settings.cs`, `MainWindow.Dlna.cs`.

**The session folder.** **Download and preview** and **Download and show details** first download the whole recording to the PC. It goes into a folder of its own inside the **Temporary files folder**, which is `%LOCALAPPDATA%\Diga\Cache` unless you chose another:

```text
<temporary files folder>\session-<32 hexadecimal digits>\
    .lock                              an empty file, held open while the application runs
    <32 hexadecimal digits>.<ext>      a whole recording, downloaded for a preview or for its details
    preview-<32 hexadecimal digits>.mkv    a preview clip of up to 45 seconds
```

The file names are random. They do not carry the recording's title. The folder is created the first time a preview or details are asked for, in the temporary files folder that is set at that moment, and the session keeps it until the application closes.

These files are removed:

- When the application closes: every file in the session folder, then the folder.
- When you choose **Remove this session's temporary files**, in **Settings** under **Temporary files**: the downloads and preview clips of this session.
- When another recording is downloaded for a preview or for its details: the whole recording downloaded before is deleted, so at most one is kept at a time.
- At the next start, after a crash or a power cut: folders of earlier sessions in the temporary files folder are removed. A folder is removed only if its `.lock` file exists and no running copy of the application holds it open. If you change the temporary files folder, what earlier sessions left in the previous folder is removed when the change is saved.

The application deletes temporary files only inside a session folder that it created itself. It refuses to delete a file anywhere else.

**Working files beside your recordings.** While a recording is saved, the destination folder holds a working file whose name starts with a full stop and ends in `.partial`, and for an easy-to-play file also the download under a name that starts with `.diga-`. Every ordinary end of a save removes them. After a crash they can stay; the next save into that folder removes the incomplete ones and tells you about complete downloads. [How it works](HOW-IT-WORKS.md#working-files) has the details.

**Other short-lived files.** While FFmpeg is downloaded, the package (about 110 MB) is in `%LOCALAPPDATA%\Diga\tools` under a name that ends in `.partial`; it is deleted when the download ends, whatever the outcome. The settings file and the sign-in files are each written through a temporary file with the ending `.tmp`, which exists for a moment.

<a name="ffmpeg"></a>
### FFmpeg

Code: `src/Diga.Core/Media/FfmpegInstaller.cs`, `MediaToolLocator.cs`; `installer/Diga.iss`.

FFmpeg is not part of the application. It is on your PC only if you asked for it:

| Downloaded by | Files | Where |
|---|---|---|
| The application | `ffmpeg.exe`, `ffprobe.exe`, `FFmpeg-LICENSE.txt`, `FFmpeg-README.txt` | `%LOCALAPPDATA%\Diga\tools` |
| The installer | `ffmpeg.exe`, `ffprobe.exe`, and the two texts in a subfolder `licenses` | the `tools` folder beside the application |

Nothing else from the FFmpeg package is kept. The installer downloads and unpacks the package in its own temporary folder.

<a name="log"></a>
### The error log

Code: `src/Diga.App/App.xaml.cs` (`LogException`); `src/Diga.Core/Media/ProcessRunner.cs` (`ErrorLog`); `src/Diga.Core/Cloud/CloudErrorDetail.cs`.

When something fails, the application writes the technical details to a text file:

```text
%LOCALAPPDATA%\Diga\logs\errors.log
%LOCALAPPDATA%\Diga\logs\errors.previous.log
```

**When.** Only when something fails. There is no log of what you do while everything works, and a **Cancel** of your own is not logged. The folder does not exist until the first failure.

**What an entry holds.** The date and time; a few English words that say what the application was doing; the technical description of the fault as .NET writes it, with the names of the program's functions that were running; and, when FFmpeg or FFprobe failed, the last 32 KiB of what that program wrote.

**What can be in it.** The application's own notice says it: **It can name folders, recording titles and the recorder's address.** In more detail:

- folder and file paths, which contain your Windows user name, and file names, which contain recording titles or your order number;
- the recorder's address on your home network, when a connection to it failed;
- the error code and the first line of the message that Microsoft or Google returned, when a sign-in, an upload or a listing failed.

**What is not in it.** The same notice: **It holds no passwords and no sign-ins**. The code is written accordingly: of an error answer from Microsoft or Google it reads only the named error code and message, never the whole answer, and a saved account is written out as the name shown in **Settings**, never with its tokens.

**How big.** When `errors.log` passes 512 KiB, it becomes `errors.previous.log`, replacing the one before, and a new file is begun. One entry is at most 48 KiB: of a longer one, the beginning and the end are kept.

**Where it goes.** Nowhere. The application never sends the log. **Open the log folder** and **Delete the error log**, in **Settings** under **About AMG DIGA Archive**, open the folder and delete both files. If you attach the log to a bug report, read it first and take out what you do not want to share.

<a name="recordings"></a>
### Saved recordings

Recordings are saved to the folder shown on the **Preserve** page. At the start that is `DIGA Exports` in your Videos folder; you can choose any other. The application keeps no list of what it saved: the **Archive** page shows the files of the current session and forgets them when the application closes. The uninstaller does not touch your recordings.

<a name="memory"></a>
### Kept only while the application runs

These exist in memory and are gone when the application closes:

- The order number.
- The devices found on the network, the recorder's address and its lists of folders and recordings.
- The list of files saved or added in this session, and the notes about what was uploaded.
- The list on the **Cloud** page. The page says so itself: **The application reads names, sizes and dates only when you ask, and keeps the list only until you close the application.**
- A Google client secret that you typed but have not connected with yet. Once you have connected, it is stored with the sign-in.
- The address of a running upload, and the one-time values of a sign-in in progress.

<a name="program"></a>
### The application itself

Code: `installer/Diga.iss`, `scripts/New-Release.ps1`.

- **Installed.** The installer puts the application into `%LOCALAPPDATA%\Programs\DIGA` unless you choose another folder. It installs for your Windows account only and needs no administrator rights. The folder holds the program, the .NET and Windows App SDK runtimes it needs, `tools\MediaInfo.dll`, the documentation, the diagnostics kit and the licence texts.
- **In Windows.** The installer adds a Start menu shortcut, a desktop shortcut if you tick that option, and the entry under your user account in the Windows registry that makes the application appear in the list of installed apps. It sets up nothing that starts by itself: no service, no scheduled task, no start with Windows.
- **Portable.** The portable ZIP is the same folder without the installer. It adds nothing to Windows. Its data is in `%LOCALAPPDATA%\Diga` all the same.

The diagnostics kit is a separate script that you run yourself. [Its own document](DLNA-DIAGNOSTICS.md) says what it does and what its report contains.

<a name="sent"></a>
## What is sent, where and when

<a name="start"></a>
### When the application starts

Code: `src/Diga.App/App.xaml.cs`, `AppLocalization.cs`, `MainWindow.Settings.cs` (`LoadSettingsAsync`), `MainWindow.Cloud.cs` (`LoadCloudAccountsAsync`).

Nothing is sent. At its start the application's code only works on the PC:

- it reads the settings file and the language preferences of Windows;
- it reads the saved sign-in files, to show whether a cloud account is connected. It does not contact Microsoft or Google to check them;
- it removes temporary folders of sessions that ended without cleaning up;
- it calculates the SHA-256 of an FFmpeg it manages, to know which build it is.

Closing the application sends nothing either.

<a name="recorder"></a>
### Your home network and the recorder

Code: `src/Diga.Core/Dlna/DlnaDiscoveryService.cs`, `DlnaContentDirectoryClient.cs`, `DlnaDownloadService.cs`, `EndpointPolicy.cs`.

| When | What is sent | To whom |
|---|---|---|
| **Find network recorders**, **Search again** | Two short search messages (SSDP `M-SEARCH`, UDP) from each network adapter of the PC | The multicast address `239.255.255.250`, port 1900. Devices on the same network segment that listen for such searches receive them. They do not pass a router. |
| Right after the search | One HTTP `GET` for the description of each device that answered, at most 64 | The device that answered, at the address the answer came from |
| **Ask this address** | The same two search messages (SSDP `M-SEARCH`, UDP), then one HTTP `GET` for each description that address names, at most 8 | The address you typed, port 1900. It must be a private IPv4 address written as four numbers. Unlike the search, these are ordinary messages to one address, which a router passes on to another private network. |
| **Connect to recorder**, opening a folder, **Refresh folder** | HTTP `POST` requests that ask for the content of a folder | The recorder you chose |
| Saving, **Download and preview**, **Download and show details** | One HTTP `GET` for each recording | The recorder |

- **What the requests contain.** The identifiers and addresses that the recorder itself supplied. No cookie, no sign-in and nothing about you. The code adds no header that names the application or the PC.
- **Where they can go.** Only to private addresses of a home network (`10.x.x.x`, `172.16.x.x` to `172.31.x.x`, `192.168.x.x`, `169.254.x.x`), to the PC itself, and to the matching local IPv6 ranges. A device cannot send the application to an address on the internet. No proxy is used for these requests.
- **Encryption.** The recorder decides whether its addresses begin with `http` or `https`, and the application uses what it is given. With `http`, the lists and the recordings cross your home network unencrypted.
- **What the recorder learns.** What any device sees that is asked for something: the PC's address on the home network, and which folders and recordings were asked for.

The application sends nothing in the background. It asks the recorder only when you act.

<a name="microsoft"></a>
### Microsoft, for OneDrive

Code: `src/Diga.Core/Cloud/CloudAuthService.cs`, `CloudUploadService.cs`, `CloudBrowseService.cs`.

Microsoft is contacted only after you choose **Connect OneDrive**, an upload to OneDrive, the list of your OneDrive files, or **Save & check the folder** while OneDrive is connected.

| When | Address | What is sent |
|---|---|---|
| **Connect OneDrive** | Your browser opens `https://login.microsoftonline.com/common/oauth2/v2.0/authorize` | In the address: the application ID, the return address on your PC, the permissions asked for, and two random one-time values. You sign in on Microsoft's page. |
| After you signed in | `https://login.microsoftonline.com/common/oauth2/v2.0/token` | The application ID, the one-time code that came back, and the secret value from which one of the two random values was calculated |
| Right after that, once | `https://graph.microsoft.com/v1.0/me/drive` | The sign-in. The application asks for the kind of drive and the owner's name, to label the connection. |
| Upload, list or folder check: if the sign-in is about to run out | The token address above | The application ID, the refresh token and the permissions the sign-in was given |
| Upload: before the first file | `https://graph.microsoft.com/v1.0/me/drive` | The sign-in. The application asks how much space is free. |
| Upload: for each file | `https://graph.microsoft.com/v1.0/me/drive/root:/<file name>:/createUploadSession` | The sign-in and the file name |
| Upload: the file | The upload address Microsoft returned | The content of the file, in pieces, and after a failure the question how much of it has arrived. No sign-in is sent to this address. |
| **List the files**, **Refresh the list** | `https://graph.microsoft.com/v1.0/me/drive/root/children`, and further pages on the same host | The sign-in. The application asks for names, sizes, dates and web links of the top folder. |
| **Disconnect** | nothing | The sign-in is removed from the PC. Microsoft is not told. |

With a shared folder for uploads set (see [below](#shared-folder)), four of these requests go to other addresses on the same host:

| When | Address | What is sent |
|---|---|---|
| **Save & check the folder**; before every upload; before every listing | `https://graph.microsoft.com/v1.0/shares/<the link, encoded>/driveItem` | The sign-in and, in the address, the sharing link you pasted. The header `Prefer: redeemSharingLink` asks Microsoft to accept the link for the connected account, as opening it in a browser would. |
| Upload: before the first file | `https://graph.microsoft.com/v1.0/drives/<drive>` | The sign-in. The application asks the drive that holds the folder how much space is free. |
| Upload: for each file | `https://graph.microsoft.com/v1.0/drives/<drive>/items/<folder>:/<file name>:/createUploadSession` | The sign-in and the file name |
| **List the files**, **Refresh the list** | `https://graph.microsoft.com/v1.0/drives/<drive>/items/<folder>/children`, and further pages on the same host | The sign-in. The application asks for names, sizes, dates and web links of the shared folder. |

The upload address must be an `https` address whose host is, or ends with, one of `1drv.com`, `onedrive.com`, `microsoftpersonalcontent.com`, `sharepoint.com`, `sharepoint.cn`, `sharepoint.us`, `storage.live.com`. Otherwise nothing is sent to it.

**The permissions.** The application asks for `offline_access`, which lets it renew the sign-in without asking you each time, and for the Microsoft Graph permission `Files.ReadWrite`. The application describes it on its OneDrive card: **Microsoft will ask whether this application may have full access to your files; when a folder for uploads is set below, it asks about all files you can access, because that folder may belong to someone else. The application uses the permission only to add new files to the top folder of your OneDrive, or to the folder set below, and to list that folder; it never changes or deletes a file that is already there.** The requests in the two tables are all the requests the code sends to Microsoft.

When a shared folder for uploads is set, a new sign-in asks for `Files.ReadWrite.All` in place of `Files.ReadWrite`. Microsoft describes it as full access to all files the user can access; Microsoft describes `Files.ReadWrite` as the user's own files, so a work or school account needs the wider permission before an application can write into a folder that belongs to someone else. The application uses it for the four requests in the second table and for nothing else. Which of the two permissions Microsoft granted is kept with the sign-in.

<a name="shared-folder"></a>
**The link to a shared folder.** Code: `src/Diga.Core/Cloud/CloudFolderLinkStore.cs`, `ProtectedFile.cs`. The link you paste into **Folder for uploads (optional)** is stored in `%LOCALAPPDATA%\Diga\Accounts\folder-OneDrive.bin`, encrypted with the Windows Data Protection API for the current user in the same way as the sign-ins, with an extra value of its own. It is not written to the settings file, because a sharing link of the kind that works for anyone is itself a key to the folder. The file is written when you save a link, removed when you save the page with the field empty (also a file that could not be read, which shows as an empty field), and removed with the `Accounts` folder by uninstalling. **Disconnect** leaves it. A file that cannot be decrypted counts as no folder: uploads then go to the top folder. The link is sent to Microsoft Graph only. On the **Archive** page it becomes the link beside each file uploaded into the folder, and it is kept in memory for that until you close the application. The error log can contain the message Microsoft returned for a refused link, but the application does not write the link itself to the log.

<a name="google"></a>
### Google, for Google Drive

Code: the same three files.

Google is contacted only after you choose **Connect Google Drive**, an upload to Google Drive, the list of your Google Drive files, or **Disconnect**.

| When | Address | What is sent |
|---|---|---|
| **Connect Google Drive** | Your browser opens `https://accounts.google.com/o/oauth2/v2/auth` | In the address: your Google client ID, the return address on your PC, the permission asked for, and two random one-time values. You sign in on Google's page. |
| After you signed in | `https://oauth2.googleapis.com/token` | The client ID, the client secret, the one-time code that came back, and the secret value from which one of the two random values was calculated |
| Right after that, once | `https://www.googleapis.com/drive/v3/about` | The sign-in. The application asks for the account's name and e-mail address, to label the connection. |
| Upload or list: if the sign-in is about to run out | The token address above | The client ID, the client secret and the refresh token |
| Upload: before the first file | `https://www.googleapis.com/drive/v3/about` | The sign-in. The application asks for the storage limit and the storage used. |
| Upload: for each file | `https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable` | The sign-in, the file name, its media type and its size |
| Upload: the file | The upload address Google returned, on a host that is, or ends with, `googleapis.com` | The sign-in and the content of the file, in pieces, and after a failure the question how much of it has arrived |
| **List the files**, **Refresh the list** | `https://www.googleapis.com/drive/v3/files` | The sign-in. The application asks for names, sizes, dates, types and web links. |
| **Disconnect** | `https://oauth2.googleapis.com/revoke` | The refresh token, or the access token if no refresh token is saved, with the request to end it |

**The permission.** The application asks for `https://www.googleapis.com/auth/drive.file` and nothing else. Google's documentation describes this permission as access to the files an application creates or that the user opens with it. The application says the same on its Google card: **The application asks Google only for access to the files it creates itself. It cannot see or change anything else in your Drive.**

**The client.** Google Drive is used with a Google Cloud client that you create yourself; the application brings none. The client ID is in the settings file, and the client secret is in the encrypted sign-in file.

### What is common to both services

- **The return address.** After you sign in, the browser is sent back to an address on your own PC: `http://localhost:<port>` for Microsoft, `http://127.0.0.1:<port>` for Google. The application listens there only while it waits for the sign-in, for ten minutes at most, on a port chosen at random between 49152 and 65534, and only on the PC's own loopback addresses, which nothing outside the PC can reach. It accepts one kind of request: a `GET` from the PC itself that repeats the random value of this sign-in. The browser's history then holds that address with a one-time code. The sign-in method, OAuth 2.0 with PKCE, is designed so that such a code works once, and only together with a secret value that the application keeps in memory and sends to the service alone.
- **Your browser.** The sign-in page is opened in your default browser. Cookies, the history and remembered accounts there belong to the browser.
- **No redirects.** The application's requests to Microsoft and Google do not follow a redirect.
- **Proxy and encryption.** These requests, and the FFmpeg download, use `https` with the certificate checks of Windows and .NET, which the code does not weaken. They use .NET's default connection settings, so a proxy configured in Windows applies to them.
- **What the services learn.** What they learn from any program that signs in and uploads: your account, the PC's internet address, and the files you upload with their names. Their own privacy terms apply to that.

<a name="ffmpeg-download"></a>
### The FFmpeg download

Code: `src/Diga.Core/Media/FfmpegPackage.json`, `FfmpegInstaller.cs`; `installer/Diga.iss`.

FFmpeg is downloaded only when you ask for it: with the button whose label begins with **Download FFmpeg** in the application, or with the FFmpeg option of the installer. The addresses are fixed in the source code:

1. `https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip`
2. `https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip`, used only if the first fails

The first address belongs to GitHub. The comment in the code notes that it redirects to its storage host. For this download the application follows redirects, at most five in a row, and the code does not limit where they lead. The second address belongs to the distributor of the build, Gyan Doshi. Whatever arrives is used only if its size and its SHA-256 are the ones recorded in the source code.

The request is a plain download. It contains nothing about you, your recorder or your recordings. The server sees the PC's internet address and the file that was asked for.

The installer uses the same two addresses and the same checksum. It skips the download if this FFmpeg build is already on the PC. Apart from this download, the installer contacts nothing, and the uninstaller contacts nothing at all.

<a name="links"></a>
### Links that open in your browser

Code: `src/Diga.App/Links.cs`; `src/Diga.App/MainWindow.Settings.cs`, `MainWindow.Cloud.cs`, `MainWindow.Export.cs`.

When you choose a link, the application hands the address to Windows, and your default browser opens it. The application itself sends nothing.

| Link in the application | Address |
|---|---|
| **AMG DIGA Archive · project and releases** | `https://github.com/lukasz-gratkowski/diga-archive` |
| **Look for a newer version on the releases page** | `https://github.com/lukasz-gratkowski/diga-archive/releases` |
| **Recorder setup and troubleshooting**, **Step-by-step setup guide**, **How to share a folder and use its link** | A guide in the same repository, at the address of the installed version: `https://github.com/lukasz-gratkowski/diga-archive/blob/v<version>/docs/…` |
| **MediaInfo · library and licence** | `https://mediaarea.net/en/MediaInfo` |
| **Open the app permissions of your Microsoft account** | `https://account.microsoft.com/privacy/app-access`, or `https://myapps.microsoft.com` for a work or school account |
| **Open the app permissions of your Google account** | `https://myaccount.google.com/connections` |
| **Open in browser** on the **Cloud** page; **Open in OneDrive** or **Open in Google Drive** on the **Archive** page | The web address that Microsoft or Google returned for that file. Only `https` addresses are offered. |
| **Open the shared folder** on the **Archive** page | The sharing link you saved in **Settings**. It can be any `https` address; the application does not check whose it is. |

The sign-in pages of Microsoft and Google are opened in the same way.

<a name="actions"></a>
### Action by action

| You do this | The application contacts |
|---|---|
| Start or close the application | nothing |
| Type an order number, change settings, **Save preferences** | nothing |
| **Save & check the folder** | Microsoft, if OneDrive is connected and a link is set; otherwise nothing |
| **Find network recorders**, **Search again** | the devices on your home network that answer the search |
| **Ask this address** | the address you typed, if it is a private address of a home network |
| **Connect to recorder**, open a folder, **Refresh folder** | the recorder |
| **Download and preview**, **Download and show details**, save recordings | the recorder |
| Download FFmpeg | `github.com` and the host it redirects to; if that fails, `www.gyan.dev` |
| **Connect OneDrive** | Microsoft, after your browser has been to Microsoft's sign-in page |
| **Connect Google Drive** | Google, after your browser has been to Google's sign-in page |
| Upload to OneDrive, or list its files | Microsoft |
| Upload to Google Drive, or list its files | Google |
| **Disconnect** on the OneDrive card | nothing |
| **Disconnect** on the Google Drive card | Google |
| **Open the log folder**, **Delete the error log**, **Remove this session's temporary files** | nothing |
| A link | nothing; your browser opens the address |

<a name="never"></a>
## What the application never does

- **No telemetry.** It sends no usage statistics, no crash reports and no error log to anyone. The source code contains no analytics or advertising component.
- **No update check.** The application says so in **Settings**: **The application never looks for updates by itself.** To look for a newer version you open the releases page yourself.
- **No account and no server of its own.** There is nothing to register and no licence key. The project runs no server that the application talks to. Its only place on the internet is the GitHub repository, which your browser opens when you choose a link.
- **No work in the background.** Nothing runs when the window is closed. There is no service, no scheduled task and no start with Windows.
- **No password.** You sign in to Microsoft or Google in your browser. The application receives tokens, never the password.
- **No upload you did not ask for.** Nothing from the recorder or from your PC goes to the cloud until you tick files and choose to upload them. The recorder's lists and titles are not sent to anyone.
- **No reading of your cloud files.** The **Cloud** page reads names, sizes and dates. The application never downloads the content of a cloud file, and never changes or deletes one.
- **No writing to the recorder.** It sends the recorder search and read requests only.
- **No network for the media tools.** MediaInfo is run with its own network option switched off. FFmpeg is told to open local files only.

<a name="uninstall"></a>
## Uninstalling: what goes and what stays

Code: `installer/Diga.iss` (the sections `[UninstallDelete]` and `[Files]`).

If Google Drive is connected, choose **Disconnect** in the application before you uninstall: only then is Google asked to end the sign-in. The uninstaller contacts no service.

**The uninstaller removes:**

- everything the installer put into the application's folder, the shortcuts and the entry in the list of installed apps;
- an FFmpeg downloaded by the installer (in the application's `tools` folder), with its licence texts;
- an FFmpeg downloaded by the application (`%LOCALAPPDATA%\Diga\tools`), with its licence texts and any unfinished download;
- the saved cloud sign-ins: the whole folder `%LOCALAPPDATA%\Diga\Accounts`, and with it a saved Google client secret and the saved link to a shared folder;
- the default temporary files folder, `%LOCALAPPDATA%\Diga\Cache`;
- the error log, `%LOCALAPPDATA%\Diga\logs`, and the folder `%LOCALAPPDATA%\DigaArchive`, where versions up to 0.5.2 kept their log;
- the folder `%LOCALAPPDATA%\Diga` itself, if it is empty afterwards.

**What stays:**

- **Your saved recordings.**
- **The settings file** `%LOCALAPPDATA%\Diga\settings.json`, and a settings file that was set aside. A later installation uses `settings.json` again.
- **Temporary files in a folder you chose yourself**, if the application was ended by force and could not remove them: folders named `session-` plus 32 digits and letters.
- **Working files beside your recordings**, if a save was interrupted by a crash: names that start with `.diga-` or end in `.partial`.
- **Your own copies of the media tools**, if you named any in **Settings**.
- **Everything in the cloud:** the files you uploaded, and the permission you gave the application at Microsoft or Google.

The portable version has no uninstaller. Delete its folder, and then follow the next section.

<a name="by-hand"></a>
## Removing everything by hand

1. If Google Drive is connected, choose **Disconnect** in **Settings** first.
2. Close the application.
3. Uninstall it, or delete the folder of the portable version.
4. Delete the folder `%LOCALAPPDATA%\Diga`. That removes the settings file, a settings file that was set aside, and anything the uninstaller left or that the portable version kept there: sign-ins, temporary files, FFmpeg and the log.
5. If the folder `%LOCALAPPDATA%\DigaArchive` exists, delete it too. Versions up to 0.5.2 kept their error log there.
6. Only if you chose your own temporary files folder: delete folders there whose names begin with `session-`.
7. In the folders you saved recordings to, look for files whose names begin with `.diga-` or end in `.partial`. They exist only after an interrupted save. A `.partial` file is incomplete. A `.diga-` file is a whole recording as the recorder delivered it: rename it to keep it, or delete it.
8. Your recordings are yours: keep them or delete them.
9. Take back the cloud permissions as described in the next section. Delete the uploaded files in OneDrive or Google Drive if you no longer want them there. If you created a Google Cloud project for the application, it stays at Google until you delete it there.

Nothing on the recorder was ever changed, so there is nothing to undo there.

<a name="withdraw"></a>
## Withdrawing the cloud permissions

![The cloud section of Settings: the cloud destination, and one card each for Microsoft OneDrive and Google Drive with the buttons to connect and to disconnect](images/en/10-settings-cloud.png)

There are two things to take back: the sign-in saved on this PC, and the permission recorded at the service.

**On this PC.** In **Settings**, under **Your cloud connections**, each service has a **Disconnect** button. It removes every sign-in saved on the PC for that service.

- **OneDrive.** The sign-in leaves the PC. Microsoft is not told: **The saved sign-in was removed from this PC. The permission you gave at Microsoft stays until you remove it on your account's page.**
- **Google Drive.** The application asks first, because the saved client secret goes with the sign-in and Google does not show a secret a second time. It then asks Google to end the sign-in and waits up to ten seconds for the answer. Its question explains the effect: **The application also asks Google to end the sign-in. That ends it on every PC that uses the same Google client with this account.** Afterwards it says whether Google confirmed. If Google did not confirm, the sign-in is removed from the PC all the same.

**At the service.** The permission itself is listed on a page of your account, where you can remove it:

- Microsoft, personal account: <https://account.microsoft.com/privacy/app-access>
- Microsoft, work or school account: <https://myapps.microsoft.com>
- Google: <https://myaccount.google.com/connections>

After **Disconnect**, the application offers the matching link, except when Google has confirmed that the sign-in has ended. Removing the permission on such a page is the service's way to end the sign-in wherever it is saved; how soon that takes effect is up to the service. The application notices at the next upload or listing, when the service refuses the sign-in, and asks you to connect again.

**The earlier built-in Microsoft registration.** If you connected OneDrive in version 0.5.2 with the built-in ID, you gave the permission to the registration that was built in then. Version 0.5.2 was the first with a built-in ID. Version 0.6.0 uses a new registration, `bfd21bf0-32a9-4520-8bbb-d525e3d34aea`, and no longer uses the earlier one. The permission you gave to the earlier registration stays at Microsoft until you remove it on the page above.

**What withdrawing does not do.** It does not delete the files you uploaded. They stay in your OneDrive or Google Drive until you delete them there.
