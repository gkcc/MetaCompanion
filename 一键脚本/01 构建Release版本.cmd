@echo off
setlocal
pushd "%~dp0.."

echo [Meta Companion] Build Release x86...
set "CSC_DIR=%USERPROFILE%\.nuget\packages\microsoft.net.compilers\4.2.0\tools"

"%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe" ".\MetaCompanion.sln" /p:Configuration=Release /p:Platform=x86 /p:CscToolPath="%CSC_DIR%" /p:CscToolExe=csc.exe /p:LangVersion=latest /m /v:minimal
set "ERR=%ERRORLEVEL%"

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
