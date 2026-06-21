@echo off
setlocal

net session >nul 2>&1
if not "%ERRORLEVEL%"=="0" (
	echo [Meta Companion] Requesting administrator permission...
	powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%ComSpec%' -Verb RunAs -ArgumentList '/c ""%~f0""'"
	exit /b
)

pushd "%~dp0.."
echo [Meta Companion] Install scheduled refresh task...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\tools\Install-MetaCompanionRefreshTask.ps1"
set "ERR=%ERRORLEVEL%"

echo.
if "%ERR%"=="0" (
	echo Done.
) else (
	echo Failed with exit code %ERR%.
)
echo.
pause
popd
exit /b %ERR%
