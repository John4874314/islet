// PauseProbe -- 判定「暂停时音频会话到底变成什么样」。
//
// 这个答案决定撤条逻辑该怎么写：
//   · 暂停后 session 还在、state 变 0  → 用 State 判定即可，立刻能发现
//   · 暂停后 session 直接消失           → 只能等它消失，或靠 SMTC
//
// 每 100ms 采样一次，只在快照变化时打印，所以输出很短。
// 用法：PauseProbe.exe --watch 90

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Islet.Sources;

internal static class PauseProbe
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        int watchSec = 90;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--watch") int.TryParse(args[i + 1], out watchSec);

        Console.WriteLine("监听 " + watchSec + " 秒，每 100ms 采样。");
        Console.WriteLine("请现在：播放音乐 → 等 5 秒 → 暂停 → 观察变化");
        Console.WriteLine();
        Console.WriteLine("时间           会话数  Active  明细(名字=state)");
        Console.WriteLine("------------------------------------------------------------");

        AudioSessionProvider p = new AudioSessionProvider();
        p.Refresh();

        string prev = null;
        Stopwatch sw = Stopwatch.StartNew();
        DateTime? lastActiveAt = null;

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
                if (active > 0) lastActiveAt = DateTime.Now;

                Console.WriteLine(string.Format(
                    "{0:HH:mm:ss.fff}  {1,6}  {2,6}  {3}",
                    DateTime.Now, p.Sessions.Count, active,
                    string.Join(",", parts.ToArray())));

                if (prev != null)
                {
                    Console.WriteLine("            ↑ 距上次 Active 已过 " +
                        (lastActiveAt.HasValue
                            ? ((DateTime.Now - lastActiveAt.Value).TotalSeconds).ToString("F1")
                            : "?") + " 秒");
                }

                prev = sig;
            }

            Thread.Sleep(100);
        }

        return 0;
    }
}
