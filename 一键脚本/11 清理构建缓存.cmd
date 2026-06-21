@echo off
setlocal
pushd "%~dp0.."

echo [Meta Companion] Clean build cache.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\tools\Clean-MetaCompanionWorkspace.ps1"
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
