<#
.SYNOPSIS
Fails unless the checked-out commit may be released under the given tag.

.DESCRIPTION
The release workflow runs this first, before anything is built or signed. It checks what cannot be repaired afterwards,
because a published release is never replaced:

  - the tag is "v" followed by the version in Directory.Build.props;
  - the commit is on the main branch. A tag can be pushed on any commit, and only main is protected by the required checks;
  - the documents that the notes of every release link to are in this commit;
  - CHANGELOG.md has a heading for this version;
  - on GitHub: the application and the installer name this repository in the links they open.

Without -Tag nothing is going to be published (a manual run of the workflow, or a rehearsal on your own PC before tagging),
so the same findings are printed as warnings and the branch is not looked at.
#>
[CmdletBinding()]
param([string]$Tag = '', [string]$MainBranch = 'origin/main')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be a three-part numeric version.' }
$changes = & git -C $root status --porcelain
if ($LASTEXITCODE -ne 0) { throw 'This is not a Git checkout.' }
if ($changes) { throw 'The checkout has modified or untracked files.' }
$problems = [Collections.Generic.List[string]]::new()
if ($Tag) {
    if ($Tag -cne "v$version") { $problems.Add("The tag is $Tag, but Directory.Build.props says $version; the tag must be v$version.") }
    $null = & git -C $root rev-parse --verify --quiet "$MainBranch^{commit}"
    if ($LASTEXITCODE -ne 0) { throw "$MainBranch is not known in this clone. The check needs the whole history (fetch-depth: 0)." }
    & git -C $root merge-base --is-ancestor HEAD $MainBranch
    if ($LASTEXITCODE -eq 1) { $problems.Add("This commit is not on $MainBranch. A release is made only from a commit that has reached main.") }
    elseif ($LASTEXITCODE -ne 0) { throw "Git could not compare this commit with $MainBranch." }
}
# .github/workflows/release.yml links to these three in "Describe the release"; keep the two lists equal.
foreach ($document in 'CHANGELOG.md', 'docs/VERIFYING-DOWNLOADS.md', 'README.md') {
    if (-not (& git -C $root ls-files -- $document)) { $problems.Add("$document is not in this commit, and the release notes link to it.") }
}
$readme = Join-Path $root 'README.md'
if ((Test-Path -LiteralPath $readme) -and (Get-Content -LiteralPath $readme -Raw) -notmatch '(?m)^#{1,6}\s+How far it has been tested\s*$') {
    $problems.Add('README.md has no heading "How far it has been tested", and the release notes link to that section.')
}
$changelog = Join-Path $root 'CHANGELOG.md'
# A heading that names the version as a whole: "## 0.6.0", "## [0.6.0] - 2026-10-03", "## v0.6.0"; not 0.6.0.1, 10.6.0 or 0.6.0-rc1.
if ((Test-Path -LiteralPath $changelog) -and (Get-Content -LiteralPath $changelog -Raw) -notmatch ('(?m)^#{1,6}\s.*(?<![\w.\-])v?' + [regex]::Escape($version) + '(?![\w.\-])')) {
    $problems.Add("CHANGELOG.md has no heading for version $version.")
}
# The application and the installer open pages of the repository by its name. Released from a repository of another name,
# a renamed one or a fork, those links would be dead. On GitHub the workflow knows which repository it runs in.
if ($env:GITHUB_REPOSITORY) {
    $expected = "https://github.com/$($env:GITHUB_REPOSITORY)"
    foreach ($place in @(@{ File = 'src/Diga.App/Links.cs'; Pattern = 'Project\s*=\s*"(?<address>[^"]+)"' }, @{ File = 'installer/Diga.iss'; Pattern = '(?m)^AppPublisherURL=(?<address>\S+)' })) {
        $found = [regex]::Match((Get-Content -LiteralPath (Join-Path $root $place.File) -Raw), $place.Pattern)
        if (-not $found.Success) { $problems.Add("$($place.File) no longer names the repository where this check looks for it.") }
        elseif ($found.Groups['address'].Value -ne $expected) { $problems.Add("$($place.File) names $($found.Groups['address'].Value), but this repository is $expected; the links the application opens would lead elsewhere.") }
    }
}
if (-not $problems) { Write-Host "Version $version can be released from this commit$(if ($Tag) { " as $Tag" })."; return }
if ($Tag) { throw "This commit cannot be released as $($Tag):`n - $($problems -join "`n - ")" }
# GitHub shows a line that starts with ::warning:: on the run's summary page.
foreach ($problem in $problems) { if ($env:GITHUB_ACTIONS) { Write-Host "::warning::$problem" } else { Write-Warning $problem } }
Write-Host "Nothing is being published now. A tag v$version on this commit would be refused for the reasons above."
