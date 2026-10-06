# Contributing

Thank you for considering it. This is a small project with one maintainer, so the most useful contributions are the ones that are easy to check.

## The most valuable help

- **Recorder reports.** The project owns no recorder. Whether the application works with a given model is known only from reports, so please open a “Recorder report” on the [issues page](https://github.com/lukasz-gratkowski/AmgDigaArchive/issues/new/choose), whether it worked or not. If the recorder is found but its folders will not open, run the [diagnostics kit](docs/DLNA-DIAGNOSTICS.md) and attach the ZIP file it makes; it contains no titles, names or addresses. [Testing](docs/TESTING.md#helping-with-a-recorder-report) says what makes a report useful.
- **Cloud reports.** Nobody has yet reported connecting or uploading through the application's new built-in Microsoft registration, and the project has never run Google Drive against Google's real servers or tried a work or school OneDrive account. The [README](README.md#how-far-it-has-been-tested) says exactly what is known. A short report of what happened with your kind of account helps. Leave out account addresses, tokens and client secrets.
- **Bug reports** with the exact message the application showed and the steps that led to it.
- **Translation fixes.** The English and Polish texts live side by side in `src/Diga.Core/Localization/Resources`.

Security problems are reported privately; see [SECURITY.md](SECURITY.md).

## Building and testing

You need Windows 11 (x64), the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (the exact version is pinned in `global.json`) and PowerShell 7. [Building](docs/BUILDING.md) has the full instructions; the short version is:

```powershell
git clone https://github.com/lukasz-gratkowski/AmgDigaArchive.git
cd AmgDigaArchive
./scripts/Build.ps1
```

`Build.ps1` downloads and checks the pinned media tools the tests need, runs the unit and integration tests and builds the application. To run only the fast tests, which need no downloads:

```powershell
dotnet test tests/Diga.Tests --filter "Category!=Integration"
```

[Testing](docs/TESTING.md) describes the two groups of tests, what each test file covers, and what no test covers.

### Trying the application without a recorder

`tests/fixtures/dlna_emulator.py` (Python 3, no packages needed) plays the part of a recorder on your own PC. Give it a small test video; [Building](docs/BUILDING.md#running-without-a-recorder-the-emulator) shows how to make one with FFmpeg.

```powershell
python tests/fixtures/dlna_emulator.py --media artifacts/dlna-demo.mpg --announce
```

With `--announce` the emulator answers the application's network search, so **Find network recorders** lists it as “DIGA demo recorder”. Its one folder holds a recording that can be saved, one of unknown conversion status, one advertised as protected and one advertised as converted. Without `--announce` the application cannot find it.

## Pull requests

- Keep a pull request to one subject, and say in its description exactly what you ran or tried. “Not tested on a recorder” is a perfectly good statement; an untested claim is not.
- Both CI jobs must pass.
- Every text the user can see goes into the catalogs, in **both** languages, with the same keys; the tests enforce this. A test also refuses a text that is the same in both languages, so the Polish file needs a Polish text. If you do not speak Polish, write the best Polish text you can and say so in the pull request, so that it is corrected in review. [Testing](docs/TESTING.md#the-catalog-tests) lists the rules the tests check.
- Match the code around your change: its naming, its comment density, its way of handling errors and cancellation.
- Add or adjust tests for behaviour you change. Tests must not contact a real recorder, Microsoft or Google; use the existing fake handlers and loopback fixtures.
- Do not commit recordings, sign-in tokens, client secrets, certificates or anything that names a person or an account.
- A change that users will notice gets a line in [CHANGELOG.md](CHANGELOG.md), under *Unreleased*.

## Documentation

- Documentation must not say that something was tested when it was not, and must say who reported a result when it was not the project's own test. The section *How far it has been tested* in the [README](README.md#how-far-it-has-been-tested) is the reference.
- What comes from Panasonic's, Microsoft's or Google's documentation is presented as coming from there, not as something this project verified.
- A label of the application (a button, a page, a field, a message) is quoted exactly as the catalog of the document's language has it, in bold, without the arrow that some labels end with. An English document quotes the English catalog, a Polish document the Polish one.
- The user guide, the recorder setup guide and the cloud setup guide exist in English and Polish. A change to one language needs the same change in the other.

## Licence of contributions

The project is licensed under the [GNU General Public License, version 3 or later](LICENSE). By submitting a contribution you agree that it is licensed under the same terms, and you confirm that you have the right to submit it.

## Forks and modified builds

A fork that publishes its own builds should register its own Microsoft application ID instead of reusing the built-in one (the constant `AppSettings.BuiltInOneDriveClientId`, at present `bfd21bf0-32a9-4520-8bbb-d525e3d34aea`); [Cloud setup](docs/CLOUD-SETUP.md#onedrive-with-your-own-registration) describes the registration. Google Drive never has a built-in client: Google's terms do not allow publishing one in open-source code, so each user creates their own.
