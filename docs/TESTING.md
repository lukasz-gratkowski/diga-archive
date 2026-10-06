# Testing

For contributors. This page says what the automated tests are, how to run them, what each group of test files covers, how to try the application by hand without a recorder, and what no test covers. Building is described in [Building](BUILDING.md).

## At a glance

- There is one test project, `tests/Diga.Tests` (xUnit). It tests the library, `src/Diga.Core`.
- The window, `src/Diga.App`, has no automated tests. It is compiled in CI and tried by hand.
- The tests fall into two groups: the default group (unit and protocol tests) and the group marked `Category=Integration`.
- No test contacts a real recorder, Microsoft or Google. Recorders and cloud services are simulated inside the tests. FFmpeg and MediaInfo are the real programs, run on generated video.
- What has and has not been tried on real hardware and with real accounts is at the end of this page. Please read it before you describe anything as tested.

## Running the tests

```powershell
# The fast group: no downloads needed
dotnet test tests/Diga.Tests --filter "Category!=Integration"

# The integration group: needs the pinned tools in tools/bin
./scripts/Get-Dependencies.ps1
dotnet test tests/Diga.Tests --filter "Category=Integration"

# Everything
dotnet test tests/Diga.Tests

# One class
dotnet test tests/Diga.Tests --filter "FullyQualifiedName~DlnaDownloadTests"
```

`./scripts/Build.ps1` runs both groups and then builds the application; see [Building](BUILDING.md#the-short-way). CI runs both groups in the `Release` configuration on every push and pull request, in two jobs.

### What each group needs

**The default group** needs Windows and the .NET SDK, nothing else.

- It opens network connections to this PC only: to `127.0.0.1` and `::1`, and in one test to the PC's own network address, where the connection must be refused.
- Its files go to new folders under the temporary folder (`%TEMP%`), which it removes again. The settings files and sign-in files that tests write are in those folders, not in `%LOCALAPPDATA%\Diga`, where the application keeps your settings and sign-ins.
- It uses the Windows encryption for the current account (DPAPI), so it runs on Windows only.
- Several tests start Windows PowerShell (`powershell.exe`), which is part of Windows, as a stand-in for a program that runs for a long time, goes silent or writes a file (`ProcessRunnerLimitTests.cs`, one test in `MediaTests.cs`). Three tests start the test assembly itself as a program.
- Some tests read files of the repository: the source files under `src`, to check that every text the code names exists, and the Markdown documents, to check their links. The tests must therefore run inside a checkout of the repository.

**The integration group** also needs:

- `ffmpeg.exe`, `ffprobe.exe` and `MediaInfo.dll` in `tools/bin`. `scripts/Get-Dependencies.ps1` puts them there and checks them. To use another folder, set the environment variable `DIGA_TOOLS` to it.
- Windows PowerShell 5.1, which is part of Windows. The tests of the diagnostic script run the script with it, because that is what users run it with.

No automated test needs Python. Python is needed only for the recorder emulator used when [testing by hand](#testing-by-hand-against-the-emulator).

### How many tests there are

About 720 test cases, of which 43 are in the integration group. This was counted on 5 October 2026: a run of the whole project reported 721, from 238 tests marked `[Fact]` and 81 tests marked `[Theory]` that run once for each of 453 `[InlineData]` rows and of 30 generated rows. The number changes with every pull request. The summary line at the end of a `dotnet test` run gives the real number, and this command lists the tests without running them:

```powershell
dotnet test tests/Diga.Tests --list-tests
```

### Tests that depend on timing

GitHub's hosted runner gave the test host two processors when this was last looked at (2 October 2026), and two tests are sensitive to that: the ones that cancel a running FFmpeg (`ActualProcessCancellationTerminatesFfmpeg` and `ActualProcessCancellationIsOnTimeWithTwoThreadPoolThreads`). They print when the cancellation was requested and when it took effect, and the diagnostic script tests print each request's time. If one of them fails on the runner and passes on your PC, repeat it with the runtime limited to two processors:

```powershell
$env:DOTNET_PROCESSOR_COUNT = 2
dotnet test tests/Diga.Tests --filter "Category=Integration"
Remove-Item Env:DOTNET_PROCESSOR_COUNT
```

Tests that change the display language belong to one xUnit collection, `Localization culture`, which xUnit does not run in parallel with other tests. They restore the language and regional settings of the process when they finish.

## What the test files cover

Files marked *integration* belong to the integration group; everything else is in the default group.

### The recorder (DLNA)

| File | What it covers |
|---|---|
| `DlnaDiscoveryTests.cs` | Answers to the network search and device descriptions: names, relative addresses, refusal of addresses on other hosts or outside the local network, XML with a DTD or external entities, XML nested too deeply, oversized answers, duplicate and malformed responders, one unusable device not hiding the others, redirects not followed, cancellation. In most tests the search is replaced by a stand-in; two send real search packets to a stand-in device on this PC only, so no test sends a search into your network. |
| `DlnaBrowseTests.cs` | Reading a folder: titles, sizes, paging with a known and an unknown total, inconsistent or endless paging, the page limit, protection and conversion marks, recording addresses that point elsewhere or carry credentials, a size of zero, time limits reported as failures while the user's own cancel stays a cancel, oversized fields. |
| `DlnaCanonicalBrowseTests.cs` | The spelling of the folder request. A simulated recorder refuses the older spelling with an empty HTTP 412 and accepts `u:Browse`. This models what the diagnostic script observed on 1 October 2026 on a recorder that its owner reports as a DMR-BS850. |
| `DlnaResponseTests.cs` | Empty answers, HTTP errors and SOAP faults: the status is kept and the message is in the chosen language, English or Polish. An empty folder is a valid answer. |
| `DlnaDownloadTests.cs` | Downloading a recording from raw HTTP and TLS servers on this PC: every byte kept and fingerprinted, unknown lengths, answers marked as protected or converted refused before any file exists, documents and HTML pages not saved as recordings, partial and empty answers, data that ends early (the incomplete file is removed), sizes that disagree, cancellation, time limits, an existing file never replaced, redirects only to the same recorder and at most three, addresses outside the local network, certificate checking not switched off, file extensions. |
| `DlnaDiagnosticScriptTests.cs` *(integration)* | Runs `scripts/Test-DlnaRecorder.ps1` with Windows PowerShell 5.1 against a recorder simulated on this PC. Its request must be byte for byte the application's request. Its report must keep HTTP statuses and must not contain titles, device identifiers or address tokens. Redirects, DTDs and other hosts are refused. |
| `DlnaIntegrationTests.cs` *(integration)* | The whole path against a recorder simulated on this PC, with the real FFmpeg and MediaInfo: read the folder, download, compare the SHA-256, make an MKV, an MPEG and an MP4 file, compare them with the download, read both with MediaInfo. An MP4 that cannot hold the recording leaves the exact copy untouched. |

### Saving and files

| File | What it covers |
|---|---|
| `MediaTests.cs` | How FFmpeg is called and checked, with a stand-in for the programs: streams are only copied; a file type that cannot hold a stream is refused and nothing is converted; subtitles are not dropped silently; an existing file is never replaced; a failed or cancelled run leaves no partial file; a read error fails the run although FFmpeg exits with zero; the result is checked before it gets its name; the wording of error messages. |
| `MediaSafetyReviewTests.cs` | A save cannot write through a hard link to its own source. |
| `IntegrationTests.cs` *(integration)* | The real FFmpeg, FFprobe and MediaInfo on generated video: MKV and MPEG files with identical compressed packets, also for file names with non-ASCII characters, spaces and brackets; H.264 with AAC into MP4; MPEG-2 into MP4 refused without a file; MediaInfo reads the source and the result; cancelling ends FFmpeg, also when only two worker threads are available. |
| `ProcessRunnerLimitTests.cs` | The time limits for FFmpeg and FFprobe, with Windows PowerShell as a stand-in program: a program that runs longer than its limit, goes silent or only repeats its last progress line is ended; one that moves on is not; a program does not outlive the process that started it. |
| `ProcessRunnerOutputTests.cs` | What is kept of a program's messages: the line that names the failure survives more output than is kept, and a file name outside ASCII arrives unchanged. |
| `ProcessRunnerProbe.cs` | Not a test. It is what the test assembly does when started as a program, used by the two-worker-thread test above, by `ProcessRunnerOutputTests.cs` and by the test that a program does not outlive the process that started it. |
| `SavedFileNamesTests.cs` | Order numbers and file names: which order numbers are accepted, the order number alone or as a prefix, titles with dots and slashes, long names, existing files and folders never replaced. Also which device is preselected after a search, and the rule on lengths that decides whether a download may be removed after its easy-to-play file was made. |
| `WorkingFilesTests.cs` | The working files of a save: which names are the application's own, what is cleared away after an interrupted save, that a copy gets its real name only when its fingerprint is the expected one, and how a download that must be kept gets a proper name. |
| `SessionCacheTests.cs` | The folder for temporary files: the lock that marks it as in use, what is deleted, and that folders of ended sessions are removed and nothing else. |
| `FfmpegInstallerTests.cs` | The FFmpeg download, against simulated servers and small stand-in packages: checksum and size, the second address, an address that is not HTTPS never contacted, cancel and stall, only the two programs and their licence texts installed, nothing written outside the folder. Also where the application looks for its media tools. |

### Cloud

| File | What it covers |
|---|---|
| `CloudTests.cs` | Sign-in (the PKCE test vector of RFC 7636, the state check, the account chooser, the address Google expects, a busy port), renewing and storing a sign-in encrypted, the upload protocol of both services against simulated answers (parts, resuming after a failure, the limit on retries, a full drive, a lost session), which upload servers are accepted, and that an error shows only the fields the service names as its error. |
| `CloudAuthRegressionTests.cs` | The Google client secret survives sign-in and renewal and is stored encrypted; a cancelled sign-in opens no browser. |
| `CloudResilienceTests.cs` | When things are not as they should be: a saved sign-in that cannot be decrypted, a sign-in nobody finishes, free space in the drive, the sign-in listening on this PC only, disconnecting, answers in an unexpected shape, an upload waiting out an interruption, a sign-in the service has ended. |
| `CloudBrowseTests.cs` | The read-only list of cloud files: paging, the next page requested only from the service's own server, limits on pages, size and time, refusals, ended sign-ins, throttling, odd entries, and that only GET requests are sent. |

### Settings and texts

| File | What it covers |
|---|---|
| `ConfigurationTests.cs` | The settings file: defaults, saving and loading, files written by earlier versions, the built-in OneDrive application ID used unless the user enters one and never written to the file, the cloud destination, a file that cannot be used set aside instead of overwritten, cancelled and simultaneous writes, validation. |
| `LocalizationTests.cs` | Language selection, plural rules and the text catalogs; see the next section. |
| `RepositoryConsistencyTests.cs` | What the application and the documents say about each other: every guide the application opens exists, with its anchors, in each language it is offered in; every link between the Markdown documents leads to a file and a heading that exist. |
| `CoreLocalizationTests.cs` | With the Polish interface, everything sent to a recorder, to FFmpeg or to a cloud service stays in its technical form (numbers, codec names, headers). One class *(integration)* runs a real preview and a real save with the Polish interface and Polish and German regional settings. |

## The catalog tests

Every text the application shows is in the catalogs under `src/Diga.Core/Localization/Resources`: `Shell`, `Journey`, `Design` and `Core`, each in English (`.en.json`) and Polish (`.pl.json`). At the time of writing there are 706 keys per language. `LocalizationTests.cs` keeps the catalogs and the code in step:

| Test | What fails it |
|---|---|
| `CatalogsHaveMatchingNonemptyKeysAndValidEquivalentPlaceholders` | A key that exists in one language only; an empty text; a text that is not a valid format string; a placeholder (`{0}`, `{1}`) that one language has and the other has not. |
| `EveryTextKeyNamedInTheSourceExistsAndEveryCatalogKeyIsUsed` | A key named in a `.cs` file under `src` that the catalogs do not have; a key in the catalogs that no source file names; the library `Diga.Core` naming a key that does not begin with `Core.` |
| `TheEnglishCatalogHoldsEnglish` | Polish letters in an English text; an English text that is word for word the Polish one. The test lets through a text without a word of three or more letters, and the words `Format`, `MENU`, `Folder` and `min`, which both languages share. |
| `EveryPluralFamilyContainsAllCatalogForms` | A text that depends on a number without all four forms `.one`, `.few`, `.many` and `.other`, in both languages. |
| `BothLanguagesAreEmbeddedAndHaveNoDuplicateJsonKeys` | The same key twice in one language; a catalog that is not built into the library. |
| `ASentenceOfTheCatalogIsRecognisedWhateverFillsItsPlaceholdersAndASystemMessageIsNot` | A catalog sentence that the application would not recognise as its own. The window uses this to tell its own messages, which are already in the user's language, from messages of Windows or .NET. |

When you add or change a text:

- Add the key to both files of the right catalog, with the same placeholders in both. The Polish file needs a Polish text: `TheEnglishCatalogHoldsEnglish` refuses a text that is the same in both languages. If you do not speak Polish, write the best Polish text you can and say so in the pull request, so that it is corrected in review.
- Name the key in the code as a plain string in quotation marks. The test finds keys by looking for such strings.
- For a text that depends on a number, add all four forms and call `AppText.Plural`; the number is `{0}`.
- Remove a key that nothing uses any more, from both files.
- Run the default group. It includes these tests.

## Testing by hand against the emulator

The emulator lets you walk through the whole application without a recorder. [Building](BUILDING.md#running-without-a-recorder-the-emulator) says how to make a test video, start the emulator and connect to it. From there, these are worth trying after a change to the window:

1. On **Order No**, type something into **Order number (optional)**, for example `TEST-1`, and choose **Continue to Connect**. Try a name Windows does not allow, such as `a:b`; the page must refuse it.
2. On **Connect**, **Find network recorders** must list the emulator, or connect to it at once when it is the only device that answered the search. Then stop the emulator and search again. If no other media server answers in your network, the message **No recorder was found** must appear and the checklist **If your recorder is not found** must open. Start the emulator again, search once more and connect before you go on: without `--port` the emulator listens on a new port each time it starts, so the earlier connection no longer answers.
3. On **Discover**, open “Demo recordings”. Two recordings are in the list and can be ticked. The two that cannot be saved are named below the list, each with its reason. Tick the first one and choose **Download and show details**. With FFmpeg present, try **Download and preview**. Whether the clip plays depends on the codecs Windows has. If Windows cannot play it, the message **Windows could not play this video** must appear; that message is then the right behaviour, not a fault.
4. On **Preserve**, an exact copy: choose **Exact copy, as the recorder delivers it**, a test folder under **Save to this folder**, and **Save 1 recording**. Then compare the fingerprints. They must be equal:

   ```powershell
   Get-FileHash artifacts/dlna-demo.mpg, 'C:\path\to\the\saved file.mpg' -Algorithm SHA256
   ```

5. On **Preserve**, an easy-to-play file: this needs FFmpeg. A recording saved in this session is unticked and marked as saved, so tick it again on **Discover** first. Choose **The same picture and sound in an easy-to-play file** and the **File type** that begins **MKV**. One file must be saved, and no second file must stay beside it. Then tick the recording once more and choose **MP4 · not for every recording**: the test video is MPEG-2 with MP2 sound, which the application does not put into an MP4 file, so the message must say that the recording was kept as an exact copy instead.
6. On **Archive**, each saved file is listed with the sentence that says how it was checked. Open **Technical details · recording and saved file** and try **Show in folder**.
7. A recorder that is gone: on **Discover**, choose **Refresh folder** and tick both recordings that can be saved. Stop the emulator with Ctrl+C, and only then save. The message must be **Saved 0 of 2 recordings**, with the reason for each, and both recordings must still be ticked. The refresh matters: without it, a recording that was downloaded for its details or its preview is saved from that download, and the recorder is not asked.
8. Closing during work: close the window while something is running, for example during a search, which takes a few seconds. The question **Stop and close?** must appear.
9. Both languages: in **Settings**, change **Application language**, choose **Save preferences** and start the application again. Look for texts that are cut off or still in the other language.
10. The keyboard: go through one page with Tab, Shift+Tab, Enter and the arrow keys only.

Use a test folder for saved files. The application never replaces a file, but a development build uses your real settings; see [Building](BUILDING.md#what-a-development-build-shares-with-an-installed-copy).

A walk-through against the emulator shows that the application works against the emulator. It says nothing about a real recorder: the emulator answers at once, has one folder and serves one small file.

There is no emulator for the cloud. Connecting, uploading and the **Cloud** page can be tried only with a real Microsoft or Google account; say in your pull request which kind of account you used, and never paste a token, a client secret or an account address.

## What the automated tests do not cover

**The window.** Nothing in `src/Diga.App` is run by a test. Pages, layout, focus, keyboard use, what a screen reader says, Windows contrast themes, display scaling and how the two languages fit on screen are checked by hand, if at all. CI only compiles the window. The release procedure also checks that the packaged application starts and creates a window, and that the installer installs and uninstalls; it does not look at any page ([Releasing](RELEASING.md)). No check with a screen reader, with a contrast theme or at several display scalings is recorded for version 0.6.0; the accessibility changes in that version were made from reading the code.

**Real recorders.** The project owns no recorder. There is one report: the owner of a recorder reported as a DMR-BS850 confirmed on 2 October 2026 that version 0.5.2 found the recorder, opened its folders and saved recordings both as exact copies (`.mpg`) and as MKV. The report did not say which recordings were chosen or how the saved files were checked. Version 0.6.0 has run from start to finish only against the project's recorder emulator. Other recorder models, protected recordings on a real recorder and real network conditions are not covered.

**Real cloud accounts.** The sign-in, upload and listing code runs in the tests against simulated Microsoft and Google servers only.

- OneDrive: the same owner reported on 2 October 2026 that connecting with the built-in registration and uploading worked in version 0.5.2. That report was made with the earlier built-in Microsoft registration. On 5 October 2026 the application got a new built-in registration (application ID `bfd21bf0-32a9-4520-8bbb-d525e3d34aea`, the constant `AppSettings.BuiltInOneDriveClientId`). Nobody has reported connecting or uploading through the new registration yet. The only thing checked for it is that Microsoft's sign-in service knows the ID and accepts the `http://localhost` redirect; this was checked without signing in. A sign-in saved with the earlier built-in ID is not used by the new one: the user connects once more.
- Google Drive has never been run against Google's real servers by the project.
- Work or school OneDrive accounts are untested.

**Real sizes and durations.** The generated test videos are a few seconds long. Downloads, saves and uploads of recordings of several gigabytes, and work that runs for hours, are not part of any automated test.

**The real network search.** Most tests replace the search by a stand-in. A few send real search packets, but only to a stand-in device on this PC (loopback). A search in a real network, and asking a recorder by its address, are tried only by hand, with the emulator or a recorder.

**Signing.** Releases are not signed yet, and the test project has no test of signing. According to [Signing](SIGNING.md), the signing scripts were run with a self-made test certificate; the Azure Artifact Signing path has not been run.

## Rules for new tests

- A test must not contact a real recorder, Microsoft, Google or any other server. Use the stand-ins that exist: the fake HTTP handlers, the servers on `127.0.0.1`, the stand-in for the network search and for FFmpeg.
- Use generated video only. Never commit a recording.
- A test that needs the real FFmpeg, FFprobe or MediaInfo is marked `[Trait("Category", "Integration")]`.
- A test that changes the display language joins the collection `Localization culture`.
- Add or adjust tests for behaviour you change, and say in the pull request which groups you ran.

## Helping with a recorder report

A report from a real recorder is the most useful test this project can get, whether the application worked or not.

1. Install the application on a PC in the same home network as the recorder; see the [user guide](USER-GUIDE.md) and [Recorder setup and troubleshooting](RECORDER-SETUP.md).
2. Try the steps you can: find the recorder, connect, open a folder, save one recording as an exact copy, save one as an easy-to-play file, play the saved files in a media player.
3. Open a “Recorder report” on the project's [issues page](https://github.com/lukasz-gratkowski/AmgDigaArchive/issues/new/choose). The form asks for the model and region (the model is printed on the back of the recorder), the application version, what worked, and details.

The details make a report useful:

- What kind of recordings you tried: broadcast in SD or HD, copied from a camera.
- Anything the application refused, with the exact message.
- How you checked a saved file: that it plays, that its length is right, that the picture and sound are complete.
- If something failed: run the [diagnostics kit](DLNA-DIAGNOSTICS.md) on a PC in the recorder's network and attach the ZIP file it makes. It contains no titles, names or addresses, and it downloads no recording.

Please do not attach recordings, and leave out programme titles if you would rather not share them.

A report about the cloud is just as welcome: the kind of account (personal, work or school OneDrive, or Google), whether you used the built-in registration or your own, and what happened. Use the form “Something does not work” on the same page if it failed. Leave out account addresses, tokens and client secrets.

A report is recorded as what it is: a report from a named kind of recorder or account, not a test made by the project.
