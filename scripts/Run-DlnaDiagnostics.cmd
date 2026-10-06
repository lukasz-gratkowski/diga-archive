@echo off
setlocal
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
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Test-DlnaRecorder.ps1" %*
set "DiagnosticExit=%errorlevel%"
echo.
echo Finished with exit code %DiagnosticExit%.
echo 0 = a root Browse succeeded - see Outcome in report.json; 2 = recorder response failed; 3 = setup or selection failed.
echo The script printed the report folder and sanitized ZIP location above.
echo This launcher changes execution policy only for its PowerShell process.
pause
exit /b %DiagnosticExit%
