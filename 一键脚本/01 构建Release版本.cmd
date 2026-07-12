@echo off
setlocal
pushd "%~dp0.."

echo [Meta Companion] Build Release AnyCPU...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\tools\Build-MetaCompanion.ps1"
set "ERR=%ERRORLEVEL%"

:done
echo.
if "%ERR%"=="0" (
	echo Build finished.
) else (
	echo Build failed with exit code %ERR%.
)
echo.
pause
popd
exit /b %ERR%
