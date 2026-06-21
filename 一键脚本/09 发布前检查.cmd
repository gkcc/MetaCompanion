@echo off
setlocal
pushd "%~dp0.."

echo [Meta Companion] Run release gate.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\tools\Invoke-ReleaseGate.ps1"
set "ERR=%ERRORLEVEL%"

echo.
if "%ERR%"=="0" (
	echo Release gate passed.
) else (
	echo Release gate failed with exit code %ERR%.
)
echo.
pause
popd
exit /b %ERR%
