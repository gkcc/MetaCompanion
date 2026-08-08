@echo off
chcp 65001 >nul
setlocal

net session >nul 2>&1
if not "%ERRORLEVEL%"=="0" (
	echo [Meta Companion] 正在请求管理员权限...
	powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%ComSpec%' -Verb RunAs -ArgumentList '/c ""%~f0""'"
	exit /b
)

pushd "%~dp0.."
echo [Meta Companion] 正在安装自动刷新计划任务...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\tools\Install-MetaCompanionRefreshTask.ps1"
set "ERR=%ERRORLEVEL%"

echo.
if "%ERR%"=="0" (
	echo 计划任务安装完成。
) else (
	echo 安装失败，退出码：%ERR%。
)
echo.
pause
popd
exit /b %ERR%
