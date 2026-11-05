using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Islet.Core;

namespace Islet.Sources
{
    /// <summary>
    /// 系统体征源。**只在越界时上岛**，不做常驻仪表盘。
    ///
    /// 这是与 Rainmeter 类工具的关键区别：用户不需要天天盯着温度，
    /// 只需要「出事的时候有人告诉他」。
    /// 每个指标都有独立的滞回（hysteresis），避免在阈值附近反复上岛。
    /// </summary>
    public sealed class HealthProvider : IIslandProvider
    {
        private const string IdMemory = "islet.health.memory";
        private const string IdCpu = "islet.health.cpu";
        private const string IdBattery = "islet.health.battery";
        private const string IdDisk = "islet.health.disk";

        // 阈值：进入 / 退出（滞回带，防止抖动）
        private const double MemoryEnter = 92.0;
        private const double MemoryExit = 87.0;
        private const double CpuEnter = 90.0;
        private const double CpuExit = 75.0;
        private const int BatteryEnter = 12;
        private const int BatteryExit = 18;
        private const double DiskEnter = 95.0;
        private const double DiskExit = 92.0;

        /// <summary>连续多少次采样越界才上岛。避免瞬时峰值造成骚扰。</summary>
        private const int SustainSamples = 3;

        private readonly Dictionary<string, int> _streak =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _raised =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        private IIslandSink _sink;
        private Timer _timer;
        private PerformanceCounter _cpuCounter;
        private bool _disposed;

        /// <summary>采样间隔。5 秒足够捕捉「持续高占用」，又不产生可感知开销。</summary>
        public TimeSpan Interval = TimeSpan.FromSeconds(5);

        public string Name
        {
            get { return "Health"; }
        }

        public void Attach(IIslandSink sink)
        {
            _sink = sink;

            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuCounter.NextValue(); // 首次调用总是 0，先丢弃
            }
            catch (Exception)
            {
                _cpuCounter = null; // 计数器不可用则跳过 CPU 监测
            }

            _timer = new Timer(Sample, null, 8000, (int)Interval.TotalMilliseconds);
        }

        private void Sample(object state)
        {
            if (_disposed || _sink == null) return;

            try
            {
                CheckMemory();
                CheckCpu();
                CheckBattery();
                CheckDisk();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Islet] HealthProvider: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------
        // 内存
        // ---------------------------------------------------------------

        private void CheckMemory()
        {
            MEMORYSTATUSEX ms = new MEMORYSTATUSEX();
            ms.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (!GlobalMemoryStatusEx(ref ms)) return;

            double usedPercent = ms.dwMemoryLoad;
            if (!Exceeded(IdMemory, usedPercent, MemoryEnter, MemoryExit)) return;

            ulong usedGb = (ms.ullTotalPhys - ms.ullAvailPhys) / 1024 / 1024 / 1024;
            ulong totalGb = ms.ullTotalPhys / 1024 / 1024 / 1024;

            string culprit = TopMemoryProcess();

            _sink.Publish(new IslandEvent(IdMemory, IslandSource.Health,
                                          string.Format("内存占用 {0:F0}%", usedPercent))
                .WithSubtitle(string.Format("{0} / {1} GB{2}",
                    usedGb, totalGb,
                    string.IsNullOrEmpty(culprit) ? "" : " · " + culprit))
                .WithUrgency(75)
                .WithActionability(60)
                .WithTtl(TimeSpan.FromMinutes(5))
                .WithActions(new IslandAction("打开任务管理器", OpenTaskManager)));
        }

        private static string TopMemoryProcess()
        {
            try
            {
                Process[] all = Process.GetProcesses();
                string name = null;
                long best = 0;
                foreach (Process p in all)
                {
                    try
                    {
                        long ws = p.WorkingSet64;
                        if (ws > best)
                        {
                            best = ws;
                            name = p.ProcessName;
                        }
                    }
                    catch (Exception) { }
                    finally { p.Dispose(); }
                }
                if (name == null || best < 512L * 1024 * 1024) return null;
                return string.Format("{0} {1:F1} GB", name, best / 1024.0 / 1024 / 1024);
            }
            catch (Exception) { return null; }
        }

        // ---------------------------------------------------------------
        // CPU
        // ---------------------------------------------------------------

        private void CheckCpu()
        {
            if (_cpuCounter == null) return;

            double cpu;
            try { cpu = _cpuCounter.NextValue(); }
            catch (Exception) { _cpuCounter = null; return; }

            if (!Exceeded(IdCpu, cpu, CpuEnter, CpuExit)) return;

            _sink.Publish(new IslandEvent(IdCpu, IslandSource.Health,
                                          string.Format("CPU 占用 {0:F0}%", cpu))
                .WithSubtitle("持续高负载")
                .WithUrgency(70)
                .WithActionability(55)
                .WithTtl(TimeSpan.FromMinutes(3))
                .WithActions(new IslandAction("打开任务管理器", OpenTaskManager)));
        }

        // ---------------------------------------------------------------
        // 电量
        // ---------------------------------------------------------------

        private void CheckBattery()
        {
            System.Windows.Forms.PowerStatus ps;
            try { ps = System.Windows.Forms.SystemInformation.PowerStatus; }
            catch (Exception) { return; }

            // 台式机或无电池设备直接跳过
            if (ps.BatteryChargeStatus == System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
                return;
            if (ps.BatteryLifePercent < 0 || ps.BatteryLifePercent > 1.0f) return;

            double percent = ps.BatteryLifePercent * 100.0;
            bool charging = (ps.BatteryChargeStatus &
                             System.Windows.Forms.BatteryChargeStatus.Charging) != 0;

            // 插着电时不报低电量
            if (charging) { Reset(IdBattery); return; }

            // 电池是「反向」阈值：低于 enter 才触发
            double inverted = 100.0 - percent;
            if (!Exceeded(IdBattery, inverted, 100.0 - BatteryEnter, 100.0 - BatteryExit)) return;

            _sink.Publish(new IslandEvent(IdBattery, IslandSource.Health,
                                          string.Format("电量仅剩 {0:F0}%", percent))
                .WithSubtitle("建议尽快充电")
                .WithUrgency(85)
                .WithActionability(20)
                .WithHardPreempt(true)
                .WithTtl(TimeSpan.FromMinutes(10)));
        }

        // ---------------------------------------------------------------
        // 磁盘
        // ---------------------------------------------------------------

        private void CheckDisk()
        {
            string worst = null;
            double worstPercent = 0;

            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed) continue;
                    if (!d.IsReady) continue;
                    if (d.TotalSize <= 0) continue;

                    double used = 100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize;
                    if (used > worstPercent)
                    {
                        worstPercent = used;
                        worst = d.Name;
                    }
                }
            }
            catch (Exception) { return; }

            if (worst == null) return;
            if (!Exceeded(IdDisk, worstPercent, DiskEnter, DiskExit)) return;

            _sink.Publish(new IslandEvent(IdDisk, IslandSource.Health,
                                          string.Format("{0} 磁盘已满 {1:F0}%", worst, worstPercent))
                .WithSubtitle("剩余空间不足 5%")
                .WithUrgency(72)
                .WithActionability(50)
                .WithTtl(TimeSpan.FromMinutes(10))
                .WithActions(new IslandAction("打开此电脑",
                    () => { try { Process.Start("explorer.exe"); } catch (Exception) { } })));
        }

        // ---------------------------------------------------------------
        // 滞回判定
        // ---------------------------------------------------------------

        /// <summary>
        /// 带滞回与持续采样的阈值判定。
        /// 返回 true 表示「本次应当上岛（或保持上岛）」。
        /// </summary>
        private bool Exceeded(string id, double value, double enter, double exit)
        {
            bool raised;
            _raised.TryGetValue(id, out raised);

            if (raised)
            {
                // 已上岛：只有降到 exit 以下才撤销
                if (value <= exit)
                {
                    _raised[id] = false;
                    _streak[id] = 0;
                    _sink.Retract(id);
                    return false;
                }
                // 保持期间刷新内容
                _sink.Retract(id);
                return true;
            }

            // 未上岛：需要连续多次越过 enter
            if (value < enter) { _streak[id] = 0; return false; }

            int n;
            _streak.TryGetValue(id, out n);
            n++;
            _streak[id] = n;

            if (n < SustainSamples) return false;

            _raised[id] = true;
            return true;
        }

        private void Reset(string id)
        {
            _raised[id] = false;
            _streak[id] = 0;
            if (_sink != null) _sink.Retract(id);
        }

        private static void OpenTaskManager()
        {
            try { Process.Start("taskmgr.exe"); }
            catch (Exception) { }
        }

        public void Dispose()
        {
            _disposed = true;
            if (_timer != null) { _timer.Dispose(); _timer = null; }
            if (_cpuCounter != null) { _cpuCounter.Dispose(); _cpuCounter = null; }
            _sink = null;
        }

        // ---------------------------------------------------------------
        // P/Invoke
        // ---------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }
}
