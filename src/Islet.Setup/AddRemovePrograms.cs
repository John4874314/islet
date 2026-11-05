using System;
using Microsoft.Win32;

namespace Islet.Setup
{
    /// <summary>
    /// 在「设置 → 应用 → 已安装的应用」里登记本程序。
    ///
    /// 不写这一项的话，用户装完之后在系统里找不到卸载入口，
    /// 只能自己去翻目录删文件 —— 这是很多小工具被人抱怨的原因。
    /// 全部写在 HKCU，不需要管理员权限。
    ///
    /// 注意：这是「按用户」登记，只在当前用户的列表里出现。
    /// 想要所有用户都看到得写 HKLM，那需要提权，与 asInvoker 的原则冲突。
    /// </summary>
    internal static class AddRemovePrograms
    {
        private const string UninstallKey =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Islet";

        /// <summary>写入或更新卸载信息。</summary>
        /// <param name="uninstallCommand">
        /// 卸载命令。为 null 时不写 UninstallString，
        /// 用户在列表里能看见但不能从那里卸（仍可用目录里的 uninstall.cmd）。
        /// </param>
        public static void Register(string installDir, string exePath,
                                    string uninstallCommand, int sizeKB)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                if (k == null) return;

                k.SetValue("DisplayName", Installer.AppDisplayName);
                k.SetValue("DisplayVersion", Installer.Version);
                k.SetValue("Publisher", Installer.Publisher);
                k.SetValue("DisplayIcon", exePath);
                // 不设 DisplayIcon 的图标索引：直接用 ico 文件第 0 帧
                k.SetValue("InstallLocation", installDir);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                if (sizeKB > 0)
                    k.SetValue("EstimatedSize", sizeKB, RegistryValueKind.DWord);

                if (!string.IsNullOrEmpty(uninstallCommand))
                {
                    // 带引号路径 + 等待结束后刷新列表
                    k.SetValue("UninstallString",
                        "\"" + uninstallCommand + "\"");
                    k.SetValue("QuietUninstallString",
                        "\"" + uninstallCommand + "\" /quiet");
                }
            }
        }

        /// <summary>卸载时删掉整个键。用户主动卸载才调用。</summary>
        public static void Unregister()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            }
            catch (Exception) { }
        }
    }
}
