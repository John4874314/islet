@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

REM ============================================================
REM  浮屿 Islet 安装脚本（由安装包解压后自动调用）
REM  安装到 %LOCALAPPDATA%\Programs\Islet，不需要管理员权限
REM ============================================================

set "APPNAME=浮屿 Islet"
set "TARGET=%LOCALAPPDATA%\Programs\Islet"
set "STARTMENU=%APPDATA%\Microsoft\Windows\Start Menu\Programs"
set "DESKTOP=%USERPROFILE%\Desktop"

echo.
echo  正在安装 %APPNAME% ...
echo.

REM ---- 1. 若已在运行，先请用户关闭 ----
tasklist /FI "IMAGENAME eq Islet.exe" 2>nul | find /I "Islet.exe" >nul
if not errorlevel 1 (
    echo  检测到浮屿正在运行，正在关闭旧实例...
    taskkill /IM Islet.exe /F >nul 2>&1
    timeout /t 1 /nobreak >nul
)

REM ---- 2. 创建安装目录 ----
if not exist "%TARGET%" mkdir "%TARGET%" 2>nul
if not exist "%TARGET%" (
    echo  [错误] 无法创建安装目录：%TARGET%
    echo  请检查磁盘空间或权限。
    pause
    exit /b 1
)

REM ---- 3. 复制文件 ----
echo  正在复制文件...
copy /Y "%~dp0Islet.exe"        "%TARGET%\" >nul
copy /Y "%~dp0Islet.Core.dll"   "%TARGET%\" >nul
if exist "%~dp0Islet.exe.config" copy /Y "%~dp0Islet.exe.config" "%TARGET%\" >nul

if not exist "%TARGET%\Islet.exe" (
    echo  [错误] 文件复制失败。
    pause
    exit /b 1
)

REM ---- 4. 开始菜单快捷方式 ----
echo  正在创建快捷方式...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ws = New-Object -ComObject WScript.Shell;" ^
  "$s = $ws.CreateShortcut('%STARTMENU%\%APPNAME%.lnk');" ^
  "$s.TargetPath = '%TARGET%\Islet.exe';" ^
  "$s.WorkingDirectory = '%TARGET%';" ^
  "$s.Description = '%APPNAME%';" ^
  "$s.Save()" >nul 2>&1

REM ---- 5. 桌面快捷方式 ----
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ws = New-Object -ComObject WScript.Shell;" ^
  "$s = $ws.CreateShortcut('%DESKTOP%\%APPNAME%.lnk');" ^
  "$s.TargetPath = '%TARGET%\Islet.exe';" ^
  "$s.WorkingDirectory = '%TARGET%';" ^
  "$s.Description = '%APPNAME%';" ^
  "$s.Save()" >nul 2>&1

REM ---- 6. 写入开机自启（默认开启，用户可在设置里关掉） ----
echo  正在注册开机自启...
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" ^
    /v "Islet" /t REG_SZ /d "\"%TARGET%\Islet.exe\"" /f >nul 2>&1

if errorlevel 1 (
    echo  [提示] 注册表写入被系统限制，改用启动文件夹。
    powershell -NoProfile -ExecutionPolicy Bypass -Command ^
      "$ws = New-Object -ComObject WScript.Shell;" ^
      "$p = Join-Path ([Environment]::GetFolderPath('Startup')) '浮屿.lnk';" ^
      "$s = $ws.CreateShortcut($p);" ^
      "$s.TargetPath = '%TARGET%\Islet.exe';" ^
      "$s.WorkingDirectory = '%TARGET%';" ^
      "$s.Save()" >nul 2>&1
)

REM ---- 7. 启动程序 ----
echo  正在启动...
start "" "%TARGET%\Islet.exe"

echo.
echo  安装完成。
echo.
echo    安装位置：%TARGET%
echo    配置文件：%LOCALAPPDATA%\Islet\settings.ini
echo    日志文件：%LOCALAPPDATA%\Islet\islet.log
echo.
echo  浮屿会出现在托盘区（屏幕右下角），双击托盘图标可打开设置。
echo  热键 Ctrl+Alt+I 可显示/隐藏胶囊。
echo.
timeout /t 4 /nobreak >nul
exit /b 0
