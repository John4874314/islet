// 直接引用 Islet.exe，用真实 AudioSessionProvider 验证「设置音量是否生效」。
//
// 为什么要这个探针：独立探针用 QI 能写成功，但应用内滑块会被弹回，
// 说明 provider 这条路径上有别的问题（Control 为空？hr 失败？）。
// 这里把每一步都打印出来，直接定位，不再靠猜。
using System;
using Islet.Sources;

internal static class ProvDiag
{
    private static void Main()
    {
        using (AudioSessionProvider p = new AudioSessionProvider())
        {
            bool ok = p.Refresh();
            Console.WriteLine("Refresh=" + ok + " err=" + (p.LastError ?? "(none)"));
            Console.WriteLine("sessions=" + p.Sessions.Count);
            Console.WriteLine();

            AudioSession target = null;
            foreach (AudioSession s in p.Sessions)
            {
                Console.WriteLine(string.Format(
                    "  key={0,-30} name={1,-14} state={2} vol={3,5:F0}% ctrl={4}",
                    Shorten(s.Key), s.DisplayName, s.State,
                    s.Volume * 100f, s.CanControl));
                if (target == null && s.IsActive && s.CanControl) target = s;
            }

            if (target == null)
            {
                foreach (AudioSession s in p.Sessions)
                {
                    if (s.CanControl) { target = s; break; }
                }
            }

            if (target == null)
            {
                Console.WriteLine();
                Console.WriteLine("没有可控会话，无法测试");
                return;
            }

            Console.WriteLine();
            Console.WriteLine("目标: " + target.DisplayName + "  key=" + Shorten(target.Key));
            float orig = target.Volume;

            // 写 40%
            float r = p.SetVolume(target, 0.40f);
            Console.WriteLine("SetVolume(0.40) -> " + r.ToString("F3")
                + "  （-1 表示失败）");
            Console.WriteLine("LastWriteError = " + (p.LastWriteError ?? "(none)"));

            // 再 Refresh 一次读真实值（绕开缓存）
            System.Threading.Thread.Sleep(300);
            p.Refresh();
            foreach (AudioSession s in p.Sessions)
            {
                if (s.Key == target.Key)
                {
                    Console.WriteLine("Refresh 后该会话真实音量 = "
                        + (s.Volume * 100f).ToString("F0") + "%");
                    break;
                }
            }

            // 恢复
            foreach (AudioSession s in p.Sessions)
            {
                if (s.Key == target.Key)
                {
                    p.SetVolume(s, orig);
                    Console.WriteLine("已恢复为 " + (orig * 100f).ToString("F0") + "%");
                    break;
                }
            }
        }
    }

    private static string Shorten(string s)
    {
        if (string.IsNullOrEmpty(s)) return "(null)";
        return s.Length <= 30 ? s : "…" + s.Substring(s.Length - 29);
    }
}
