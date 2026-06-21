@echo off
setlocal
pushd "%~dp0.."

echo [Meta Companion] Force refresh remote cache for current patch.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\tools\Run-MetaCompanionRefresh.ps1" -Force -PrimaryTimeRange CURRENT_PATCH -MetaFallbackTimeRange CURRENT_PATCH
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
