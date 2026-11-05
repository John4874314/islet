using System;
using System.IO;
using System.Text;

namespace Islet.Sources
{
    /// <summary>
    /// 媒体链路的**事件日志**（不是全量流水）。
    ///
    /// 为什么需要它：媒体条整条链路（SMTC → WASAPI 回落 → 槽位 → UI）
    /// 每一层都有自己的 catch，异常被吞掉之后现象只剩「岛不显示」，
    /// 从外部完全看不出断在哪一环。2026-10-30 那次 coclass 冲突就是这样，
    /// 排查花了很久才定位到一句「X 不能转 X」。
    ///
    /// ------------------------------------------------------------------
    /// 写入策略（重要）：**只在异常和状态变化时写**，正常轮询一行都不写。
    ///
    /// 早先的临时版本把每轮 Poll 的进入/分支都打进去，结果是每 30 秒 3 行、
    /// 一天约 8600 行 / 430KB、一个月 13MB，而且全是「一切正常」的噪音，
    /// 真出问题时反而要被淹没。
    ///
    /// 现在写的是：异常、发布/撤销媒体事件、设备切换。
    /// 一天下来通常只有几十行。
    ///
    /// 文件超过 256KB 就重命名成 .old 重新开始，避免无限增长。
    /// ------------------------------------------------------------------
    internal static class MediaDiag
    {
        private const long MaxBytes = 256 * 1024;
        private static readonly object Gate = new object();

        private static string PathFile
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Islet", "media.log");
            }
        }

        internal static void Event(string msg)
        {
            try
            {
                lock (Gate)
                {
                    string path = PathFile;
                    RotateIfNeeded(path);

                    File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + msg
                        + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch (Exception)
            {
                // 日志本身不许影响主流程
            }
        }

        private static void RotateIfNeeded(string path)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < MaxBytes) return;

                string old = path + ".old";
                try { File.Delete(old); } catch (Exception) { }
                File.Move(path, old);
            }
            catch (Exception) { }
        }
    }
}
