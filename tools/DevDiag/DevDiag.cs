// 验证 AudioSessionProvider 的设备变化检测不会误判。
//
// 两个必须成立的性质：
//   1. 连续多次 Refresh，DeviceChangedLastRefresh 必须一直是 false
//      —— 否则每 3 秒重建一次 manager，音频引擎里又会积会话（之前踩过 2.9GB 那个坑）。
//   2. DeviceId 要稳定拿到，且两次 Refresh 的值一致。
//   3. 释放重建（Dispose 后再 new）也不应误报。
using System;
using Islet.Sources;

internal static class DevDiag
{
    private static void Main()
    {
        using (AudioSessionProvider p = new AudioSessionProvider())
        {
            for (int i = 1; i <= 4; i++)
            {
                bool ok = p.Refresh();
                Console.WriteLine(string.Format(
                    "Refresh#{0} ok={1} device={2} changed={3} sessions={4} err={5}",
                    i, ok, p.DeviceId ?? "(null)",
                    p.DeviceChangedLastRefresh, p.Sessions.Count,
                    p.LastError ?? "-"));
            }
        }

        Console.WriteLine();

        // 新实例：首次绑定不应算「切换」
        using (AudioSessionProvider q = new AudioSessionProvider())
        {
            q.Refresh();
            Console.WriteLine("新实例首次 Refresh: changed=" + q.DeviceChangedLastRefresh
                + " device=" + (q.DeviceId ?? "(null)"));
        }

        Console.WriteLine();
        Console.WriteLine("判定：上面 changed 全为 False 才算通过（无设备切换时的正确行为）");
    }
}
