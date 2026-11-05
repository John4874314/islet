using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Islet.Setup
{
    /// <summary>
    /// 创建 Windows 快捷方式（.lnk）。
    ///
    /// 为什么不用 `WScript.Shell` 的 COM 后期绑定：
    /// 那需要 dynamic / Type.InvokeMember，`/langversion:5` 下写起来很别扭，
    /// 而且在部分机器上会被安全软件拦住「脚本宿主创建快捷方式」这个行为。
    /// 直接声明 IShellLink + IPersistFile 的接口，走正规的 COM 早绑定，
    /// 既没有动态调用、又不需要额外依赖。
    /// </summary>
    internal static class ShellLink
    {
        // ---- COM 接口声明 ----

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLinkCoClass { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
                         int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
                                 int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            // IPersist
            void GetClassID(out Guid pClassID);
            // IPersistFile
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName,
                      [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        private const int SW_SHOWNORMAL = 1;

        /// <summary>
        /// 创建（或覆盖）一个快捷方式。<paramref name="iconPath"/> 可以为 null。
        /// 失败时抛 COMException，由调用方决定是否忽略。
        /// </summary>
        public static void Create(string linkPath, string targetPath, string workingDir,
                                  string description, string iconPath)
        {
            string dir = Path.GetDirectoryName(linkPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            object coclass = new ShellLinkCoClass();
            try
            {
                IShellLinkW link = (IShellLinkW)coclass;
                link.SetPath(targetPath);
                if (!string.IsNullOrEmpty(workingDir)) link.SetWorkingDirectory(workingDir);
                if (!string.IsNullOrEmpty(description)) link.SetDescription(description);
                link.SetShowCmd(SW_SHOWNORMAL);
                if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
                    link.SetIconLocation(iconPath, 0);

                ((IPersistFile)link).Save(linkPath, true);
            }
            finally
            {
                Marshal.ReleaseComObject(coclass);
            }
        }
    }
}
