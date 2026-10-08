# Architecture

This document is for people who change the code. It says which projects there are and what depends on what, what the main types of the library do, how the window is put together, which rules every change has to keep, and where the tests for each area are.

It describes the code of version 0.6.3. What the application does, step by step and with the reasons, is in [How it works](HOW-IT-WORKS.md). What it stores and sends is in [Privacy](PRIVACY.md). Commands for building and testing are in [Contributing](../CONTRIBUTING.md).

## Contents

- [Projects](#projects)
- [The repository](#repository)
- [The library, folder by folder](#library)
- [The window](#window)
- [One save, from click to file](#flow)
- [Rules a change must keep](#rules)
- [Conventions the code follows](#conventions)
- [Where the tests are](#tests)
- [Build, package and release](#build)

<a name="projects"></a>
## Projects

The solution `Diga.sln` has three projects.

| Project | What it is | Target | Depends on |
|---|---|---|---|
| `src/Diga.Core` | The library: everything that talks to the recorder, writes files, runs the media tools, talks to the cloud, stores settings and holds the texts. No user interface types. | `net10.0` | One package, `System.Security.Cryptography.ProtectedData` |
| `src/Diga.App` | The window. WinUI 3, unpackaged, self-contained, x64. Its program is `Diga.exe`. | `net10.0-windows10.0.26100.0`, minimum Windows build 22000 | `Diga.Core`; `Microsoft.WindowsAppSDK` 2.5.1; `Microsoft.Windows.SDK.BuildTools` |
| `tests/Diga.Tests` | The tests. xUnit. | `net10.0` | `Diga.Core` only |

```text
Diga.App  ----->  Diga.Core  <-----  Diga.Tests
```

Two consequences follow from this picture:

- **The library knows nothing about the window.** It can be built and tested without Windows App SDK. The parts of it that need Windows (the encryption of sign-ins, the MediaInfo library) check for Windows at run time.
- **The tests do not reference the window.** `Diga.App` has no automated tests of its own. It is covered by being built in CI and by the package check, which starts the application and looks for its window. This is the reason for the rule that decisions about files live in the library; see [Rules](#rules).

The tests use the library's public types. Nothing is opened to them with `InternalsVisibleTo`.

At run time the application also uses three files that are not .NET code:

| Tool | How it is used | Shipped |
|---|---|---|
| `ffmpeg.exe`, `ffprobe.exe` | Started as separate programs by `ProcessRunner`. The check of the media tools (`ValidateToolsAsync` in `MainWindow.Settings.cs`) starts them the same way, with `-version` and a limit of 30 seconds. | No. Downloaded by the installer or the application; see `FfmpegPackage.json` |
| `MediaInfo.dll` | Loaded as a native library by `MediaInfoService` | Yes, in `tools` beside the application |

Shared build settings are in `Directory.Build.props`, among them the version (`0.6.3`). The .NET SDK version is pinned in `global.json`.

<a name="repository"></a>
## The repository

```text
src/Diga.Core/            the library
  Cloud/                  sign-in, upload, list of cloud files
  Configuration/          settings and the places where the application keeps things
  Dlna/                   finding the recorder, reading its lists, downloading
  Files/                  working files and the temporary folder of a session
  Localization/           the texts in English and Polish
  Media/                  FFmpeg, FFprobe, MediaInfo
  Naming/                 names of saved files
src/Diga.App/             the window
tests/Diga.Tests/         the tests
tests/fixtures/           dlna_emulator.py, a stand-in recorder for trying the application by hand
installer/Diga.iss        the installer (Inno Setup 6)
scripts/                  build, dependencies, release, signing, package check, diagnostics kit
.github/                  CI and release workflows, issue forms
docs/                     the documentation, which also ships with the application
tools/bin/                not in the repository: made by scripts/Get-Dependencies.ps1
```

<a name="library"></a>
## The library, folder by folder

Each type is in the file of the same name unless a file is named.

### Dlna

| Type | What it does |
|---|---|
| `DlnaDiscoveryService` | Searches for media servers with SSDP and reads the description of each device that answers. `DiscoverWithRespondersAsync` also says how many addresses answered; `DiscoverAtAsync` asks one address on the home network instead of the whole network, and `TryParseAddress` says which text counts as such an address. `TryParseSearchResponse` decides which answers are usable, `SelectLocations` which descriptions are read and in which order, so that one device cannot crowd out the others; `GetDeviceAsync` turns a description into a `DlnaDevice`. |
| `IDlnaSsdpTransport` (in `DlnaModels.cs`), `DlnaSsdpTransport` | The part of the search that touches the network. The default sends from every IPv4 adapter, or to one address, and drops an answer that does not come from the subnet of the adapter that received it; tests supply their own. |
| `DlnaContentDirectoryClient` | `BrowseAsync` lists one folder with the UPnP `Browse` action, page by page, with consistency checks and limits, and turns the DIDL-Lite answer into `DlnaObject`s. It sends no other action. |
| `DlnaDevice`, `DlnaObject`, `DlnaResource` (in `DlnaModels.cs`) | A recorder; a folder or a recording; one version in which a recording is offered. `DlnaObject.PreferredResource` and `TransferStatus` hold the decision whether a recording can be saved. |
| `DlnaProtocolInfo` (in `DlnaModels.cs`) | Reads a `protocolInfo` value: protected or not, converted or not. Anything malformed counts as converted. |
| `DlnaDevicePreference` (in `DlnaModels.cs`) | Which found device is preselected. |
| `DlnaDownloadService` | Downloads one recording unchanged. It checks the address, the destination and the recorder's answer, writes a working file, calculates the SHA-256 and renames without replacing. `GuessSafeExtension` chooses the extension from a fixed list, and `ExtensionFromContent` tells the container from the first bytes when the recorder names no known type. Before and during the download it refuses to leave the drive with less than its reserve (`DiskSpace`). |
| `DiskSpace` | `Available` asks Windows how much the current user can still write in a folder, a share included; `Reserve` is what a download leaves free: 1 GiB on the drive Windows runs from, recognised by its volume serial number, 64 MiB elsewhere. The window's own check before a save uses the same two. |
| `EndpointPolicy` (internal) | The address rules: only `http` and `https`, no user name, the same host as the recorder's description, and an HTTP client that connects to local addresses only, without proxy, cookies, sign-in or redirects. `IsLocalAddress` is the one test for "is this address local", and `ConnectLocalAsync` the one place where a connection to a recorder is opened; the search, the lists and the download all use them. |
| `DlnaXml` (internal) | Reads an answer within 10 seconds and 4 MiB and parses XML with document type definitions prohibited and a depth limit. `DlnaBrowseException` carries a UPnP error code. |

### Media

| Type | What it does |
|---|---|
| `RemuxService` | Makes the easy-to-play file. `ValidateCompatibility` holds the lists of codecs each file type accepts, `BuildArguments` the FFmpeg command line (`-c copy`), and `RemuxAsync` runs FFmpeg into a working file, compares the streams of the result with the source and renames without replacing. |
| `MediaProbeService` | Runs FFprobe and parses its JSON into a `MediaProbeResult` with its `MediaStreamInfo`s. |
| `PreviewService` | Copies the first 45 seconds of the first video and audio stream into an MKV clip. |
| `MediaInfoService` | Loads `MediaInfo.dll` through its C interface for one reading and returns its report as JSON. |
| `ProcessRunner`, `IProcessRunner` | Starts a program with an argument list and without a shell, reads its output on two threads of its own, keeps a bounded amount of it and ends the whole process tree on cancellation. `ProcessLimits` gives a run a time limit for the whole run, or for the time without progress; when one runs out the program is ended and the run fails with `ProcessTimeoutException`. Every program started is put into a Windows job object that ends it when the application ends. `ProcessResult.ErrorSummary` is for a message, `ErrorLog` for the log. |
| `MediaToolLocator` | Finds the two FFmpeg programs, together from one folder unless the settings name a file for either of them, and the MediaInfo library. `Examine` calculates the SHA-256 of the copies the application manages, so that `FfmpegTools.Pinned` can say whether they are the pinned build. |
| `FfmpegPackage` (in `FfmpegInstaller.cs`) | The pinned FFmpeg build, read from the embedded `FfmpegPackage.json`: version, two addresses, size and checksums. |
| `FfmpegInstaller` | Downloads that package, checks its size and SHA-256, and installs four files under fixed names. |
| `OutputFormat`, `RemuxRequest`, `RemuxResult`, `PreviewResult` and others (in `MediaModels.cs`) | The data of the services above. `RemuxResult.DurationsAgree` is the two-second rule that decides whether a download may be removed. |

### Files

| Type | What it does |
|---|---|
| `WorkingFiles` | The names of working files and what happens to them. `Sweep` finds leftovers of an interrupted save. `SettleDelivered` decides what becomes of a download once its easy-to-play file was attempted: deleted, or kept under a proper name. `CopyVerifiedAsync` copies a file and gives the copy its name only if its SHA-256 is the expected one. |
| `SessionCache` | The temporary folder of one session, with its lock file. It deletes only inside a folder it created, and `RemoveStale` removes the folders of sessions that ended without cleaning up. |

### Naming

| Type | What it does |
|---|---|
| `SavedFileNames` | `TryNormalizeOrderNumber` says whether an order number can be part of a file name. `Stem` builds the name from the order number and the title. `NextAvailablePath` returns the first name that is not taken. |

### Configuration

| Type | What it does |
|---|---|
| `AppPaths` | Every place where the application keeps something for the user, all below `%LOCALAPPDATA%\Diga`. |
| `AppSettings` | The settings as one record. It also holds the built-in Microsoft application ID (`BuiltInOneDriveClientId`), which is never written to the file. |
| `JsonSettingsStore` | Reads and writes `settings.json`. A write goes to a temporary file that then takes the place of the old one. `UpdateAsync` changes one value in the file as it is now. `LoadOrSetAsideAsync` renames a file that cannot be used instead of overwriting it. |

### Cloud

| Type | What it does |
|---|---|
| `CloudAuthService` | The sign-in: OAuth 2.0 authorisation code flow with PKCE, the browser, and a listener on the PC's loopback addresses. Also `RefreshAsync`, which renews a sign-in, and `DisconnectAsync`. |
| `ICloudTokenStore`, `ProtectedTokenStore` | Where sign-ins are kept: one file for each service and client ID, encrypted for the Windows user. |
| `CloudFolderLinkStore` | Where the link to a shared folder for uploads is kept: one encrypted file beside the sign-ins, under a name **Disconnect** does not match. An empty link removes the file. |
| `ProtectedFile` (internal) | What the two stores share: reading a file encrypted for the Windows user, and writing one through a temporary file that then takes the place of the old one. |
| `CloudUploadService` | Uploads one file through the service's resumable upload, in pieces of 5 MiB, and tries again after failures. `ValidateSessionUri` checks the upload address. |
| `CloudBrowseService` | The read-only list of cloud files (`ListAsync`) and the free space (`GetFreeSpaceAsync`), of the top folder or of a shared folder. `ResolveFolderAsync` asks OneDrive which folder a sharing link leads to; `TryParseFolderLink` and `ShareToken` say what counts as such a link and how it is written for Microsoft Graph. The service sends `GET` requests only. |
| `CloudErrorDetail` (internal) | Takes the error code and the first line of the message out of a service's error answer, and nothing else. |
| `CloudAccount`, `CloudProvider`, `CloudSignInExpiredException` and others (in `CloudModels.cs`) | The data of the services above. `CloudAccount.ToString` returns the display name, so that tokens cannot end up in a message or a log by accident. `CloudFolder` is a shared folder as OneDrive described it: the drive, the item, the name and the sharing link. `CloudAccount.SharedFiles` records that Microsoft gave the sign-in the files shared with the account too; `OAuthClientOptions.SharedFiles` asks for that. |

### Localization

| Type | What it does |
|---|---|
| `AppText` | Looks up a text by its key in the language in use (`T`), chooses plural forms (`Plural`), decides the language from the saved preference and the Windows language list (`Configure`, `ResolveLanguage`), and can tell whether a message is one of the catalog's own sentences (`IsCatalogText`). |
| `Resources/*.en.json`, `Resources/*.pl.json` | The text catalogs, embedded in the library. Four pairs: `Shell`, `Journey`, `Design`, `Core`. |

<a name="window"></a>
## The window

`Diga.App` contains one window class, `MainWindow`, and no XAML for it. The only XAML file is `App.xaml`, which holds the colours and styles for the light, dark and high-contrast themes.

### Files

| File | What is in it |
|---|---|
| `App.xaml.cs` | Start-up, and `LogException`, the one place that writes the error log. |
| `AppLocalization.cs` | Sets the language before any control is created. |
| `Links.cs` | Every address the application opens in the browser. |
| `MainWindow.cs` | The frame: navigation, banner, status bar, `RenderPage`, `RunOperationAsync`, `Describe`, and small builders such as `Card`, `ActionButton` and `Section`. |
| `MainWindow.Design.cs` | Window size, the row of steps at the top of a page (`RefreshJourney`), layout helpers, announcements for screen readers. |
| `MainWindow.Order.cs` | The **Order No** page and the rule that counts recordings under an order number. |
| `MainWindow.Dlna.cs` | The **Connect** card, the **Discover** and **Preserve** pages, the save sequence, preview and details. |
| `MainWindow.Library.cs` | **Discover** without a recorder, the preview player, the session's temporary folder, the readable form of the MediaInfo report, the free-space check. |
| `MainWindow.Export.cs` | The **Archive** page. |
| `MainWindow.Cloud.cs` | The cloud destination, upload, the **Cloud** page, connecting and disconnecting. |
| `MainWindow.Settings.cs` | Loading the settings at the start, the **Settings** page and saving it, the check of the media tools. |
| `MainWindow.Tools.cs` | Which FFmpeg is in use, the FFmpeg notice, the FFmpeg download. |

`MainWindow` is one `partial` class spread over these files. They share its fields.

### Pages are rebuilt, not updated

There are seven pages. Each has a key and one method that builds it:

| Navigation item | Key | Built by |
|---|---|---|
| **Order No** | `order` | `BuildOrderPage` |
| **Connect** | `source` | `BuildSourcePage` |
| **Discover** | `library` | `BuildLibraryPage` |
| **Preserve** | `export` | `BuildDlnaExportPage` |
| **Archive** | `complete` | `BuildCompletePage` |
| **Cloud** | `cloud` | `BuildCloudPage` |
| **Settings** | `settings` | `BuildSettingsPage` |

`RenderPage` creates the controls of the current page from scratch, in code, and puts them into the page area. There is no data binding and there are no view models. What the application knows is in the fields of `MainWindow`: the found devices, the current folder and its entries, the ticked recordings, the files saved in the session, the settings, the order number.

After every operation the page is built again from those fields. So a change of state never has to find and update a control; it only has to change a field. Three things keep a rebuilt page where the user left it:

- `RenderPageInPlace` restores the scroll position and gives the focus back to the control that was used. Controls are found again by their accessible name.
- `_sections` remembers which expandable sections are open.
- `_leavingPage` lets a page hand over what was typed and not yet saved. The **Settings** page uses it for its unsaved fields.

If building a page throws, `RenderPage` logs the fault and shows a card with the reason instead of ending the application.

### One operation at a time: `RunOperationAsync`

Everything that takes time goes through one method:

```csharp
private async Task RunOperationAsync(Func<CancellationToken, Task> operation, string success)
```

Searching, opening a folder, downloading, making an easy-to-play file, previewing, uploading, listing cloud files, signing in, downloading FFmpeg and saving the settings all use it. It does this:

1. If an operation is already running, it returns at once. There is never more than one.
2. It disables the navigation and the page, shows the progress bar and **Cancel**, and creates the cancellation token.
3. It asks Windows not to go to sleep while the operation runs.
4. It awaits the operation.
5. A cancellation that the user asked for ends with **Cancelled.** Any other exception is written to the error log and shown in the banner as **Action needed**, with the text that `Describe` makes of it.
6. It enables the page again and rebuilds it.

Inside an operation, `SetProgress` updates the status bar and `ShowBanner` shows a result. Closing the window during an operation asks for confirmation, cancels, and closes only after the operation has finished cleaning up.

Short actions that only change what the page shows, such as moving to another page or ticking a file, run in the control's own handler. A button made with `ActionButton` catches what its action throws and shows it in the banner.

### Messages

The library reports a failure by throwing an ordinary .NET exception whose message is a sentence from the text catalogs, in the user's language. `Describe` in `MainWindow.cs` turns any exception into what the user reads:

- a message that `AppText.IsCatalogText` recognises as the application's own is shown as it is;
- a message that Windows or .NET wrote, which is in English whatever the language of the window, is put into a sentence of the catalog as its technical detail;
- some Windows faults (access denied, folder missing, file in use, drive full) get a sentence of their own.

### Texts

Every text the user can see comes from the catalogs in `src/Diga.Core/Localization/Resources`. A source file that shows text starts with

```csharp
using L = Diga.Core.Localization.AppText;
```

and then asks for a text by its key:

```csharp
L.T("Journey.Dlna.Find")                          // a text
L.T("Journey.Dlna.Opening", device.FriendlyName)  // a text with a value
L.Plural("Journey.Dlna.Found", devices.Count)     // one of the keys ...Found.one, .few, .many, .other
```

| Catalog | Keys begin with | What it holds |
|---|---|---|
| `Shell` | `Shell.` | The frame of the window, **Settings**, cloud connections, FFmpeg notices, error sentences |
| `Journey` | `Journey.` | The pages of the five steps and the **Cloud** page |
| `Design` | `Design.` | The row of steps and the state lines in the navigation |
| `Core` | `Core.` | The messages of the library. The library uses these keys and no others. |

- **Language.** `AppLocalization.Initialize` runs before the first control exists. It takes the saved preference, or else the first supported language in the Windows language list, or else English. A changed language applies after a restart.
- **Numbers and dates** follow the regional settings of Windows, not the language of the texts.
- **A missing key** falls back to the English text, and then to the key itself. A test makes sure this does not happen.
- **Arrows.** Some labels end in `→` or `↗`. `SpokenLabel` removes the arrow from the name a screen reader says.
- **Not in the catalogs:** proper names and unit symbols that are the same in both languages, such as "AMG DIGA Archive", "OneDrive", "English", "Polski" and "MB".
- **Not the place for texts:** the two files `Strings/en-US/Resources.resw` and `Strings/pl-PL/Resources.resw` in `Diga.App`. Each holds a single entry, the name of its language.

<a name="flow"></a>
## One save, from click to file

This is the path of the save button on the **Preserve** page through the types. The button's label carries the number of ticked recordings, for example **Save 3 recordings**. This path is the longest sequence in the application, and the one where most of the rules below meet.

```text
MainWindow.Dlna.cs  ExportDlnaSelectedAsync            inside RunOperationAsync
  CheckAvailableSpace                                  enough room, before anything is fetched
  WorkingFiles.Sweep                                   leftovers of an interrupted save
  for each ticked recording: SaveDlnaPickAsync
    SavedFileNames.Stem, NextAvailablePath             the name; never one that is taken
    FetchDeliveredAsync
      WorkingFiles.CopyVerifiedAsync                   from the preview's copy, if this recording has one
      DlnaDownloadService.DownloadAsync                otherwise from the recorder
    exact copy:
      MediaInfoService.InspectAsync                    the technical details
    easy-to-play file:
      MediaInfoService.InspectAsync                    the technical details of the download
      RemuxService.RemuxAsync                          FFprobe, FFmpeg -c copy, FFprobe, rename
      RemuxResult.DurationsAgree                       may the download go?
      MediaInfoService.InspectAsync                    the technical details of the new file
      WorkingFiles.SettleDelivered                     delete the download, or keep it under a proper name
```

The window decides the order and what the user is told. Each step that touches a file is a method of the library, with one exception: an exact copy that was saved as `.bin` is renamed to the extension its content shows by the window itself (`SaveDlnaPickAsync`), without replacing a file.

<a name="rules"></a>
## Rules a change must keep

These are promises the application makes to its users. A change that breaks one of them is a defect, whatever else it achieves.

### 1. No re-encoding

Whenever FFmpeg reads a recording it is started with `-c copy`: in `RemuxService.BuildArguments` and in `PreviewService.PrepareAsync`. The only other start is `-version`, in the check of the media tools (`ValidateToolsAsync` in `MainWindow.Settings.cs`), which goes through `ProcessRunner` as well. No encoder is named anywhere in `src`. A recording that does not fit a file type is refused by `RemuxService.ValidateCompatibility`; it is never converted and no stream is dropped to make it fit. Do not add a fallback that encodes.

The tests generate their test video with encoders. That is test code only.

Tests: `MediaTests` (`CompatibleOriginalStreamsUseOnlyCopy`, `IncompatibleContainerCannotTranscode`, `Mp4RefusesSubtitleLoss`), `IntegrationTests` (`IncompatibleMp4DoesNotTranscodeOrCreateDestination`, `RecordingWithUnusualNameRemuxesWithIdenticalCompressedPayloads`).

### 2. Never overwrite

A file of the user is never replaced. The pattern is the same everywhere: write to a working file in the destination folder, then `File.Move(working, final, overwrite: false)`.

- `DlnaDownloadService`, `RemuxService`, `WorkingFiles.CopyVerifiedAsync` and `WorkingFiles.SettleDelivered` all end with that move.
- `SavedFileNames.NextAvailablePath` chooses a free name first, and the services check again.
- FFmpeg is run with `-n`.
- In the cloud, OneDrive is asked to rename on a name conflict, and Google Drive is only ever asked to create a file.

The rule is about the user's files. The application's own files are replaced when they are rewritten: `settings.json`, the sign-in files, the FFmpeg programs it manages and the previous error log.

Tests: `DlnaDownloadTests` (`ExistingDestinationIsNeverRequestedOrOverwritten`, `DestinationCreatedDuringDownloadWinsWithoutBeingOverwritten`), `MediaTests` (`ExistingDestinationIsNeverOverwritten`, `DestinationCreatedDuringExportIsNeverOverwritten`, `SourceCannotBeDestination`), `MediaSafetyReviewTests`, `SavedFileNamesTests` (`ExistingFilesAndFoldersAreNeverReplaced`), `WorkingFilesTests` (`AFileThatAlreadyHasTheNameIsNeitherReplacedNorRemoved`, `ADownloadThatMustBeKeptGetsAProperNameAndNeverReplacesAFile`), `CloudTests` (`ChunkUploadUsesProviderProtocolAndPreservesExistingCloudFiles`).

### 3. Nothing is written to the recorder

The library sends a recorder three things: the SSDP search message, HTTP `GET` requests (the description, a recording) and one SOAP action, `Browse`, which only reads. `DlnaContentDirectoryClient.CreateBrowse` is the only place that builds a SOAP request. Do not add another UPnP action, and do not add a request that changes a setting of the recorder.

No single test states this rule. `DlnaBrowseTests` and `DlnaCanonicalBrowseTests` fix the form of the one request that exists.

### 4. Local addresses only

Everything a device on the network sends is untrusted, including the addresses it supplies. The recorder code connects only to private and local addresses, and never follows a device to another host.

- `DlnaDiscoveryService.TryParseSearchResponse`: the address in a search answer must be the IPv4 address the answer came from, and that address must lie in the subnet of the adapter that received the answer (`IsFromLocalSubnet`).
- `DlnaDiscoveryService.SelectLocations`: at most 8 description addresses from one answering address and 64 in all, read in rounds. This does not stop a device that forges the sender of its answers from keeping the recorder out of the list, so nothing may take a device for the recorder because it is the only one listed. The window connects by itself only when one device is listed and one address answered, and shows the address of every entry.
- `DlnaDiscoveryService.DiscoverAtAsync` and `TryParseAddress`: an address typed by the user is asked only if it is an IPv4 address in the local ranges, written as four numbers.
- `EndpointPolicy.SameHost` and `Resolve`: every address from a description or a list must be on the recorder's own host.
- `EndpointPolicy.CreateClient` and `EndpointPolicy.ConnectLocalAsync`: a connection is opened only to a private, link-local or loopback address, whatever a name resolves to.
- `DlnaDownloadService.GetResponseAsync`: at most three redirects, same host only.

The test for "is this address local" exists once, as `EndpointPolicy.IsLocalAddress`. Do not add a second one.

Tests: `DlnaDiscoveryTests` (`SsdpResponseValidation`, `RejectsUnsafeAdvertisedEndpoints`, `DefaultTransportRejectsPublicLiteralAddressWithoutConnecting`), `DlnaBrowseTests` (`ExternalAndCredentialResourceUrlsRemainUnavailable`, `CancellationAndCrossHostDeviceAreRejectedBeforeNetwork`), `DlnaDownloadTests` (`UnsafeUrlsAreRejectedWithoutNetwork`, `DifferentHostRedirectIsNotFollowedEvenIfBothHostsAreLoopback`, `FourthRedirectIsRejected`).

### 5. Every user-visible text is in both catalogs

A new text gets a key in the `.en.json` and in the `.pl.json` file of the same catalog, with the same placeholders. A plural needs all four forms (`.one`, `.few`, `.many`, `.other`) in both languages. The library names only `Core.` keys.

Tests, all in `LocalizationTests`: `CatalogsHaveMatchingNonemptyKeysAndValidEquivalentPlaceholders`, `EveryTextKeyNamedInTheSourceExistsAndEveryCatalogKeyIsUsed`, `EveryPluralFamilyContainsAllCatalogForms`, `TheEnglishCatalogHoldsEnglish`. The second one also fails for a key that is left in the catalogs after its last use was removed.

The guides in `docs` quote labels from the catalogs word for word. When you change a label, search `docs` for the old wording.

### 6. Decisions about files live in the library, with tests

Because the window has no tests, code that decides whether a file is deleted, renamed, kept or replaced must not live in the window. It belongs in `Diga.Core` as a method with tests, and the window calls it. The existing examples:

| Decision | Method | Tests |
|---|---|---|
| What becomes of a download after its easy-to-play file was attempted | `WorkingFiles.SettleDelivered` | `WorkingFilesTests` |
| Which leftover files may be removed | `WorkingFiles.Sweep` | `WorkingFilesTests` |
| Whether a copy may take its name | `WorkingFiles.CopyVerifiedAsync` | `WorkingFilesTests` |
| Which temporary files and folders may be deleted | `SessionCache` | `SessionCacheTests` |
| Which name a file gets | `SavedFileNames` | `SavedFileNamesTests` |
| Whether two lengths agree, so that a download may go | `RemuxResult.DurationsAgree` | `SavedFileNamesTests` (`ContainerDurationMustAgreeWithItsSourceBeforeTheSourceIsRemoved`) |

The window itself touches two files directly: it deletes its own error log (`App.DeleteLog`), and it renames an exact copy from `.bin` to the extension its content shows (`SaveDlnaPickAsync`); that rename has no test.

<a name="conventions"></a>
## Conventions the code follows

These are not promises to users, but the code is consistent about them, and a change should be too.

- **A time limit is a failure, not a cancellation.** When a limit of the library runs out, the library reports a failure with a reason, in most places as a `TimeoutException`. `RunOperationAsync` treats an `OperationCanceledException` as a cancellation only when the user's own token was cancelled, and shows **Cancelled.** only then. `Describe` gives any other one the sentence that the other side did not answer in time.
- **Everything from the network is bounded:** the size of an answer, the number of entries and pages, the depth of XML, the length of a field, and the time.
- **Nothing happens on the network without an action of the user.** Constructors and the loading of settings open no connection. Loading the saved sign-ins at the start reads files only.
- **Secrets stay in one place.** Tokens and the Google client secret are only in `ProtectedTokenStore`, and the link to a shared folder, which can itself open the folder, only in `CloudFolderLinkStore`. They are not in `AppSettings`, and messages are built so that they cannot contain them (`CloudErrorDetail`, `CloudAccount.ToString`).
- **External programs get an argument list**, never a command line for a shell. Whenever FFmpeg or FFprobe is given a file to read, it is limited to local files with `-protocol_whitelist file,pipe`.
- **The application deletes only what it can recognise as its own:** by a name pattern and by the folder it is in.
- **One place for each fact.** The FFmpeg version, addresses and checksums are in `FfmpegPackage.json` and are read from there by the library, by `scripts/Get-Dependencies.ps1` and, through `scripts/New-Release.ps1`, by the installer. The addresses opened in the browser are in `Links.cs`. The version of the application is written twice: in `Directory.Build.props`, and in `installer/Diga.iss`, which repeats it as the default of `AppVersion`. `scripts/New-Release.ps1` reads the version from `Directory.Build.props` and passes it to the installer, which overrides that default; [Releasing](RELEASING.md) says to change both.
- **Guide links carry the version.** `Links.Document` builds addresses of the form `…/blob/v<version>/docs/<name>`, in Polish for the guides listed in `Links.Translated`. The application links to `RECORDER-SETUP.md` and to the headings `onedrive-with-your-own-registration`, `shared-folder` and `google-drive` of `CLOUD-SETUP.md`. Renaming one of these needs a change in the code.

<a name="tests"></a>
## Where the tests are

All tests are in `tests/Diga.Tests`. Tests marked `[Trait("Category", "Integration")]` need the real FFmpeg, FFprobe and MediaInfo in `tools/bin`, which `scripts/Get-Dependencies.ps1` downloads. All other tests need nothing but the .NET SDK.

No test contacts a real recorder, Microsoft or Google. The library is written so that tests can replace what touches the outside: the HTTP client of every service except `DlnaDownloadService` (which always builds its own client and is tested against a server on the PC itself), `IDlnaSsdpTransport` for the search, `ICloudTokenStore` for saved sign-ins, `IProcessRunner` for FFmpeg and FFprobe, and for the sign-in the functions that open the browser and choose the port. Tests that need a real connection open one on the PC itself.

| Area | Test files |
|---|---|
| Search, device descriptions, address rules | `DlnaDiscoveryTests.cs` |
| Folder lists: paging, limits, protected and converted marks | `DlnaBrowseTests.cs`, `DlnaCanonicalBrowseTests.cs`, `DlnaResponseTests.cs` |
| Download: checks of the answer, working file, sizes, time limits, redirects | `DlnaDownloadTests.cs` |
| Working files and leftovers | `WorkingFilesTests.cs` |
| The temporary folder of a session | `SessionCacheTests.cs` |
| File names. Also the preselected device and the two-second rule | `SavedFileNamesTests.cs` |
| Easy-to-play file: codec lists, FFmpeg arguments, checks, cleaning up | `MediaTests.cs`, `MediaSafetyReviewTests.cs` |
| FFmpeg download, and which copy of a tool is used | `FfmpegInstallerTests.cs` |
| Settings | `ConfigurationTests.cs` |
| Sign-in, saved sign-ins, upload | `CloudTests.cs`, `CloudAuthRegressionTests.cs`, `CloudResilienceTests.cs` |
| List of cloud files, free space | `CloudBrowseTests.cs`, `CloudResilienceTests.cs` |
| Texts and languages | `LocalizationTests.cs`, `CoreLocalizationTests.cs` |
| Integration: real FFmpeg, FFprobe and MediaInfo with generated video | `IntegrationTests.cs`, `CoreLocalizationTests.cs` (the class `CoreLocalizationMediaIntegrationTests`) |
| Integration: a stand-in recorder on the PC, from description to checked file | `DlnaIntegrationTests.cs` |
| Integration: the diagnostics script `scripts/Test-DlnaRecorder.ps1` | `DlnaDiagnosticScriptTests.cs` |

`ProcessRunnerProbe.cs` is not a test. It is the entry point of the test assembly, which one integration test and three tests of the default group start as a separate program.

What the tests cannot show is stated in [How it works](HOW-IT-WORKS.md#tested): they run against simulated recorders and simulated Microsoft and Google servers. The window is not tested automatically beyond the package check below.

[Testing](TESTING.md) describes each test file in more detail and says how to run the tests.

<a name="build"></a>
## Build, package and release

| Script or file | What it does |
|---|---|
| `scripts/Get-Dependencies.ps1` | Downloads the pinned FFmpeg (for the tests) and MediaInfo into `tools/bin` and checks their SHA-256. |
| `scripts/Build.ps1` | Dependencies, restore, the tests without the integration category, the integration tests, then the build of the window. |
| `scripts/New-Release.ps1` | Publishes the self-contained application, checks that the payload holds MediaInfo and no FFmpeg, compiles the installer, and makes the portable ZIP, the diagnostics kit, the source archive and the checksums. |
| `installer/Diga.iss` | The installer: a per-user installation without administrator rights, the optional FFmpeg download, and what the uninstaller removes. |
| `scripts/Test-Package.ps1` | The package check: starts the application from the portable folder and after an installation, looks for its window, and checks that uninstalling leaves no program, no FFmpeg and no saved sign-in behind. |
| `scripts/Sign-Files.ps1`, `scripts/Test-Signature.ps1` | Signing, when a signing identity is configured. |
| `.github/workflows/ci.yml` | On every push and pull request: one job for the tests without the integration category, one for the integration tests and the build of the window. |
| `.github/workflows/release.yml` | On a tag `v<version>`: build, test, package, check, and publish the release. |

The application is published unpackaged and self-contained: the .NET and Windows App SDK runtimes are part of its folder. `tools/bin` is copied into the application's `tools` folder without the FFmpeg programs.

[Releasing](RELEASING.md) and [Signing](SIGNING.md) describe the release procedure. [Building](BUILDING.md) and [Testing](TESTING.md) describe the way from a clone to a tested build.
