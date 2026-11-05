// MediaProbe -- 直接问 SMTC：此刻系统里有哪些媒体会话。
//
// 为什么要单独写探针：
//   「网易云播放时岛上没反应」可能是三件事：
//     1) 数据源开关被关了（看 settings.ini 就知道）
//     2) SMTC 读不到网易云（网易云某些版本不注册 SMTC）
//     3) MediaProvider 的反射路径在本机失效
//   三者表现完全一样，不实测分不清。
//
// 编译：
//   csc /nologo /target:exe /out:build\MediaProbe.exe ^
//       /r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll ^
//       islet\tools\MediaProbe.cs
// 运行：
//   build\MediaProbe.exe              列一次
//   build\MediaProbe.exe --watch 60   连续观察 60 秒
using System;
using System.Collections;
using System.Reflection;
using System.Threading;

namespace Islet.Tools
{
    internal static class MediaProbe
    {
        private const string WinRtSuffix =
            ", Version=255.255.255.255, Culture=neutral, PublicKeyToken=null, "
            + "ContentType=WindowsRuntime";

        private static Type Find(string ns, string asm)
        {
            return Type.GetType(ns + ", " + asm + WinRtSuffix, false);
        }

        private static object Await(object op, Type closedAsync, int timeoutMs)
        {
            if (op == null || closedAsync == null || !closedAsync.IsGenericType) return null;
            try
            {
                Type open = closedAsync.GetGenericTypeDefinition();
                Type result = closedAsync.GetGenericArguments()[0];

                MethodInfo asTask = null;
                foreach (MethodInfo m in typeof(System.WindowsRuntimeSystemExtensions)
                             .GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "AsTask") continue;
                    if (m.GetGenericArguments().Length != 1) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    Type pt = ps[0].ParameterType;
                    if (!pt.IsGenericType) continue;
                    if (pt.GetGenericTypeDefinition() != open) continue;
                    asTask = m;
                    break;
                }
                if (asTask == null) return null;

                object task = asTask.MakeGenericMethod(result)
                    .Invoke(null, new object[] { op });
                Type tt = task.GetType();
                bool done = (bool)tt.GetMethod("Wait", new Type[] { typeof(int) })
                    .Invoke(task, new object[] { timeoutMs });
                if (!done) return null;

                PropertyInfo rp = tt.GetProperty("Result");
                return rp == null ? null : rp.GetValue(task, null);
            }
            catch (Exception ex) { Console.WriteLine("  [await] " + Msg(ex)); return null; }
        }

        private static string Msg(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.GetType().Name + ": " + ex.Message;
        }

        private static string Str(object o, string p)
        {
            if (o == null) return null;
            PropertyInfo pi = o.GetType().GetProperty(p);
            if (pi == null) return null;
            try { object v = pi.GetValue(o, null); return v == null ? null : v.ToString(); }
            catch (Exception) { return null; }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            bool watch = args.Length >= 2 && args[0] == "--watch";
            int secs = watch ? int.Parse(args[1]) : 0;

            Console.WriteLine("===== SMTC 媒体会话探针 =====");
            Console.WriteLine();

            if (!watch)
            {
                Dump();
                return 0;
            }

            Console.WriteLine("连续观察 " + secs + " 秒（每 2 秒）...");
            int ticks = secs / 2;
            for (int i = 0; i < ticks; i++)
            {
                Console.Write(string.Format("t={0,3}s  ", (i + 1) * 2));
                DumpOneLine();
                Thread.Sleep(2000);
            }
            return 0;
        }

        private static object GetManager()
        {
            Type mgr = Find("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager",
                            "Windows.Media");
            if (mgr == null) return null;

            MethodInfo req = mgr.GetMethod("RequestAsync",
                BindingFlags.Public | BindingFlags.Static);
            if (req == null) return null;

            object op = req.Invoke(null, null);
            return Await(op, req.ReturnType, 4000);
        }

        private static void Dump()
        {
            object manager = GetManager();
            if (manager == null)
            {
                Console.WriteLine("拿不到 SessionManager（SMTC 不可用）");
                return;
            }
            Console.WriteLine("SessionManager 已获取");
            Console.WriteLine();

            IEnumerable sessions = null;
            PropertyInfo all = manager.GetType().GetProperty("GetSessions");
            if (all != null)
            {
                try { sessions = all.GetValue(manager, null) as IEnumerable; }
                catch (Exception) { }
            }

            int n = 0;
            if (sessions != null)
            {
                Console.WriteLine("--- 所有会话 ---");
                foreach (object s in sessions)
                {
                    n++;
                    PrintSession(n, s, true);
                }
            }
            if (n == 0) Console.WriteLine("--- 所有会话 ---（无）");

            Console.WriteLine();
            object cur = InvokeSafe(manager, "GetCurrentSession");
            Console.WriteLine("--- 当前会话（Islet 实际用的就是这个）---");
            if (cur == null) Console.WriteLine("  null（岛上不会有任何显示）");
            else PrintSession(0, cur, true);
        }

        private static void DumpOneLine()
        {
            object manager = GetManager();
            if (manager == null) { Console.WriteLine("SMTC 不可用"); return; }

            object cur = InvokeSafe(manager, "GetCurrentSession");
            if (cur == null) { Console.WriteLine("当前会话=null"); return; }

            string app = Str(cur, "SourceAppUserModelId");
            object info = InvokeSafe(cur, "GetPlaybackInfo");
            string status = info == null ? "?" : Str(info, "PlaybackStatus");
            Console.WriteLine("app=" + (app ?? "?") + " status=" + (status ?? "?"));
        }

        private static object InvokeSafe(object o, string method)
        {
            if (o == null) return null;
            try
            {
                MethodInfo m = o.GetType().GetMethod(method);
                return m == null ? null : m.Invoke(o, null);
            }
            catch (Exception) { return null; }
        }

        private static void PrintSession(int idx, object s, bool detail)
        {
            string app = Str(s, "SourceAppUserModelId");
            object info = InvokeSafe(s, "GetPlaybackInfo");
            string status = info == null ? "?" : Str(info, "PlaybackStatus");

            string title = null, artist = null;
            try
            {
                MethodInfo m = s.GetType().GetMethod("TryGetMediaPropertiesAsync");
                if (m != null)
                {
                    object op = m.Invoke(s, null);
                    object props = Await(op, m.ReturnType, 2000);
                    title = Str(props, "Title");
                    artist = Str(props, "Artist");
                }
            }
            catch (Exception) { }

            Console.WriteLine(string.Format("  [{0}] app={1}", idx, app ?? "?"));
            Console.WriteLine(string.Format("      status={0}  title=[{1}]  artist=[{2}]",
                status ?? "?", title ?? "", artist ?? ""));
        }
    }
}
