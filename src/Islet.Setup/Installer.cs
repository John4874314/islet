using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Islet.Setup
{
    /// <summary>
    /// 安装程序的数据与动作层：不依赖 UI，方便单独测。
    ///
    /// 载荷从哪来：正式包把 Islet 的文件**嵌进 Setup.exe 的资源**里，
    /// 安装时先解到临时目录再往外拷。这样分发出去就是一个 exe，
    /// 不会出现「解压出一堆文件，用户不知道该双击哪个」。
    /// 开发调试时（资源不存在）则退回「读 Setup.exe 旁边的文件」。
    /// </summary>
    internal static class Installer
    {
        public const string AppDisplayName = "浮屿 Islet";
        public const string AppId = "Islet";
        public const string Publisher = "浮屿";
        public const string Version = "1.0.0";

        /// <summary>安装包必须带的文件。</summary>
        public static readonly string[] Payload =
        {
            "Islet.exe",
            "Islet.Core.dll",
            "Islet.exe.config",
            "islet.ico"
        };

        /// <summary>
        /// 载荷文件的内容：文件名 → 字节。
        ///
        /// 为什么放内存而不解到临时目录：
        ///   1. 受限环境（安全软件、组策略、沙箱）经常连 %TEMP% 下新建目录都拒绝，
        ///      解包会直接失败，用户看到「访问被拒绝」却无从下手；
        ///   2. 解到临时目录再复制一遍，等于同一份数据写两次、读两次；
        ///   3. 临时目录一旦清理失败就留下垃圾，卸载也清不掉。
        /// 整个载荷才 300 KB 出头，常驻内存完全没有压力。
        /// </summary>
        private static readonly Dictionary<string, byte[]> _files =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        private static bool _prepared;
        private static string _externalDir;   // 开发模式：直接读 exe 旁边的文件

        /// <summary>载荷是否可以直接读（内存里有，或 exe 同目录有）。</summary>
        public static string SourceDir
        {
            get
            {
                return _externalDir ?? AppDomain.CurrentDomain.BaseDirectory;
            }
        }

        public static string DefaultTargetDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "Islet");
            }
        }

        /// <summary>配置与日志目录。卸载时单独询问是否删除，所以不在安装目录里。</summary>
        public static string DataDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Islet");
            }
        }

        /// <summary>
        /// 把载荷读进内存。内嵌资源存在就从资源读，否则退回「读 exe 同目录的文件」。
        /// 返回 null 表示成功，否则是错误说明。
        ///
        /// ⚠️ 打包脚本把四个文件写进**一个** .resources（名字叫
        /// "Islet.Setup.payload.resources"），不是每个文件一个资源。
        /// 所以这里必须用 ResourceReader 遍历里面的键，
        /// 不能按 "Islet.Setup.payload.<文件名>" 去 GetManifestResourceStream ——
        /// 那样一个都找不到，运行时会误报「安装包不完整」。
        /// </summary>
        public static string PreparePayload()
        {
            if (_prepared) return VerifyPayload();

            Assembly asm = Assembly.GetExecutingAssembly();

            string resName = null;
            foreach (string n in asm.GetManifestResourceNames())
            {
                if (n.IndexOf("payload", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    resName = n;
                    break;
                }
            }

            if (resName == null)
            {
                // 开发模式：安装程序旁边就有那几个文件
                _externalDir = AppDomain.CurrentDomain.BaseDirectory;
                _prepared = true;
                return VerifyPayload();
            }

            try
            {
                using (Stream s = asm.GetManifestResourceStream(resName))
                {
                    if (s == null) return "安装包损坏：读不到内嵌资源。";

                    using (System.Resources.ResourceReader reader =
                           new System.Resources.ResourceReader(s))
                    {
                        System.Collections.IDictionaryEnumerator it = reader.GetEnumerator();
                        while (it.MoveNext())
                        {
                            string key = it.Key as string;
                            if (key == null) continue;

                            // 键形如 "Islet.Setup.payload.Islet.exe"。
                            // ⚠️ 不能用 Path.GetFileName 取文件名：资源名里的分隔符是**点**，
                            // 不是反斜杠，GetFileName 会把整串原样返回，
                            // 于是文件名变成 "Islet.Setup.payload.Islet.exe"，
                            // VerifyPayload 找不到 Islet.exe —— 自检里就是这么暴露的。
                            string fileName = key;
                            const string prefix = "Islet.Setup.payload.";
                            if (fileName.StartsWith(prefix, StringComparison.Ordinal))
                                fileName = fileName.Substring(prefix.Length);

                            if (fileName.Length == 0) continue;
                            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;

                            byte[] bytes = it.Value as byte[];
                            if (bytes == null) continue;

                            _files[fileName] = bytes;
                        }
                    }
                }

                _prepared = true;
                return VerifyPayload();
            }
            catch (Exception ex)
            {
                return "读取安装包内容失败：" + ex.Message;
            }
        }

        /// <summary>载荷里某个文件的字节数（-1 表示没有）。自检用来核对装出来的大小。</summary>
        public static long PayloadSize(string fileName)
        {
            byte[] bytes;
            if (_files.TryGetValue(fileName, out bytes)) return bytes.Length;

            try
            {
                string p = Path.Combine(SourceDir, fileName);
                if (File.Exists(p)) return new FileInfo(p).Length;
            }
            catch (Exception) { }
            return -1;
        }

        /// <summary>把一个载荷文件写到目标目录。内存里有就直接写，否则从旁边复制。</summary>
        private static void WritePayloadFile(string fileName, string targetDir)
        {
            string dest = Path.Combine(targetDir, fileName);

            byte[] bytes;
            if (_files.TryGetValue(fileName, out bytes))
            {
                File.WriteAllBytes(dest, bytes);
                return;
            }

            File.Copy(Path.Combine(SourceDir, fileName), dest, true);
        }

        private static string VerifyPayload()
        {
            if (_externalDir != null)
            {
                // 开发模式：文件必须真的在磁盘上
                foreach (string f in Payload)
                {
                    if (!File.Exists(Path.Combine(_externalDir, f)))
                        return "安装包不完整，缺少文件：" + f + "\n请重新下载安装包。";
                }
                return null;
            }

            foreach (string f in Payload)
            {
                if (!_files.ContainsKey(f))
                    return "安装包不完整，缺少文件：" + f + "\n请重新下载安装包。";
            }
            return null;
        }

        /// <summary>
        /// 保留这个 API 供 Program 调用，但现在已经没有临时目录要清 ——
        /// 载荷全程在内存里。留着是为了不改调用方，也方便以后真有临时文件时补。
        /// </summary>
        public static void CleanupPayload()
        {
            _files.Clear();
            _prepared = false;
        }

        // ---------------------------------------------------------------
        // 安装前检查
        // ---------------------------------------------------------------

        public static bool HasExisting(string targetDir)
        {
            try { return File.Exists(Path.Combine(targetDir, "Islet.exe")); }
            catch (Exception) { return false; }
        }

        /// <summary>目标目录是否可写。选到 Program Files 之类就需要换位置。</summary>
        public static bool IsWritable(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".islet-write-test");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch (Exception) { return false; }
        }

        // ---------------------------------------------------------------
        // 安装
        // ---------------------------------------------------------------

        public static void Install(string targetDir, bool desktopShortcut,
                                   bool startMenuShortcut, bool autorun,
                                   Action<string> log)
        {
            Report(log, "准备安装目录…");
            KillRunning(log);
            Directory.CreateDirectory(targetDir);

            Report(log, "复制程序文件…");
            foreach (string f in Payload)
            {
                WritePayloadFile(f, targetDir);
            }

            string exe = Path.Combine(targetDir, "Islet.exe");
            if (!File.Exists(exe))
                throw new IOException("复制后找不到 Islet.exe，安装未完成。");

            // 卸载脚本由安装器自己生成，不用随包带一份 cmd
            Report(log, "写入卸载程序…");
            try
            {
                File.WriteAllText(Path.Combine(targetDir, "uninstall.cmd"),
                                  UninstallScript.Build(), new System.Text.UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                Report(log, "  （卸载脚本写入失败：" + ex.Message + "）");
            }

            if (startMenuShortcut)
            {
                Report(log, "创建开始菜单快捷方式…");
                try
                {
                    string sm = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        @"Microsoft\Windows\Start Menu\Programs");
                    Directory.CreateDirectory(sm);
                    ShellLink.Create(Path.Combine(sm, AppDisplayName + ".lnk"),
                                     exe, targetDir, AppDisplayName,
                                     Path.Combine(targetDir, "islet.ico"));
                }
                catch (Exception ex)
                {
                    Report(log, "  （开始菜单快捷方式失败：" + ex.Message + "）");
                }
            }

            if (desktopShortcut)
            {
                Report(log, "创建桌面快捷方式…");
                try
                {
                    string desk = Environment.GetFolderPath(
                        Environment.SpecialFolder.DesktopDirectory);
                    ShellLink.Create(Path.Combine(desk, AppDisplayName + ".lnk"),
                                     exe, targetDir, AppDisplayName,
                                     Path.Combine(targetDir, "islet.ico"));
                }
                catch (Exception ex)
                {
                    Report(log, "  （桌面快捷方式失败：" + ex.Message + "）");
                }
            }

            Report(log, "注册卸载信息…");
            try
            {
                string uninstall = Path.Combine(targetDir, "uninstall.cmd");
                AddRemovePrograms.Register(targetDir, exe,
                    File.Exists(uninstall) ? uninstall : null,
                    (int)(DirectorySize(targetDir) / 1024));
            }
            catch (Exception ex)
            {
                Report(log, "  （卸载信息写入失败：" + ex.Message + "）");
            }

            Report(log, autorun ? "注册开机自启…" : "不注册开机自启。");
            try { AutoStart.Set(autorun, exe); }
            catch (Exception ex) { Report(log, "  （自启设置失败：" + ex.Message + "）"); }
        }

        /// <summary>启动刚装好的程序。失败不算安装失败。</summary>
        public static void Launch(string targetDir)
        {
            try
            {
                string exe = Path.Combine(targetDir, "Islet.exe");
                if (!File.Exists(exe)) return;

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = targetDir,
                    UseShellExecute = true
                });
            }
            catch (Exception) { }
        }

        /// <summary>结束正在运行的浮屿。升级时必须要，否则 exe 被占用复制会失败。</summary>
        public static void KillRunning(Action<string> log)
        {
            try
            {
                System.Diagnostics.Process[] ps =
                    System.Diagnostics.Process.GetProcessesByName("Islet");
                if (ps.Length == 0) return;

                Report(log, "关闭正在运行的浮屿…");
                foreach (System.Diagnostics.Process p in ps)
                {
                    try { p.Kill(); }
                    catch (Exception) { }
                    finally { p.Dispose(); }
                }
                System.Threading.Thread.Sleep(900);
            }
            catch (Exception) { }
        }

        private static long DirectorySize(string dir)
        {
            long total = 0;
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    try { total += new FileInfo(f).Length; }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return total;
        }

        private static void Report(Action<string> log, string text)
        {
            if (log != null) log(text);
        }
    }
}
