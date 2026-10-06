# Signing releases

For the maintainer. Users who want to check a download should read [Verifying your download](VERIFYING-DOWNLOADS.md).

Release files are signed with **Microsoft Azure Artifact Signing** (formerly Trusted Signing). The private key never leaves Microsoft's service, no secret is stored in GitHub, and the release workflow can sign only from one protected environment. Until the setup below is complete, releases are published unsigned and say so in their notes.

**What has been run.** The first form of the signing scripts, the Inno Setup integration and the verification steps (commit `70f37bb`) was run end to end with a self-made test certificate from the Windows certificate store. What was added since has not been run with any certificate: the division of the signed path into the jobs described under [How a signed release is made](#how-a-signed-release-is-made), signing the diagnostics script, the launcher that asks PowerShell to check that signature, and the publisher name that is now required. Of these, only what signs nothing was run, on one PC: the two packaging stages without a signing identity, the fetching of the compiler and of the signing tools against their recorded checksums, the refusal to sign while the publisher's name is missing, the rewriting of the launcher and of the kit's instructions, the comparison of the publisher's name (on files that other publishers had signed), and the check of the diagnostics kit beside a signed installer. For that check a program signed by another publisher stood in for the installer and was never started: a kit as an unsigned release has it, a kit with only its launcher changed and a kit whose script is unsigned were each refused. No kit has passed the check as a signed one, because none has been signed. The workflow in this form has not run on GitHub yet, and the Azure path has not been run at all: it needs the account described below. Expect to adjust details on the first signed release, and correct this page when you do.

## What signing gives, and what it does not

- Windows shows the publisher's name instead of *Unknown publisher*, and the files cannot be altered without breaking the signature.
- The name shown is the **legal name** that Microsoft validated. It cannot be chosen freely.
- SmartScreen's *Windows protected your PC* does not disappear at once. Reputation builds with downloads of files signed by the same publisher; Microsoft speaks of weeks.
- Signed are the project's own programs (`Diga.exe`, `Diga.dll`, `Diga.Core.dll`), the diagnostics script (`Test-DlnaRecorder.ps1`, in the application's `diagnostics` folder and in the diagnostics kit), the installer and its uninstaller. The .NET, Windows App SDK and MediaInfo files already carry their publishers' signatures and are left alone. FFmpeg is not distributed at all.

## Before you start

- **Eligibility.** Microsoft issues publicly trusted certificates to organisations in the EU, but to individuals only in the United States and Canada. The validation therefore has to be done for the registered business. Whether a Polish sole proprietorship passes the organisation validation is not confirmed by Microsoft; if it is refused, see [Another certificate](#another-certificate).
- **Cost.** The Basic account costs USD 9.99 a month and includes 5,000 signatures; one release uses six. Billing starts when the account is created and is not pro-rated, so create the account when you are ready to go through validation.
- **Subscription.** A paid (pay-as-you-go) Azure subscription is required; free and trial subscriptions are refused.

## One-time setup in Azure

1. **Register the resource provider.** Azure portal → your subscription → **Resource providers** → `Microsoft.CodeSigning` → **Register**.
2. **Create the signing account.** Search for **Artifact Signing Accounts** → **Create**. Name: 3 to 24 letters and digits. Region: for example Poland Central or West Europe; remember it, the endpoint depends on it. Pricing tier: **Basic**.
3. **Allow yourself to request validation.** On the account: **Access control (IAM)** → **Add role assignment** → **Artifact Signing Identity Verifier** → your own user.
4. **Identity validation.** On the account: **Identity validations** → **New identity** → **Organization**, **Public**. Enter the business exactly as it is registered. Microsoft may ask for documents; the decision takes from one to twenty business days.
5. **Certificate profile.** On the account: **Certificate profiles** → **Create** → **Public Trust**, choose the completed validation as the verified name. Name: 5 to 100 characters.
6. **An identity for GitHub.** Microsoft Entra ID → **App registrations** → **New registration** (any name, single tenant, no redirect address). Note its **Application (client) ID** and **Directory (tenant) ID**, and the **Subscription ID** of the subscription.
7. **Let that identity sign.** Open the certificate profile → **Access control (IAM)** → **Add role assignment** → **Artifact Signing Certificate Profile Signer** → select the app registration from step 6. Assign it on the profile, not on the whole subscription.
8. **Trust GitHub instead of a password.** In the app registration: **Certificates & secrets** → **Federated credentials** → **Add credential** → scenario **GitHub Actions deploying Azure resources**. Organisation `lukasz-gratkowski`, repository `AmgDigaArchive`, entity type **Environment**, environment name `release-signing`.

   The *subject* of that credential must equal what GitHub sends, character for character. Repositories created after 15 July 2026 send a subject that contains numeric IDs. Check before saving:

   ```powershell
   gh api repos/lukasz-gratkowski/AmgDigaArchive/actions/oidc/customization/sub
   gh api repos/lukasz-gratkowski/AmgDigaArchive --jq '.owner.id, .id'
   ```

   If `use_immutable_subject` is true, the subject is `repo:lukasz-gratkowski@<owner id>/AmgDigaArchive@<repository id>:environment:release-signing`; otherwise `repo:lukasz-gratkowski/AmgDigaArchive:environment:release-signing`. Edit the subject field by hand if the portal's wizard writes the other form. The first signed run prints the subject it actually presented, under *Federated token details* in the Azure sign-in step.

The endpoint for the next section is `https://<region code>.codesigning.azure.net`: `plc` for Poland Central, `weu` for West Europe, `neu` for North Europe.

## One-time setup in GitHub

Environments with protection rules are available once the repository is public.

1. **Settings → Environments → New environment**: `release-signing`.
   - **Deployment branches and tags**: **Selected branches and tags** → add a rule with **Ref type: Tag** and the pattern `v*`.
   - **Required reviewers**: add yourself. Every signed release then waits for one click of approval before the signing job starts. The tag rule above looks at the name of a tag, not at the commit it is on; the approval, and the workflow's own check that the tagged commit is on `main`, are what keep an unreviewed commit from being signed. Approve within a day: the application folder that the build job hands to the signing job is kept for one day only.
2. In that environment add these **variables** (they are names, not secrets):

   | Variable | Value |
   |---|---|
   | `AZURE_CLIENT_ID` | Application (client) ID from step 6 |
   | `AZURE_TENANT_ID` | Directory (tenant) ID |
   | `AZURE_SUBSCRIPTION_ID` | Subscription ID |
   | `DIGA_SIGN_ENDPOINT` | for example `https://plc.codesigning.azure.net` |
   | `DIGA_SIGN_ACCOUNT` | the signing account's name |
   | `DIGA_SIGN_PROFILE` | the certificate profile's name |
   | `DIGA_SIGN_PUBLISHER` | the validated legal name, character for character as the certificate shows it (*Issued to*, the name Windows displays as the publisher) |

   All four `DIGA_SIGN_…` variables are required. The publisher's name is what ties a release to this project: the files must be signed by exactly that name, so a signature made with another certificate profile does not pass.

3. **Settings → Secrets and variables → Actions → Variables**: add the *repository* variable `SIGNING_ENABLED` with the value `true`.

`SIGNING_ENABLED` is the switch. While it is not `true`, a tag is released unsigned. Once it is `true`, a tag is released signed or not at all: a signing failure stops the release.

## How a signed release is made

Nothing changes for the person releasing: push the tag ([Releasing](RELEASING.md)). The signing identity exists in one job only, and what that job downloads and runs is pinned: by a recorded checksum or, for the four GitHub actions it uses (checkout, artifact download, Azure sign-in, artifact upload), by commit.

1. **Release checks**, as for every tag: the version, the branch, the changelog.
2. **Build and test.** No signing identity: this job is outside the `release-signing` environment and may not ask GitHub for an identity token. It restores the packages, runs all tests, publishes the application folder, checks it (`scripts/New-Release.ps1 -Stage Publish`) and hands it to the next job.
3. **Sign and package**, the only job in the `release-signing` environment. In this order it
   - stops if one of the four `DIGA_SIGN_…` variables is empty;
   - takes the application folder from job 2;
   - fetches the Inno Setup compiler and the two signing tools, each accepted only with its recorded checksum, so that nothing is downloaded once the identity is live. The compiler is put in place by Inno Setup's own installer, which therefore also runs here, before the sign-in;
   - signs in to Azure with GitHub's identity token (OpenID Connect) and fetches the signing token straight away, because GitHub's token is valid for five minutes only;
   - runs `scripts/New-Release.ps1 -Stage Package`, which signs `Diga.exe`, `Diga.dll`, `Diga.Core.dll` and the diagnostics script, lets the Inno Setup compiler sign the uninstaller and the installer (all through `scripts/Sign-Files.ps1`), checks the installer, the three programs and the script with `scripts/Test-Signature.ps1` (valid for Windows, timestamped, issued to `DIGA_SIGN_PUBLISHER` and to nobody else), and writes the packages and their checksums.

   With the identity live, this job runs signtool, Microsoft's signing client, the Inno Setup compiler, Git, the Azure CLI, this repository's scripts and, as its last step, the GitHub action that uploads the finished files; the sign-in ends only when the job does. It restores no packages, runs no tests and starts neither FFmpeg nor the programs it has just signed.
4. **Install and start the signed files.** No identity again. It starts the application from the portable package, installs in English and Polish, lets the installer download FFmpeg, starts the installed application and uninstalls. `scripts/Test-Package.ps1 -RequireSignature` fails unless the installer, the installed uninstaller, the three programs and the diagnostics script, in the portable package and as installed, carry valid, timestamped signatures of one and the same publisher. It also opens the diagnostics kit and requires the same of the script in it, together with the launcher and the instructions that belong to a signed script.
5. **Publish**, with release notes that state what the installer's signature actually is.

If job 4 does not succeed, whether a check failed, the runner failed or the job was cancelled, a further job deletes the signed packages from the run at once: a validly signed build that the project's own checks have not accepted must not stay downloadable. What a failed job keeps for diagnosis is text only. The packages of a successful run are kept as a workflow artifact for one day; the release is the lasting copy.

Running a signed release again follows from that. After job 4, GitHub's **Re-run failed jobs** cannot work, because the packages that job would test are gone: use **Re-run all jobs**, which builds and signs again (six more signatures) and waits for a new approval. After a failed job 5 the packages are still there for a day, and **Re-run failed jobs** publishes the same files.

The certificates Microsoft issues live for three days, so the timestamp is what keeps a signature valid afterwards. A signature without a timestamp is treated as a failure everywhere.

### The diagnostics script

The diagnostics kit is the one part of a release that users are asked to run as a script. In an unsigned release its launcher, `Run-DlnaDiagnostics.cmd`, starts Windows PowerShell with the execution policy switched off for that one process (`-ExecutionPolicy Bypass`), because nothing else runs an unsigned script on a PC with the default policy. In a signed release the script is signed, and `scripts/New-Release.ps1` packages the launcher with `-ExecutionPolicy AllSigned` instead; where the kit's `README.md` shows how to start the script by hand, it makes the same replacement. PowerShell then runs the script only while its signature is valid, and the first time it asks whether to run software from this publisher. The launcher tells the user to compare the name with the release notes and to answer *Run once*.

Both forms of the launcher were tried with a stand-in script: with `Bypass` the script runs; with `AllSigned` PowerShell refuses an unsigned script, and the launcher explains exit code 1. A validly signed script under `AllSigned` has not been tried, for want of a certificate.

## Signing on your own PC

```powershell
az login
$env:DIGA_SIGN_ENDPOINT  = 'https://plc.codesigning.azure.net'
$env:DIGA_SIGN_ACCOUNT   = '<account>'
$env:DIGA_SIGN_PROFILE   = '<profile>'
$env:DIGA_SIGN_PUBLISHER = '<the validated legal name>'
./scripts/New-Release.ps1
```

Your own user needs the **Artifact Signing Certificate Profile Signer** role for this. `scripts/Sign-Files.ps1` downloads the two tools it needs (Microsoft's `signtool` and the Artifact Signing client, both from nuget.org and both checked against a recorded SHA-512) into `tools/signing`, which Git ignores. `scripts/New-Release.ps1` refuses to sign while `DIGA_SIGN_PUBLISHER` is empty.

## Another certificate

`scripts/Sign-Files.ps1` also signs with any code-signing certificate in the Windows certificate store, for example one on a hardware token or in a certificate authority's cloud service:

```powershell
$env:DIGA_SIGN_THUMBPRINT = '<SHA-1 thumbprint of the certificate in Cert:\CurrentUser\My>'
$env:DIGA_SIGN_TIMESTAMP  = '<the certificate authority''s RFC 3161 timestamp address>'
$env:DIGA_SIGN_PUBLISHER  = '<the name the certificate is issued to>'
./scripts/New-Release.ps1
```

This works only where the certificate is, so not on GitHub's runners: build and sign locally in a clean checkout and publish the files by hand.

## Trying the pipeline without a real certificate

A self-made certificate exercises everything except Windows' trust in the publisher. `DIGA_SIGN_TEST_THUMBPRINT` tells the verification to accept that one untrusted certificate; the release workflow never sets it.

```powershell
$test = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=DIGA signing test (not trusted)' -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddDays(2)
$env:DIGA_SIGN_THUMBPRINT = $test.Thumbprint
$env:DIGA_SIGN_TEST_THUMBPRINT = $test.Thumbprint
$env:DIGA_SIGN_TIMESTAMP = 'http://timestamp.digicert.com'
$env:DIGA_SIGN_PUBLISHER = 'DIGA signing test (not trusted)'
./scripts/New-Release.ps1
./scripts/Test-Package.ps1 -TestInstaller -RequireSignature
Remove-Item "Cert:\CurrentUser\My\$($test.Thumbprint)" -DeleteKey
```

Never publish files signed this way. The launcher of such a diagnostics kit does not run its script: PowerShell's `AllSigned` policy accepts only a signature that Windows trusts, which is the point of it.

## When signing fails

| What the log shows | Likely cause |
|---|---|
| `No matching federated identity record found` (AADSTS70021 or AADSTS700213) in the Azure sign-in step | The federated credential's subject differs from the one GitHub sent; compare with *Federated token details* in the same step |
| The job fails before it starts, saying the tag is not allowed to deploy | The environment's tag rule does not match the tag |
| `403 Forbidden` from signtool | Missing role on the certificate profile, wrong account or profile name, endpoint of another region, or the identity validation is not complete |
| signtool ends without any message | The .NET runtime the signing client needs is missing: version 8 or any later one. The signing job installs none and relies on the runner image, which has it |
| `signtool failed with exit code 2` | The signature was made but the timestamp server did not answer; the script tries three times |
| `Checksum mismatch for microsoft…` | nuget.org served a different package than the one recorded in `scripts/Sign-Files.ps1`; do not continue, find out why |
| `… the variable DIGA_SIGN_… of the release-signing environment is empty` | One of the four variables is missing in the environment, or was added as a repository variable or under another name |
| `… is signed by 'X' (…), not by 'Y'` | `DIGA_SIGN_PUBLISHER` is not exactly the name on the certificate, or the account and profile variables point at another certificate profile. The message shows both names |
| `artifacts/publish was published from version …, commit …` | The application folder handed over by the build job does not belong to the commit the signing job checked out; run the workflow again from the start |
| The signing job cannot download the artifact `application` | The approval came more than a day after the build job, and the application folder had expired; run the workflow again from the start |
| The job *Install and start the signed files* fails | The signed packages of that run have been deleted by the job after it, and nothing was published. If the cause lies outside the source (a download that failed, a fault of the runner), choose **Re-run all jobs** on the run's page and approve the signing job again. *Re-run failed jobs* fails at once here: it would test the packages that were deleted. If the cause is in the source, fix it and release the next version |
| The job *Publish* fails | Nothing was published, or a draft was left that the next attempt removes. The signed packages stay with the run for one day: within it, *Re-run failed jobs* publishes the same files. Later, only *Re-run all jobs* helps, with a new build, new signatures and a new approval |

## Keeping it working

- The account is billed monthly whether or not anything is signed.
- The tool versions and their SHA-512 values are recorded in `scripts/Sign-Files.ps1`; they are the pair Microsoft's own signing action uses. Change them deliberately, in a reviewed commit.
- The Inno Setup compiler, which writes the setup program and the uninstaller that are then signed, is pinned in `scripts/Get-InnoSetup.ps1` in the same way ([Releasing](RELEASING.md#pinned-inputs-and-how-to-change-them)).
- If the business name changes, the validation and the certificate profile have to be redone, and SmartScreen reputation starts again.
