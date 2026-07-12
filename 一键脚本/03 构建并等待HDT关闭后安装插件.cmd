@echo off
setlocal
pushd "%~dp0.."

echo [Meta Companion] Build Release AnyCPU...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\tools\Build-MetaCompanion.ps1"
set "ERR=%ERRORLEVEL%"
if not "%ERR%"=="0" goto :done

echo.
echo [Meta Companion] Waiting for HDT to close, then install...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\tools\Wait-AndInstall-MetaCompanion.ps1" -BuildPath ".\MetaCompanion\bin\Release\MetaCompanion.dll"
set "ERR=%ERRORLEVEL%"

:done
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
