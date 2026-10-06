# Releasing

For the maintainer. A release is made by pushing a version tag; everything else is done by the release workflow, and nothing reaches users unless every check passes.

## What runs when

**Every push and pull request** ([`ci.yml`](../.github/workflows/ci.yml)): the unit and protocol tests with a syntax check of the scripts, and a second job with the real media tools (FFmpeg, MediaInfo), the loopback recorder tests and the application build. Both jobs restore exactly the NuGet packages recorded in the lock files. Pull requests from forks get no credentials and cannot publish.

**A tag `vX.Y.Z`** ([`release.yml`](../.github/workflows/release.yml)):

1. **Check.** [`scripts/Test-ReleaseReadiness.ps1`](../scripts/Test-ReleaseReadiness.ps1) stops the run before anything is built unless
   - the tag is `v` followed by the version in `Directory.Build.props`;
   - the tagged commit is on `main`. A tag can be pushed on any commit, and only `main` is protected by the required checks;
   - `CHANGELOG.md`, `docs/VERIFYING-DOWNLOADS.md` and `README.md` are in the commit, and the README has the section *How far it has been tested*: the notes of every release link to them;
   - `CHANGELOG.md` has a heading that names the version;
   - the application (`src/Diga.App/Links.cs`) and the installer (`installer/Diga.iss`) name the repository the workflow runs in. They open its pages by name, so a release from a renamed repository or a fork would otherwise ship dead links.
2. **Package.** Builds, runs all tests, assembles the installer, the portable package, the diagnostics kit and the source archive, and checks that the application starts from the portable folder, that the installer installs in English and Polish, downloads and verifies FFmpeg, starts the installed application and uninstalls cleanly. A tag takes exactly one of two paths:
   - **unsigned**, while the repository variable `SIGNING_ENABLED` is not `true`: one job does all of it;
   - **signed**: three jobs, so that the signing identity exists only where files are signed. *Build and test* has no identity and hands over the application folder. *Sign and package* signs, compiles the installer and writes the packages; it restores no packages, runs no tests and starts neither FFmpeg nor the application. *Install and start the signed files* runs the same checks as the unsigned path, again without the identity, and requires the signatures. If it does not succeed, one more job deletes the signed packages from the run, and the run can then only be repeated as a whole (*Re-run all jobs*). [Signing](SIGNING.md) has the details.
3. **Publish.** Checks the files against their checksums, records a build attestation for them, writes the release notes from the installer's actual signature, and publishes. A published release is never replaced: a second run for the same tag succeeds only if its files are byte for byte the published ones.

**Run workflow** (manual start on a branch) packages without publishing. It is always unsigned; the check of step 1 only prints, as warnings, what would stop a tag on that commit. The files are kept as workflow artifacts for 30 days. For a tag the artifact is kept for one day, because the release is the lasting copy.

A packaging job that fails keeps text for diagnosis (what the start-up check wrote down, the installer's logs), never the packages it made.

**What has been run.** On one PC, on 5 October 2026, the unsigned path as the workflow runs it, on commit `b811dd4`: `New-Release.ps1` with the build and all tests, and the installer test in English and in Polish. Everything passed. In the Polish run the installer downloaded FFmpeg, unpacked it and installed the two programs, which the test compared with their recorded checksums and started. An earlier run of the same steps, on commit `2e20a56`, was made on a Windows account prepared like a developer's, with stand-ins for a downloaded FFmpeg of the same build, a saved sign-in, the logs, the settings and temporary files; every one of them was there unchanged afterwards.

After `b811dd4` the installer changed once more, in commit `63f22eb`: what an earlier version left behind is removed only when an earlier version is installed. On that commit the packaging was run again (`New-Release.ps1 -SkipBuild`), and the new installer was tried by hand, once: a first installation into a folder that already held a `docs` folder with a file of the user's own kept that file, a second installation over the first removed the stand-ins for an earlier version's documents and programs, and the uninstaller removed the application. The installer test in the two languages was not repeated on that commit, and the commits after it changed texts and documents only. The first run of the workflow on GitHub will repeat all of it. What the installer test does when something is in its way (a folder that cannot be set aside, an uninstaller that fails or is missing, a copy that an earlier run left installed, a diagnostics kit that does not fit the release) was run with a stand-in installer in a stand-in account only. The workflows in their present form (the release checks as a job of their own, the pinned Inno Setup compiler, the named runner image, the locked package restore, the three jobs of the signed path) have not run on GitHub yet. Correct this paragraph after the first run.

## Making a release

1. Set the version in `Directory.Build.props` and the same default `AppVersion` in `installer/Diga.iss`. Add a heading for the version to `CHANGELOG.md` (for example `## 0.6.0`) and, if the test status changed, update the section *How far it has been tested* in `README.md`.
2. Commit. In a clean clone or `git worktree` of that commit run the same steps the workflow runs:

   ```powershell
   ./scripts/Test-ReleaseReadiness.ps1
   ./scripts/Build.ps1
   ./scripts/New-Release.ps1 -SkipBuild
   ./scripts/Test-Package.ps1 -TestInstaller -InstallerLanguage en
   ./scripts/Test-Package.ps1 -TestInstaller -InstallerLanguage pl -WithFfmpegDownload
   ```

   `Test-ReleaseReadiness.ps1` without `-Tag` prints what a tag on this commit would be refused for. `New-Release.ps1` needs a Git clone, refuses a checkout with modified or untracked files, and fetches the pinned Inno Setup compiler into `tools/innosetup` the first time. The installer test refuses a Windows account that already has the application installed, and a disk with less than 1 GB free (0.5 GB without the FFmpeg download): on a full disk the installer installs the application but cannot write FFmpeg. It also opens the diagnostics kit that lies beside the installer and checks that its launcher fits the release, signed or unsigned.

   Uninstalling removes saved cloud sign-ins, a downloaded FFmpeg, the error log and the default folder for temporary files from the Windows account, so the test sets your own aside before the installer starts and puts them back at the end. Before the installer for two reasons: the installer downloads no FFmpeg when your FFmpeg folder already holds the same build, and a folder that cannot be set aside has to stop the test while nothing is installed yet. Windows does not rename a folder while a file in it is open (a log that another program is following, for example). The test then stops, names the folder and has installed nothing; close the program and run it again. Only the folder for temporary files is treated differently: one that a running copy of the application is using is left in place and left to the uninstaller. Close your own copy of the application for the test if you can, because its sign-ins and its log are out of its reach while the test runs. If a run is cut short, the folders it had set aside keep the ending `.smoke-…`; the next run stops and names them, so that you can give them their names back. If it was cut short with its copy still installed, the next run says where that copy is and how to remove it without losing your own folders.
3. Push, bring the commit to `main` and wait for CI.
4. Tag the tested commit and push the tag:

   ```powershell
   git tag -a v0.6.0 -m "AMG DIGA Archive 0.6.0"
   git push origin v0.6.0
   ```

The tag must equal the version (`v` + `Version`) and its commit must be on `main`, or the workflow stops at the first job. Never move or reuse a published tag; if a published release is wrong, fix it in a new version.

## What `New-Release.ps1` guarantees

- The source is exactly one commit: a clean tree before and after.
- The payload's `tools` folder holds exactly `MediaInfo.dll`, its two licence files and `dependencies.json`; `MediaInfo.dll` carries MediaArea's valid signature. FFmpeg is not in the payload.
- The documents in the payload are named one by one and must be tracked by Git: `LICENSE`, `THIRD-PARTY-NOTICES.md` and `README.md`, which must exist, and of the `docs` folder only the user guide, the cloud setup guide and the recorder setup guide in each language they have (`USER-GUIDE*.md`, `CLOUD-SETUP*.md`, `RECORDER-SETUP*.md`) and `PRIVACY.md`, as far as they are in the commit. Anything else in the payload's `docs` folder stops the build. The maintainer's notes and the screenshots do not ship.
- The .NET and Windows App SDK licence texts are in `licenses`.
- No file in the payload names the build account or the checkout folder (`scripts/Test-BuildPathLeak.ps1`). A guide may say where a Windows profile usually is, with a placeholder for the account name (`C:\Users\<your name>\`); the script lets exactly those spellings through. Release builds map source paths to `/_/…` for the same reason.
- With a signing identity, the project's own programs, the diagnostics script, the uninstaller and the installer are signed, and each signature is checked as soon as it is made: valid for Windows, timestamped, and issued to the name in `DIGA_SIGN_PUBLISHER`. Without that name nothing is signed. `Test-Package.ps1` checks the signatures again in the portable package, in an installed copy and in the diagnostics kit. Without an identity the release is unsigned and `BUILDINFO.json` says `"Signed": false`.
- The installer is compiled with exactly the Inno Setup version pinned in `scripts/Get-InnoSetup.ps1`; any other version fails the build.
- `BUILDINFO.json` in the payload records the version, the commit and its time, the .NET SDK that built the application, the .NET runtime and the Windows App SDK that are inside it, the Inno Setup version, the runner image when the build ran on GitHub, and whether the files were signed.
- The source archive is written by `git archive` from the commit itself, with the commit ID as the ZIP's comment. It holds what Git stores, with line endings as `.gitattributes` prescribes, whatever the build PC's Git settings are.
- `SHA256SUMS.txt` lists the four files. Finished files replace `artifacts/publish` and `artifacts/release` only when everything succeeded; the previous ones are kept under `…-previous-…`. Nothing removes those: every run adds about 0.4 GB, so delete them yourself when you no longer want them.

The script works in two stages, which the signed path of the workflow runs in separate jobs: `-Stage Publish` builds the application folder and checks it, `-Stage Package` signs, compiles the installer and writes the packages. The second stage accepts only a folder that the first stage made from the same commit. Without `-Stage` both run in turn.

## Building the same commit again

Whatever carries a date carries the commit's: `BUILDINFO.json`, the time of every file in the payload (which the installer records), and the entries of the portable package, the diagnostics kit and the source archive. The two ZIP files the scripts write list their entries in a fixed order. Each workflow names its runner image (`windows-2025`), so a new image is a change in the repository.

What was observed: on one PC, packaging the same commit twice without signing (once in the two stages, once in one run) gave four files with identical SHA-256 values, the installer included.

What is not promised: that a build on another PC or on GitHub's runner gives the same bytes. `global.json` accepts a later patch of the .NET SDK, text files are checked out with the line endings the PC's Git settings ask for unless `.gitattributes` names them, and a signature contains the time of signing, so two signed builds always differ. The publish job's "identical files are fine" case is therefore reached only by running that job again, not by a rebuild. The build attestation remains the link between the published files and the source.

## Pinned inputs and how to change them

Each pin is changed in a commit of its own, with the new checksum taken from a download you verified.

| Input | Where it is recorded |
|---|---|
| FFmpeg package: version, two addresses, SHA-256, size, folder name, fingerprints of the two programs | `src/Diga.Core/Media/FfmpegPackage.json` (read by the application, the installer build and `Get-Dependencies.ps1`) |
| MediaInfo library | `scripts/Get-Dependencies.ps1` |
| Signing tools | `scripts/Sign-Files.ps1` |
| Inno Setup compiler: version, address, SHA-256, name on its signature | `scripts/Get-InnoSetup.ps1` |
| GitHub Actions | commit hashes in `.github/workflows/*.yml` and `.github/actions/package/action.yml` |
| Runner image | `runs-on` in `.github/workflows/*.yml` |
| .NET SDK | `global.json` |
| NuGet packages | versions in the project files; the exact version and content hash of every package, direct or transitive, in the `packages.lock.json` beside each project; the one feed in `nuget.config` |

**NuGet packages.** `nuget.config` clears whatever feeds the PC's own NuGet settings name and leaves nuget.org as the only source. `scripts/Build.ps1` and CI restore in locked mode: a package whose version or content differs from the lock file fails the restore, and nothing after it restores again. A Release restore also fails when any package, direct or transitive, has a known vulnerability rated high or critical; a Debug build only warns. An ordinary `dotnet build` or a build in Visual Studio is not locked and rewrites the lock files when a package reference changed. The lock files do not cover what the .NET SDK adds on its own: the .NET runtime packs that are published with the application and the Windows SDK reference pack. Their exact versions belong to the SDK version, they come from nuget.org as well, and no checksum of them is recorded in the repository; `BUILDINFO.json` names the runtime version. After a deliberate change of a package:

```powershell
dotnet restore Diga.sln
git add src/Diga.App/packages.lock.json src/Diga.Core/packages.lock.json tests/Diga.Tests/packages.lock.json
```

**Inno Setup.** `scripts/Get-InnoSetup.ps1` downloads the installer of one exact version from Inno Setup's own GitHub releases, accepts it only with the recorded SHA-256 and a valid signature of the recorded publisher, and runs it in its portable mode, which copies the compiler into `tools/innosetup` inside the checkout and registers nothing in Windows. Git ignores the folder. The workflow and local builds use that copy and no other; an Inno Setup installed on the PC is not looked at. `-IsccPath` names another copy, which must report the same version. To change the pin, download the new installer yourself, check its signature, record version, address and SHA-256, and run the installer tests.

## Repository settings worth keeping

- **Rulesets**: on `main`, block force pushes and deletion and require the two CI checks; on tags `v*`, restrict creation to the maintainers who release, and block updates and deletion.
- **Advanced Security**: secret scanning with push protection, private vulnerability reporting, Dependabot alerts.
- **Releases → Enable release immutability**, after the first release made by this workflow has succeeded. Published files and their tag are then locked by GitHub itself.
- The `release-signing` environment restricted to tags `v*`, with a required reviewer ([Signing](SIGNING.md)).

## Honesty rule

Release notes, the README and the changelog say only what was actually run, and say who reported a result when it was not one of the project's own tests. A recorder or an account that was not tried is described as not tried.
