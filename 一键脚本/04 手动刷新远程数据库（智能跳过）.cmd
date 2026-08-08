@echo off
chcp 65001 >nul
setlocal
pushd "%~dp0.."

echo [Meta Companion] 正在刷新当前补丁的远端缓存；如果今天的数据已经完整，会自动跳过。
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\tools\Run-MetaCompanionRefresh.ps1" -PrimaryTimeRange CURRENT_PATCH -MetaFallbackTimeRange LAST_1_DAY
set "ERR=%ERRORLEVEL%"

echo.
if "%ERR%"=="0" (
	echo 刷新完成。
) else (
	echo 刷新失败，退出码：%ERR%。
)
echo.
pause
popd
exit /b %ERR%
