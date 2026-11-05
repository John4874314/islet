@echo off
chcp 65001 >nul
setlocal

REM ============================================================
REM  浮屿 Islet 卸载脚本
REM  会删掉安装目录、快捷方式、自启项；用户配置需单独确认
REM ============================================================

set "APPNAME=浮屿 Islet"
set "TARGET=%LOCALAPPDATA%\Programs\Islet"
set "STARTMENU=%APPDATA%\Microsoft\Windows\Start Menu\Programs"
set "DESKTOP=%USERPROFILE%\Desktop"
set "CONFIG=%LOCALAPPDATA%\Islet"

echo.
echo  正在卸载 %APPNAME% ...
echo.

REM ---- 1. 结束进程 ----
tasklist /FI "IMAGENAME eq Islet.exe" 2>nul | find /I "Islet.exe" >nul
if not errorlevel 1 (
    echo  正在关闭浮屿...
    taskkill /IM Islet.exe /F >nul 2>&1
    timeout /t 1 /nobreak >nul
)

REM ---- 2. 移除自启（注册表 + 启动文件夹，两条都清） ----
echo  正在移除开机自启...
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "Islet" /f >nul 2>&1

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$p = Join-Path ([Environment]::GetFolderPath('Startup')) '浮屿.lnk';" ^
  "if (Test-Path $p) { Remove-Item $p -Force }" >nul 2>&1

REM ---- 3. 删除快捷方式 ----
echo  正在删除快捷方式...
if exist "%STARTMENU%\%APPNAME%.lnk" del /F /Q "%STARTMENU%\%APPNAME%.lnk" >nul 2>&1
if exist "%DESKTOP%\%APPNAME%.lnk" del /F /Q "%DESKTOP%\%APPNAME%.lnk" >nul 2>&1

REM ---- 4. 删除安装目录 ----
echo  正在删除程序文件...
if exist "%TARGET%" rd /S /Q "%TARGET%" >nul 2>&1

REM ---- 5. 询问是否删除配置 ----
echo.
set /p DELCFG="是否同时删除配置与日志（%CONFIG%）？[y/N] "
if /I "%DELCFG%"=="y" (
    if exist "%CONFIG%" rd /S /Q "%CONFIG%" >nul 2>&1
    echo  已删除配置与日志。
) else (
    echo  保留了配置与日志，下次安装会沿用。
)

echo.
echo  卸载完成。
echo.
timeout /t 3 /nobreak >nul
exit /b 0
