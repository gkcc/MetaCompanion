@echo off
setlocal
pushd "%~dp0.."

echo [Meta Companion] Build Release AnyCPU before tests...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\tools\Build-MetaCompanion.ps1"
set "ERR=%ERRORLEVEL%"
if not "%ERR%"=="0" goto :done

echo.
echo [Meta Companion] Run tests in native PowerShell with HDT AppData sandbox.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\tools\Run-Tests.ps1"
set "ERR=%ERRORLEVEL%"

:done
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
