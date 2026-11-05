using System;
using System.Text;

namespace Islet.Setup
{
    /// <summary>
    /// 生成卸载脚本。
    ///
    /// 为什么卸载仍用 cmd 而不是再写一个 exe：卸载是低频、破坏性操作，
    /// 让用户能看见「它在删什么」比一个看不见的 exe 更安全 —— 出问题时用户能自己改。
    /// 这个脚本由安装器按当时的路径生成，所以换个安装位置也不会失效。
    ///
    /// 三条边界（都写死在脚本里）：
    ///   1. 只删安装目录，只删它自己的快捷方式和自启项；
    ///   2. 配置与日志单独询问，默认保留（用户可能想重装后沿用）；
    ///   3. 安装目录之外的东西一概不碰。
    /// </summary>
    internal static class UninstallScript
    {
        public static string Build()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine("setlocal");
            sb.AppendLine("REM ============================================================");
            sb.AppendLine("REM  浮屿 Islet 卸载脚本（由安装程序生成）");
            sb.AppendLine("REM ============================================================");
            sb.AppendLine();
            sb.AppendLine("title 卸载 浮屿 Islet");
            sb.AppendLine();
            sb.AppendLine("echo.");
            sb.AppendLine("echo  即将卸载 浮屿 Islet");
            sb.AppendLine("echo.");
            sb.AppendLine("echo    程序目录：%~dp0");
            sb.AppendLine("echo    配置与日志：%LOCALAPPDATA%\\Islet");
            sb.AppendLine("echo.");
            sb.AppendLine("set /p CONFIRM=\"确认卸载？[y/N] \"");
            sb.AppendLine("if /I not \"%CONFIRM%\"==\"y\" (");
            sb.AppendLine("    echo  已取消。");
            sb.AppendLine("    timeout /t 2 /nobreak >nul");
            sb.AppendLine("    exit /b 0");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine("REM ---- 1. 结束进程 ----");
            sb.AppendLine("tasklist /FI \"IMAGENAME eq Islet.exe\" 2>nul | find /I \"Islet.exe\" >nul");
            sb.AppendLine("if not errorlevel 1 (");
            sb.AppendLine("    echo  正在关闭浮屿...");
            sb.AppendLine("    taskkill /IM Islet.exe /F >nul 2>&1");
            sb.AppendLine("    timeout /t 1 /nobreak >nul");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine("REM ---- 2. 移除开机自启（注册表 + 启动文件夹都清，保证状态一致） ----");
            sb.AppendLine("echo  正在移除开机自启...");
            sb.AppendLine("reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\" /v \"Islet\" /f >nul 2>&1");
            sb.AppendLine("powershell -NoProfile -ExecutionPolicy Bypass -Command ^");
            sb.AppendLine("  \"$p = Join-Path ([Environment]::GetFolderPath('Startup')) '浮屿.lnk';\" ^");
            sb.AppendLine("  \"if (Test-Path $p) { Remove-Item $p -Force }\" >nul 2>&1");
            sb.AppendLine();
            sb.AppendLine("REM ---- 3. 移除控制面板里的卸载项 ----");
            sb.AppendLine("reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Islet\" /f >nul 2>&1");
            sb.AppendLine();
            sb.AppendLine("REM ---- 4. 删除快捷方式 ----");
            sb.AppendLine("echo  正在删除快捷方式...");
            sb.AppendLine("set \"SM=%APPDATA%\\Microsoft\\Windows\\Start Menu\\Programs\\浮屿 Islet.lnk\"");
            sb.AppendLine("if exist \"%SM%\" del /F /Q \"%SM%\" >nul 2>&1");
            sb.AppendLine("set \"DK=%USERPROFILE%\\Desktop\\浮屿 Islet.lnk\"");
            sb.AppendLine("if exist \"%DK%\" del /F /Q \"%DK%\" >nul 2>&1");
            sb.AppendLine();
            sb.AppendLine("REM ---- 5. 询问是否删除配置 ----");
            sb.AppendLine("echo.");
            sb.AppendLine("set /p DELCFG=\"是否同时删除配置与日志（%LOCALAPPDATA%\\Islet）？[y/N] \"");
            sb.AppendLine("if /I \"%DELCFG%\"==\"y\" (");
            sb.AppendLine("    if exist \"%LOCALAPPDATA%\\Islet\" rd /S /Q \"%LOCALAPPDATA%\\Islet\" >nul 2>&1");
            sb.AppendLine("    echo  已删除配置与日志。");
            sb.AppendLine(") else (");
            sb.AppendLine("    echo  已保留配置与日志，下次安装会沿用。");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine("REM ---- 6. 删除程序文件 ----");
            sb.AppendLine("REM  先把自己复制到临时目录再删自己所在的目录：");
            sb.AppendLine("REM  cmd 正在运行位于该目录里的脚本，直接 rd 会被占用而失败。");
            sb.AppendLine("echo  正在删除程序文件...");
            sb.AppendLine("set \"SELF=%~f0\"");
            sb.AppendLine("set \"TMPDIR=%TEMP%\\islet_uninstall_%RANDOM%\"");
            sb.AppendLine("mkdir \"%TMPDIR%\" 2>nul");
            sb.AppendLine("copy /Y \"%SELF%\" \"%TMPDIR%\\u.cmd\" >nul 2>&1");
            sb.AppendLine("start \"\" /min cmd /c \"ping -n 2 127.0.0.1 >nul & rd /S /Q \"\"%~dp0\"\" & rd /S /Q \"\"%TMPDIR%\"\"\"");
            sb.AppendLine();
            sb.AppendLine("echo.");
            sb.AppendLine("echo  卸载完成。");
            sb.AppendLine("echo.");
            sb.AppendLine("exit /b 0");
            return sb.ToString();
        }
    }
}
