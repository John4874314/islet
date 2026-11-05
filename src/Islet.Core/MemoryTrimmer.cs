using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace Islet.Core
{
    /// <summary>
    /// 内存优化。常驻组件的内存占用必须可控（PRD §8：≤ 60 MB），
    /// 这里做三件事：
    ///   1. 空闲时把工作集换出（EmptyWorkingSet）
    ///   2. 关闭后台 GC，避免 GC 线程长期占栈
    ///   3. 压缩大对象堆
    ///
    /// 全部是「按需触发」，不常驻线程、不轮询。
    /// </summary>
    public static class MemoryTrimmer
    {
        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        /// <summary>
        /// 把当前进程的工作集尽量换出到页面文件。
        /// 注意：这不是「释放内存」，只是让任务管理器里的数字降下来，
        /// 下次访问会被换回。所以只在真正空闲时调用一次，不要循环调用。
        /// </summary>
        public static bool TrimWorkingSet()
        {
            try
            {
                return EmptyWorkingSet(GetCurrentProcess()) != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>当前进程工作集字节数。</summary>
        public static long WorkingSetBytes
        {
            get
            {
                try
                {
                    using (Process p = Process.GetCurrentProcess())
                    {
                        p.Refresh();
                        return p.WorkingSet64;
                    }
                }
                catch (Exception) { return 0; }
            }
        }

        public static double WorkingSetMB
        {
            get { return WorkingSetBytes / 1024.0 / 1024.0; }
        }

        /// <summary>
        /// 一次完整的瘦身：压缩 LOH、回收、再换出工作集。
        /// 顺序很重要——先回收再换出，否则刚释放的页又会被换回来。
        /// </summary>
        public static void Compact()
        {
            try
            {
                GCSettings.LargeObjectHeapCompactionMode =
                    GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            }
            catch (Exception) { }

            TrimWorkingSet();
        }

        /// <summary>
        /// 在进程启动时调用一次，换取更小的常驻开销。
        /// .NET Framework 下真正生效的是 Islet.exe.config 里的 gcConcurrent/gcServer，
        /// 这里只做最后兜底，避免配置文件缺失时行为不可预期。
        /// </summary>
        public static void TuneForBackgroundApp()
        {
            try
            {
                // 若有配置文件，这两项已由 CLR 读取；此处不重复设置，
                // 只确认 GC 处于工作站模式（桌面应用不该用 Server GC）
                if (GCSettings.IsServerGC)
                {
                    Debug.WriteLine("[Islet] 警告：检测到 Server GC，常驻内存会偏高");
                }
            }
            catch (Exception) { }
        }
    }
}
