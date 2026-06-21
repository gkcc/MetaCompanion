@echo off
setlocal
pushd "%~dp0.."

echo [Meta Companion] Refresh local meta for current patch and personal recommendations.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\tools\Update-MetaCompanionData.ps1" -LocalMeta -PersonalRecommendations -MetaTimeRange CURRENT_PATCH
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
