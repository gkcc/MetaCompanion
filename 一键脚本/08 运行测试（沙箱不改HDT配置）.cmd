@echo off
setlocal
pushd "%~dp0.."

echo [Meta Companion] Run tests in x86 PowerShell with HDT AppData sandbox.
"%WINDIR%\SysWOW64\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File ".\tools\Run-Tests.ps1"
set "ERR=%ERRORLEVEL%"

echo.
if "%ERR%"=="0" (
	echo Tests passed.
) else (
	echo Tests failed with exit code %ERR%.
)
echo.
pause
popd
exit /b %ERR%
