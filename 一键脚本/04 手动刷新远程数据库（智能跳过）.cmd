@echo off
setlocal
pushd "%~dp0.."

echo [Meta Companion] Refresh remote cache for current patch. This may skip when today's cache is already fresh.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\tools\Run-MetaCompanionRefresh.ps1" -PrimaryTimeRange CURRENT_PATCH -MetaFallbackTimeRange CURRENT_PATCH
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
