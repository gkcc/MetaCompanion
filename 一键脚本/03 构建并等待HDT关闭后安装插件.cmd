@echo off
setlocal
pushd "%~dp0.."

echo [Meta Companion] Build Release x86...
set "CSC_DIR=%USERPROFILE%\.nuget\packages\microsoft.net.compilers\4.2.0\tools"
"%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe" ".\MetaCompanion.sln" /p:Configuration=Release /p:Platform=x86 /p:CscToolPath="%CSC_DIR%" /p:CscToolExe=csc.exe /p:LangVersion=latest /m /v:minimal
set "ERR=%ERRORLEVEL%"
if not "%ERR%"=="0" goto :done

echo.
echo [Meta Companion] Waiting for HDT to close, then install...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\tools\Wait-AndInstall-MetaCompanion.ps1" -BuildPath ".\MetaCompanion\bin\x86\Release\MetaCompanion.dll"
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
