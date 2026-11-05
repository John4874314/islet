using System;
using System.IO;
using System.Text;

namespace Islet.App
{
    /// <summary>
    /// 极简文件日志。产品态没有调试器，异常必须落盘才能排查。
    /// 写到 %LOCALAPPDATA%\Islet\islet.log，超过 1 MB 自动截断。
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;

        public static string Path_
        {
            get
            {
                if (_path != null) return _path;
                try
                {
                    string dir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Islet");
                    Directory.CreateDirectory(dir);
                    _path = System.IO.Path.Combine(dir, "islet.log");
                }
                catch (Exception)
                {
                    _path = System.IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "islet.log");
                }
                return _path;
            }
        }

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Error(string message, Exception ex)
        {
            StringBuilder sb = new StringBuilder(message);
            if (ex != null)
            {
                sb.Append(" | ").Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
                if (ex.StackTrace != null) sb.Append(Environment.NewLine).Append(ex.StackTrace);
                Exception inner = ex.InnerException;
                int depth = 0;
                while (inner != null && depth < 3)
                {
                    sb.Append(Environment.NewLine).Append("  INNER ")
                      .Append(inner.GetType().FullName).Append(": ").Append(inner.Message);
                    inner = inner.InnerException;
                    depth++;
                }
            }
            Write("ERROR", sb.ToString());
        }

        private static void Write(string level, string message)
        {
            lock (Gate)
            {
                try
                {
                    string p = Path_;
                    if (File.Exists(p) && new FileInfo(p).Length > 1024 * 1024)
                        File.Delete(p);

                    File.AppendAllText(p,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                        + " [" + level + "] " + message + Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch (Exception) { /* 日志失败绝不能影响主流程 */ }
            }
        }
    }
}
