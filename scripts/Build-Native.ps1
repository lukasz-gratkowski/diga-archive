[CmdletBinding()]
param([string]$GccPath = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $GccPath) {
    $candidates = @('C:/msys64/mingw64/bin/gcc.exe', 'C:/msys64/ucrt64/bin/gcc.exe')
    $GccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $GccPath) { $GccPath = (Get-Command gcc.exe -ErrorAction Stop).Source }
}
$savedPath = $env:PATH
$env:PATH = (Split-Path $GccPath -Parent) + ';' + $savedPath
$native = Join-Path $root 'tools/bin/native'
$upstream = Join-Path $root 'third_party/panasonic-rec'
New-Item -ItemType Directory -Force $native | Out-Null
if (-not (Test-Path "$upstream/meihdfs/extract/extract_meihdfs.c")) {
    throw 'Initialize pinned upstream sources: git submodule update --init --recursive'
}
function Invoke-Compile([string[]]$CompilerArguments) {
    & $GccPath @CompilerArguments
    if ($LASTEXITCODE -ne 0) { throw "Native compilation failed ($LASTEXITCODE)." }
}
Push-Location $root
try {
    # Compile the pinned author's C sources, without upstream's obsolete -m32/-march=i486 flags.
    $common = @('-std=gnu99','-O2','-static','-DWIN32','-D_FILE_OFFSET_BITS=64')
    Invoke-Compile ($common + @('-o',"$native/extract_meihdfs.exe","$upstream/meihdfs/extract/extract_meihdfs.c"))
    # libudf 2008 contains an unused inline reference to cdio_warn without a declaration.
    # Keep it visible as a warning when compiling this historical source with modern GCC.
    $udf = "$upstream/udf/pana-udf"
    Invoke-Compile ($common + @('-Wno-error=implicit-function-declaration',"-I$udf",'-o',"$native/udf_dump.exe","$udf/udf_file.c","$udf/udf_fs.c","$udf/udf_time.c","$udf/udf_dump.c"))
    foreach ($variant in @(@{ Directory='meihdfs/dvd-vr-meihdfs'; Name='dvd-vr-meihdfs' },@{ Directory='udf/dvd-vr-orig'; Name='dvd-vr-udf' })) {
        $source = Join-Path $upstream $variant.Directory
        Invoke-Compile ($common + @('-DMINGW','-DVERSION="0.9.8b"',"-I$source/mingw",'-o',"$native/$($variant.Name).exe","$source/dvd-vr.c","$source/mingw/sys/mman.c"))
    }
    if (Test-Path -LiteralPath (Join-Path $upstream '.git')) {
        $commit = & git -C $upstream rev-parse HEAD
        if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve upstream source pin.' }
    } else {
        $sourceInfoPath = Join-Path $root 'SOURCEINFO.json'
        if (-not (Test-Path -LiteralPath $sourceInfoPath)) { throw 'Source archive pin manifest is missing (SOURCEINFO.json).' }
        $sourceInfo = Get-Content -LiteralPath $sourceInfoPath -Raw | ConvertFrom-Json
        $commit = [string]$sourceInfo.PanasonicCommit
        if ($commit -notmatch '^[0-9a-fA-F]{40}$') { throw 'Source archive Panasonic commit pin is invalid.' }
    }
    $compiler = (& $GccPath --version | Select-Object -First 1)
    @{ Upstream='https://github.com/leecher1337/panasonic-rec'; Fork='https://github.com/lukasz-gratkowski/panasonic-rec'; Commit=$commit; Compiler=$compiler } |
        ConvertTo-Json | Set-Content "$native/build-info.json" -Encoding utf8
    foreach ($exe in Get-ChildItem $native -Filter '*.exe') {
        $info = [Diagnostics.ProcessStartInfo]::new($exe.FullName)
        $info.UseShellExecute = $false; $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
        $process = [Diagnostics.Process]::Start($info)
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(10000)) { $process.Kill($true); throw "$($exe.Name) startup timed out." }
        $message = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        # No-argument usage is expected to return nonzero; a Windows loader failure is not.
        if ($process.ExitCode -lt -255 -or [string]::IsNullOrWhiteSpace($message)) { throw "$($exe.Name) failed startup: $($process.ExitCode)" }
        $process.Dispose()
        Write-Host "Native startup verified: $($exe.Name)"
    }
}
finally { Pop-Location; $env:PATH = $savedPath }
