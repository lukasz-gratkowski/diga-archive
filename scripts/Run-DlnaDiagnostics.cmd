@echo off
setlocal
rem An unsigned kit can only run its script with the execution policy switched off for this one PowerShell process.
rem A signed release is packaged with AllSigned on the next line instead (scripts\New-Release.ps1 changes it), and
rem PowerShell then runs the script only while its publisher's signature is valid.
set "DiagnosticPolicy=Bypass"
title AMG DIGA Archive DLNA diagnostics
echo AMG DIGA Archive - read-only recorder diagnostics
echo Script location: "%~dp0"
echo No recordings are downloaded and nothing is uploaded.
echo.
if not exist "%~dp0Test-DlnaRecorder.ps1" (
  echo Test-DlnaRecorder.ps1 is missing. Extract the whole diagnostic ZIP first.
  pause
  exit /b 3
)
if /i "%DiagnosticPolicy%"=="AllSigned" (
  echo Windows PowerShell checks the signature of the script before it runs it. If it asks whether to run
  echo software from this publisher, compare the name with the one in the release notes and answer R, Run once.
  echo.
)
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy %DiagnosticPolicy% -File "%~dp0Test-DlnaRecorder.ps1" %*
set "DiagnosticExit=%errorlevel%"
echo.
echo Finished with exit code %DiagnosticExit%.
echo 0 = a root Browse succeeded - see Outcome in report.json; 2 = recorder response failed; 3 = setup or selection failed.
echo The script printed the report folder and sanitized ZIP location above.
if /i "%DiagnosticPolicy%"=="AllSigned" (
  echo 1 = PowerShell did not run the script: its signature is missing or damaged, or the publisher was not accepted.
  echo This launcher changes no Windows setting.
) else (
  echo This launcher changes execution policy only for its PowerShell process.
)
pause
exit /b %DiagnosticExit%
