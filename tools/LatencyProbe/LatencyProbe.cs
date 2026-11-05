// LatencyProbe -- 实测「播放/停止」从状态变化到岛更新的延迟。
//
// 为什么需要它：用户反馈「还是慢」，而「慢」必须先变成毫秒数，
// 否则每次改完都只能靠感觉，容易反复猜错方向。
//
// 做法（两段，互不干扰）：
//   A. 静态能力检查：会话事件推送到底注册上没有？
//      注册失败就退回轮询，功能正常但慢 —— 这是首先要排除的。
//   B. 延迟采样：订阅 SessionActivity 与岛上的可见状态，记录
//      「状态变化 → 事件发布」的耗时。
//
// 用法：
//   LatencyProbe.exe --watch 30     监听 30 秒，打印每次状态变化的延迟
//   LatencyProbe.exe --info         只打印能力信息
//
// 注意：它**不注入任何输入**，只读音频会话状态。
// 要测「按下播放键 → 岛上出现」的端到端延迟，需要手动配合：
// 先运行 --watch，再自己按播放/停止，观察输出的时间差。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Islet.Sources;

internal static class LatencyProbe
{
    private static readonly object Gate = new object();
    private static int _activityCount;
    private static DateTime _lastActivityAt = DateTime.MinValue;
    private static string _lastSig;
    private static readonly List<string> Timeline = new List<string>();

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        bool infoOnly = HasFlag(args, "--info");
        int watchSec = GetInt(args, "--watch", 30);

        PrintInfo();

        if (infoOnly) return 0;

        Console.WriteLine();
        Console.WriteLine("=== 监听 " + watchSec + " 秒 ===");
        Console.WriteLine("现在请手动操作播放器：播放 → 暂停 → 播放 → 停止");
        Console.WriteLine("每次状态变化会打印时间戳与耗时");
        Console.WriteLine();

        AudioSessionProvider.SessionActivity += OnActivity;

        // 用同一个 provider 持续采样，模拟应用里的真实路径
        AudioSessionProvider provider = new AudioSessionProvider();
        provider.Refresh();

        string prevSig = null;
        DateTime prevAt = DateTime.UtcNow;
        Stopwatch total = Stopwatch.StartNew();
        int changes = 0;

        while (total.Elapsed.TotalSeconds < watchSec)
        {
            provider.Refresh();

            List<string> playing = new List<string>();
            foreach (AudioSession s in provider.Sessions)
            {
                if (s.State == 1 && s.CanControl &&
                    !string.Equals(s.DisplayName, "Islet", StringComparison.OrdinalIgnoreCase))
                    playing.Add(s.DisplayName);
            }
            playing.Sort(StringComparer.OrdinalIgnoreCase);
            string sig = string.Join(",", playing.ToArray());

            if (sig != prevSig)
            {
                DateTime now = DateTime.UtcNow;
                double sincePrev = (now - prevAt).TotalMilliseconds;

                // 距上一次推送回调多久。接近 0 说明是推送驱动的，
                // 接近采样间隔说明还是靠轮询发现的。
                double sincePush = _lastActivityAt == DateTime.MinValue
                    ? -1
                    : (now - _lastActivityAt).TotalMilliseconds;

                changes++;
                Console.WriteLine(string.Format(
                    "[{0:HH:mm:ss.fff}] 变化#{1}  {2} → {3}   (距上次变化 {4:F0}ms, 距推送 {5})",
                    now.ToLocalTime(), changes,
                    prevSig == null || prevSig.Length == 0 ? "(无)" : prevSig,
                    sig.Length == 0 ? "(无)" : sig,
                    sincePrev,
                    sincePush < 0 ? "无推送" : sincePush.ToString("F0") + "ms"));

                prevSig = sig;
                prevAt = now;
            }

            Thread.Sleep(40);   // 40ms 采样，足以分辨「推送」与「800ms 轮询」
        }

        Console.WriteLine();
        Console.WriteLine("---------------------------------------");
        Console.WriteLine("状态变化次数   : " + changes);
        Console.WriteLine("推送回调次数   : " + _activityCount);
        Console.WriteLine("采样间隔       : 40ms");

        if (!AudioSessionProvider.NotifyActive)
        {
            Console.WriteLine();
            Console.WriteLine("判定: 推送**未注册**，仍在纯轮询 —— 这就是慢的原因。");
            return 3;
        }

        if (_activityCount == 0)
        {
            Console.WriteLine();
            Console.WriteLine("判定: 推送已注册，但本轮没收到任何回调。");
            Console.WriteLine("      若你刚才确实按了播放，说明 OnSessionCreated 没触发，");
            Console.WriteLine("      播放路径仍依赖轮询。");
            return 4;
        }

        Console.WriteLine();
        Console.WriteLine("判定: 推送工作正常（收到 " + _activityCount + " 次回调）。");
        return 0;
    }

    private static void OnActivity()
    {
        lock (Gate)
        {
            _activityCount++;
            _lastActivityAt = DateTime.UtcNow;
        }
    }

    private static void PrintInfo()
    {
        Console.WriteLine("=== 能力检查 ===");

        AudioSessionProvider p = new AudioSessionProvider();
        p.Refresh();

        Console.WriteLine("SMTC 可用      : " + SmtcBridge.IsAvailable);
        Console.WriteLine("设备变更推送   : " + (AudioDeviceWatcher.IsActive ? "已注册" : "未注册"));
        Console.WriteLine("会话事件推送   : " + (AudioSessionProvider.NotifyActive ? "已注册" : "未注册"));
        Console.WriteLine("音频会话数     : " + p.Sessions.Count);

        int active = 0;
        foreach (AudioSession s in p.Sessions)
        {
            if (s.State == 1) active++;
        }
        Console.WriteLine("其中 Active    : " + active);

        if (!string.IsNullOrEmpty(p.LastError))
            Console.WriteLine("LastError      : " + p.LastError);
    }

    private static int GetInt(string[] args, string name, int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name)
            {
                int v;
                if (int.TryParse(args[i + 1], out v)) return v;
            }
        return fallback;
    }

    private static bool HasFlag(string[] args, string name)
    {
        foreach (string a in args) if (a == name) return true;
        return false;
    }
}
