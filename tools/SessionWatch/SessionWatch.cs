// SessionWatch -- 高频盯住音频会话状态，用来判定「暂停时会话到底怎么了」。
//
// 这个问题的答案决定「停止」该怎么加速：
//   · 如果暂停后会话**消失**（数量 -1）→ OnSessionCreated 帮不上，只能轮询，
//     但可以把轮询压到很短。
//   · 如果暂停后会话**还在、只是 State 变 0** → 同样只能轮询发现，
//     因为 IAudioSessionNotification 只报「新会话」，不报状态变化。
//
// 用法：
//   SessionWatch.exe --watch 40
//
// 每 50ms 采样一次，只在状态真的变了才打印，所以输出很短。
// 它**不注入输入**，请手动按播放/暂停来观察。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Islet.Sources;

internal static class SessionWatch
{
    private static int _pushCount;
    private static DateTime _lastPushAt = DateTime.MinValue;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        int watchSec = GetInt(args, "--watch", 40);

        Console.WriteLine("推送已注册: " + AudioSessionProvider.NotifyActive);
        Console.WriteLine("监听 " + watchSec + " 秒，每 50ms 采样，只在变化时打印。");
        Console.WriteLine("现在请手动：播放 → 暂停 → 播放 → 停止");
        Console.WriteLine();
        Console.WriteLine("时间           会话数  Active  明细                            距上次推送");
        Console.WriteLine("--------------------------------------------------------------------------");

        AudioSessionProvider.SessionActivity += delegate
        {
            Interlocked.Increment(ref _pushCount);
            _lastPushAt = DateTime.UtcNow;
        };

        AudioSessionProvider p = new AudioSessionProvider();
        p.Refresh();

        string prev = null;
        Stopwatch sw = Stopwatch.StartNew();
        int changes = 0;

        while (sw.Elapsed.TotalSeconds < watchSec)
        {
            p.Refresh();

            int active = 0;
            List<string> parts = new List<string>();
            foreach (AudioSession s in p.Sessions)
            {
                if (string.Equals(s.DisplayName, "Islet", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (s.State == 1) active++;
                parts.Add(s.DisplayName + "=" + s.State);
            }
            parts.Sort(StringComparer.OrdinalIgnoreCase);

            string sig = p.Sessions.Count + "|" + active + "|" + string.Join(",", parts.ToArray());

            if (sig != prev)
            {
                changes++;
                double sincePush = _lastPushAt == DateTime.MinValue
                    ? -1
                    : (DateTime.UtcNow - _lastPushAt).TotalMilliseconds;

                Console.WriteLine(string.Format(
                    "{0:HH:mm:ss.fff}  {1,6}  {2,6}  {3,-32} {4}",
                    DateTime.Now,
                    p.Sessions.Count,
                    active,
                    string.Join(",", parts.ToArray()),
                    sincePush < 0 ? "-" : sincePush.ToString("F0") + "ms"));

                prev = sig;
            }

            Thread.Sleep(50);
        }

        Console.WriteLine();
        Console.WriteLine("---------------------------------------");
        Console.WriteLine("状态变化次数: " + changes);
        Console.WriteLine("推送回调次数: " + _pushCount);

        if (_pushCount == 0)
            Console.WriteLine("结论: 播放/暂停**不会**触发 OnSessionCreated，停止只能靠轮询。");
        else
            Console.WriteLine("结论: 推送在工作（" + _pushCount + " 次）。");

        return 0;
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
}
