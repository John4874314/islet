using System;
using Microsoft.Win32;

namespace Islet.Setup
{
    /// <summary>
    /// 开机自启。写 HKCU 的 Run 项，不需要管理员权限。
    /// 失败时退回「启动」文件夹快捷方式（组策略常会拦注册表那条）。
    /// </summary>
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Islet";

        /// <summary>开或关。两个位置都写/清，保证状态一致。</summary>
        public static void Set(bool enabled, string exePath)
        {
            if (enabled) Enable(exePath);
            else Disable();
        }

        private static void Enable(string exePath)
        {
            bool ok = false;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true)
                                       ?? Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k != null)
                    {
                        k.SetValue(ValueName, "\"" + exePath + "\"", RegistryValueKind.String);
                        ok = true;
                    }
                }
            }
            catch (Exception) { }

            if (ok) return;

            // 注册表写不进去 → 用启动文件夹
            try
            {
                string startup = Environment.GetFolderPath(
                    Environment.SpecialFolder.Startup);
                string lnk = System.IO.Path.Combine(startup, "浮屿.lnk");
                ShellLink.Create(lnk, exePath, System.IO.Path.GetDirectoryName(exePath),
                                 "浮屿 Islet", null);
            }
            catch (Exception) { }
        }

        private static void Disable()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k != null) k.DeleteValue(ValueName, false);
                }
            }
            catch (Exception) { }

            try
            {
                string startup = Environment.GetFolderPath(
                    Environment.SpecialFolder.Startup);
                string lnk = System.IO.Path.Combine(startup, "浮屿.lnk");
                if (System.IO.File.Exists(lnk)) System.IO.File.Delete(lnk);
            }
            catch (Exception) { }
        }
    }
}
