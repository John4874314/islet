// PeakProbe -- 对比 State 与音量峰值，找出「暂停」的可靠判据。
//
// 已知事实（2026-11-02 实测）：
//   网易云暂停后，音频会话的 State 仍然保持 1(Active) 约 20 秒才变 0。
//   所以 State 不能用来判断「是否正在播放」。
//
// 本探针同时读：
//   · State   —— AudioSessionControl2::GetState
//   · Peak    —— IAudioMeterInformation::GetPeakValue（会话自己的峰值）
//
// 假设：暂停后 Peak 会立刻掉到 ≈0，而 State 还要拖 20 秒。
// 如果成立，就用 Peak 当判据。
//
// 用法：PeakProbe.exe --watch 90
//       然后：播放 → 等 5 秒 → 暂停 → 观察

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Islet.Sources;

internal static class PeakProbe
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        int watchSec = 90;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--watch") int.TryParse(args[i + 1], out watchSec);

        Console.WriteLine("监听 " + watchSec + " 秒，每 150ms 采样。");
        Console.WriteLine("请现在：播放音乐 → 等 5 秒 → 暂停");
        Console.WriteLine();
        Console.WriteLine("时间         应用             State  Peak");
        Console.WriteLine("------------------------------------------------");

        AudioSessionProvider p = new AudioSessionProvider();
        p.Refresh();

        string prev = null;
        Stopwatch sw = Stopwatch.StartNew();

        while (sw.Elapsed.TotalSeconds < watchSec)
        {
            p.Refresh();

            List<string> parts = new List<string>();
            foreach (AudioSession s in p.Sessions)
            {
                if (string.Equals(s.DisplayName, "Islet", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (s.IsSystemSounds) continue;

                float peak = s.Peak;
                // 峰值量化到 2 位小数，避免小抖动刷屏
                parts.Add(s.DisplayName + " State=" + s.State + " Peak=" + peak.ToString("F3"));
            }
            parts.Sort(StringComparer.OrdinalIgnoreCase);
            string sig = string.Join(" | ", parts.ToArray());

            if (sig != prev)
            {
                Console.WriteLine(string.Format("{0:HH:mm:ss.fff}  {1}", DateTime.Now, sig));
                prev = sig;
            }

            Thread.Sleep(150);
        }

        return 0;
    }
}
