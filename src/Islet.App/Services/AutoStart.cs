using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Islet.App.Services
{
    /// <summary>
    /// 开机自启。两条通路，按可用性自动选择：
    ///
    ///   1. **注册表** HKCU\...\Run ——首选，最标准
    ///   2. **启动文件夹** 快捷方式 ——注册表被策略锁死时的退路
    ///
    /// 实测有些机器（尤其开了组策略或第三方安全软件的）会拒绝对 Run 键的写入，
    /// 此时静默降级到启动文件夹，而不是让「开机自启」这个开关变成谎话。
    /// 两条都不可用时如实返回 false，设置页会把开关拨回去。
    /// </summary>
    internal static class AutoStart
    {
        private const string RunKey =
            @"Software\Microsoft\Windows\CurrentVersion\Run";

        private const string ValueName = "Islet";
        private const string ShortcutName = "浮屿.lnk";

        private static string ExeCommand
        {
            get { return "\"" + Application.ExecutablePath + "\""; }
        }

        /// <summary>启动文件夹里的快捷方式完整路径。</summary>
        private static string StartupShortcutPath
        {
            get
            {
                string dir = Environment.GetFolderPath(
                    Environment.SpecialFolder.Startup);
                return Path.Combine(dir, ShortcutName);
            }
        }

        // ---------------------------------------------------------------
        // 状态查询
        // ---------------------------------------------------------------

        public static bool IsRegistryEntryPresent()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    object v = key.GetValue(ValueName);
                    return v != null && v.ToString().Length > 0;
                }
            }
            catch (Exception)
            {
                // 连读都被拒，视为不存在（后续会走启动文件夹）
                return false;
            }
        }

        public static bool IsShortcutPresent()
        {
            try { return File.Exists(StartupShortcutPath); }
            catch (Exception) { return false; }
        }

        public static bool IsEnabled()
        {
            return IsRegistryEntryPresent() || IsShortcutPresent();
        }

        /// <summary>当前实际生效的机制，供设置页与日志展示。</summary>
        public static string ActiveMechanism()
        {
            bool reg = IsRegistryEntryPresent();
            bool lnk = IsShortcutPresent();
            if (reg && lnk) return "注册表 + 启动文件夹";
            if (reg) return "注册表";
            if (lnk) return "启动文件夹";
            return "未启用";
        }

        // ---------------------------------------------------------------
        // 开关
        // ---------------------------------------------------------------

        /// <summary>
        /// 开启开机自启。返回是否至少有一种机制生效。
        /// </summary>
        public static bool Enable()
        {
            if (TryEnableRegistry())
            {
                Log.Info("已开启开机自启（注册表）: " + ExeCommand);
                // 注册表成功时顺手清掉可能残留的旧快捷方式，避免双份启动
                RemoveShortcut();
                return true;
            }

            if (TryEnableShortcut())
            {
                Log.Info("已开启开机自启（启动文件夹）: " + StartupShortcutPath);
                return true;
            }

            Log.Info("开机自启开启失败：注册表与启动文件夹均不可用");
            return false;
        }

        /// <summary>关闭开机自启。两条通路都清掉，保证真的不启动。</summary>
        public static bool Disable()
        {
            bool reg = RemoveRegistryEntry();
            bool lnk = RemoveShortcut();
            Log.Info("已关闭开机自启（注册表=" + reg + " 快捷方式=" + lnk + "）");
            return !IsEnabled();
        }

        /// <summary>按期望值同步，返回实际生效值。</summary>
        public static bool Apply(bool wanted)
        {
            if (wanted) Enable();
            else Disable();
            return IsEnabled();
        }

        // ---------------------------------------------------------------
        // 注册表通路
        // ---------------------------------------------------------------

        private static bool TryEnableRegistry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return false;
                    key.SetValue(ValueName, ExeCommand, RegistryValueKind.String);
                }
                // 回读校验：写入不一定代表生效
                return IsRegistryEntryPresent();
            }
            catch (Exception ex)
            {
                Log.Info("注册表方式不可用（将尝试启动文件夹）: " + ex.Message);
                return false;
            }
        }

        private static bool RemoveRegistryEntry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return true;
                    if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("清理注册表自启失败: " + ex.Message);
                return false;
            }
        }

        // ---------------------------------------------------------------
        // 启动文件夹通路
        // ---------------------------------------------------------------

        private static bool TryEnableShortcut()
        {
            try
            {
                string path = StartupShortcutPath;
                string dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;

                // 已存在且指向当前 exe 就直接算成功
                string existing = ReadShortcutTarget(path);
                if (existing != null &&
                    string.Equals(existing, Application.ExecutablePath,
                                  StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (File.Exists(path)) File.Delete(path);
                return CreateShortcut(path, Application.ExecutablePath);
            }
            catch (Exception ex)
            {
                Log.Info("启动文件夹方式失败: " + ex.Message);
                return false;
            }
        }

        private static bool RemoveShortcut()
        {
            try
            {
                string path = StartupShortcutPath;
                if (File.Exists(path))
                {
                    File.Delete(path);
                    return true;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("删除启动快捷方式失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 用 WScript.Shell 创建 .lnk。
        ///
        /// 注意：不能对 shortcut 对象直接调用 GetType().InvokeMember —— 那是
        /// IDispatch 上的后期绑定，.NET 的反射 API 在这里不适用，会静默失败。
        /// 必须通过 shell 的 IDispatch 接口、以属性名做 InvokeMember，
        /// 这正是 PowerShell 里 New-Object -ComObject 能成功而这里失败的原因。
        /// </summary>
        private static bool CreateShortcut(string linkPath, string targetPath)
        {
            object shell = null;
            object shortcut = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return false;

                shell = Activator.CreateInstance(t);
                shortcut = t.InvokeMember("CreateShortcut",
                    BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
                if (shortcut == null) return false;

                // 关键：用 shortcut 自身的 IDispatch 类型来后期绑定属性
                Type st = shortcut.GetType();
                SetProperty(st, shortcut, "TargetPath", targetPath);
                SetProperty(st, shortcut, "WorkingDirectory",
                            Path.GetDirectoryName(targetPath));
                SetProperty(st, shortcut, "Description", "浮屿 Islet");
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);

                return File.Exists(linkPath);
            }
            catch (Exception ex)
            {
                Log.Info("创建快捷方式失败(" + ex.GetType().Name + "): " + ex.Message);
                return false;
            }
            finally
            {
                Release(shortcut);
                Release(shell);
            }
        }

        private static void SetProperty(Type type, object target, string name, object value)
        {
            type.InvokeMember(name, BindingFlags.SetProperty, null, target,
                              new object[] { value });
        }

        private static void Release(object com)
        {
            if (com == null) return;
            try
            {
                if (System.Runtime.InteropServices.Marshal.IsComObject(com))
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(com);
            }
            catch (Exception) { }
        }

        private static string ReadShortcutTarget(string linkPath)
        {
            object shell = null;
            object shortcut = null;
            try
            {
                if (!File.Exists(linkPath)) return null;
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;

                shell = Activator.CreateInstance(t);
                shortcut = t.InvokeMember("CreateShortcut",
                    BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
                if (shortcut == null) return null;

                object target = shortcut.GetType().InvokeMember("TargetPath",
                    BindingFlags.GetProperty, null, shortcut, null);
                return target == null ? null : target.ToString();
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                Release(shortcut);
                Release(shell);
            }
        }

        /// <summary>
        /// 已启用时，把失效的路径纠正到当前 exe 位置（程序被移动过的场景）。
        /// </summary>
        public static void RepairIfStale()
        {
            try
            {
                if (IsRegistryEntryPresent())
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    {
                        if (key != null)
                        {
                            object v = key.GetValue(ValueName);
                            if (v != null &&
                                !string.Equals(v.ToString(), ExeCommand,
                                               StringComparison.OrdinalIgnoreCase))
                            {
                                key.SetValue(ValueName, ExeCommand,
                                             RegistryValueKind.String);
                                Log.Info("开机自启注册表路径已修正");
                            }
                        }
                    }
                }

                if (IsShortcutPresent())
                {
                    string target = ReadShortcutTarget(StartupShortcutPath);
                    if (target == null ||
                        !string.Equals(target, Application.ExecutablePath,
                                       StringComparison.OrdinalIgnoreCase))
                    {
                        TryEnableShortcut();
                        Log.Info("开机自启快捷方式已修正");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("修正开机自启路径失败", ex);
            }
        }
    }
}
