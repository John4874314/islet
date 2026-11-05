// 验证「高频 Refresh 是否会累积引擎侧音频会话」。
//
// 这决定了能不能把轮询间隔从 30 秒缩短。
// 早先的结论是「2 秒轮询一小时攒两千个僵尸会话」，但那是**每次 new 一个 provider**
// 的写法。现在 provider 会复用 _manager 和 _enumerator，开销应该低得多。
//
// 本探针用同一个 provider 连续 Refresh N 次，记录：
//   · 系统侧会话总数（用全新 provider 读，代表真实存量）
//   · 耗时
using System;
using System.Diagnostics;
using Islet.Sources;

internal static class PollCost
{
    private static void Main()
    {
        Console.WriteLine("=== 基线：新建 provider 读一次 ===");
        int baseline = CountFresh();
        Console.WriteLine("系统会话总数 = " + baseline);
        Console.WriteLine();

        Console.WriteLine("=== 同一个 provider 连续 Refresh 600 次（约 10 秒 @16ms）===");
        using (AudioSessionProvider p = new AudioSessionProvider())
        {
            p.Refresh();   // 预热，把 manager 建起来

            Stopwatch sw = Stopwatch.StartNew();
            const int N = 600;
            int ok = 0;
            for (int i = 0; i < N; i++)
            {
                if (p.Refresh()) ok++;
            }
            sw.Stop();

            Console.WriteLine(string.Format(
                "{0} 次 Refresh，成功 {1}，总耗时 {2:F0}ms，单次平均 {3:F3}ms",
                N, ok, sw.Elapsed.TotalMilliseconds,
                sw.Elapsed.TotalMilliseconds / N));
            Console.WriteLine("最后一次会话数 = " + p.Sessions.Count);
        }

        Console.WriteLine();
        Console.WriteLine("=== 600 次之后的系统会话总数 ===");
        int after = CountFresh();
        Console.WriteLine("系统会话总数 = " + after);
        Console.WriteLine("增量 = " + (after - baseline));
        Console.WriteLine();
        Console.WriteLine("增量接近 0 → 高频轮询安全，可以缩短间隔");
        Console.WriteLine("增量明显 → 保持 30 秒，改用别的办法提速");
    }

    private static int CountFresh()
    {
        using (AudioSessionProvider p = new AudioSessionProvider())
        {
            p.Refresh();
            return p.Sessions.Count;
        }
    }
}
