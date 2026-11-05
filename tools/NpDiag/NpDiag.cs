// 直接驱动 NowPlayingProvider.FindPlaying()，看回落路径到底认定「谁在出声」。
//
// 岛不显示媒体条时，可能断在三个地方：
//   1. FindPlaying() 返回空（筛选条件把它排掉了）
//   2. 返回了但 signature 没变（去重逻辑挡住）
//   3. 发布了但槽位/评分没让它上岛
// 这里先定位第 1 步。
using System;
using System.Collections.Generic;
using Islet.Sources;

internal static class NpDiag
{
    // NowPlayingProvider 是 internal，探针访问不到。
    // 这里用同样的公开 API + 同样的筛选条件复现它：
    //   State==1 && CanControl && DisplayName != "Islet"
    private static void Main()
    {
        using (AudioSessionProvider p = new AudioSessionProvider())
        {
            for (int round = 1; round <= 3; round++)
            {
                if (!p.Refresh())
                {
                    Console.WriteLine("Round" + round + " Refresh 失败: "
                        + (p.LastError ?? "?"));
                    return;
                }

                Console.WriteLine("Round" + round + " 全部会话 " + p.Sessions.Count + " 个：");
                int pass = 0;
                foreach (AudioSession s in p.Sessions)
                {
                    bool active = s.State == 1;
                    bool ctrl = s.CanControl;
                    bool self = string.Equals(s.DisplayName, "Islet",
                                              StringComparison.OrdinalIgnoreCase);
                    bool kept = active && ctrl && !self;

                    Console.WriteLine(string.Format(
                        "   {0} name={1,-14} state={2} ctrl={3,-5} 自己={4,-5} => {5}",
                        kept ? "✓" : " ", s.DisplayName, s.State, ctrl, self,
                        kept ? "上岛" : "被排除"));
                    if (kept) pass++;
                }

                Console.WriteLine("   —— 通过筛选: " + pass + " 个");
                if (round < 3) System.Threading.Thread.Sleep(1200);
            }
        }
    }
}
