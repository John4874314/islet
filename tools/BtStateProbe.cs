// BtStateProbe -- 验证「蓝牙音频设备连接状态」这条路能不能做成常驻轮询。
//
// 要回答三个决定性问题：
//   1) 后台线程（Timer 回调，MTA 线程池）能不能调 WinRT？
//      Provider 是 Timer 驱动的，如果只能在 STA 主线程调，整个方案就得改。
//   2) 能不能区分「耳机」和「鼠标/键盘」等其它蓝牙设备？
//      不区分的话，用户接个蓝牙鼠标都会上岛，变成噪音。
//   3) 连接/断开的变化，轮询能不能稳定捕捉到？
//
// 编译：
//   csc /nologo /target:exe /out:build\BtStateProbe.exe ^
//       /r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll ^
//       islet\tools\BtStateProbe.cs
// 运行：
//   build\BtStateProbe.exe            当前状态 + 后台线程测试
//   build\BtStateProbe.exe --watch 60 连续观察 60 秒，看变化能否捕捉
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace Islet.Tools
{
    internal static class BtStateProbe
    {
        private const string WinRtSuffix =
            ", Version=255.255.255.255, Culture=neutral, PublicKeyToken=null, "
            + "ContentType=WindowsRuntime";

        private static Type FindType(string nsAndName)
        {
            return Type.GetType(nsAndName + ", Windows.Devices" + WinRtSuffix, false);
        }

        // ---------------------------------------------------------------
        // WinRT 异步等待（与 BtProbe 同一套，已验证可用）
        // ---------------------------------------------------------------
        private static object AwaitOp(object op, Type closedAsyncType, int timeoutMs)
        {
            if (op == null || closedAsyncType == null || !closedAsyncType.IsGenericType)
                return null;

            try
            {
                Type openAsync = closedAsyncType.GetGenericTypeDefinition();
                Type resultType = closedAsyncType.GetGenericArguments()[0];

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
                    if (pt.GetGenericTypeDefinition() != openAsync) continue;
                    asTask = m;
                    break;
                }
                if (asTask == null) return null;

                object task = asTask.MakeGenericMethod(resultType)
                    .Invoke(null, new object[] { op });
                Type tt = task.GetType();
                bool done = (bool)tt.GetMethod("Wait", new Type[] { typeof(int) })
                    .Invoke(task, new object[] { timeoutMs });
                if (!done) return null;

                PropertyInfo rp = tt.GetProperty("Result");
                return rp == null ? null : rp.GetValue(task, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine("    [await] " + Inner(ex));
                return null;
            }
        }

        private static string Inner(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.GetType().Name + ": " + ex.Message;
        }

        private static string Prop(object o, string n)
        {
            if (o == null) return "?";
            PropertyInfo p = o.GetType().GetProperty(n);
            if (p == null) return "?";
            try
            {
                object v = p.GetValue(o, null);
                return v == null ? "(null)" : v.ToString();
            }
            catch (Exception) { return "?"; }
        }

        private static object PropObj(object o, string n)
        {
            if (o == null) return null;
            PropertyInfo p = o.GetType().GetProperty(n);
            return p == null ? null : p.GetValue(o, null);
        }

        // ---------------------------------------------------------------
        // 核心：枚举并判定
        // ---------------------------------------------------------------

        private sealed class Dev
        {
            public string Name;
            public string Id;
            public string Status;       // Connected / Disconnected / ...
            public bool IsAudio;        // 是不是音频设备（耳机/音箱）
            public string Evidence;     // 判定依据，便于排查
        }

        /// <summary>
        /// 蓝牙无线电是否开启。
        ///
        /// 为什么必须先查这个：
        /// 蓝牙关着时，FromIdAsync 会对每个设备抛
        ///   0x800710DF ERROR_DEVICE_NOT_READY「设备未就绪」
        /// 不先拦掉，日志会被刷屏，而且拿不到任何有效状态。
        /// 实测：关蓝牙 → 4 个设备全部抛异常；开蓝牙 → 全部正常。
        /// </summary>
        private static bool RadioOn()
        {
            try
            {
                Type t = Type.GetType("Windows.Devices.Radios.Radio, Windows.Devices"
                                      + WinRtSuffix, false);
                if (t == null) return true;   // 查不到就别拦，让它自己报错

                MethodInfo getAsync = t.GetMethod("GetRadiosAsync",
                    BindingFlags.Public | BindingFlags.Static);
                object op = getAsync.Invoke(null, null);
                object res = AwaitOp(op, getAsync.ReturnType, 4000);
                IEnumerable list = res as IEnumerable;
                if (list == null) return true;

                foreach (object r in list)
                {
                    string kind = Prop(r, "Kind");
                    if (kind.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    string state = Prop(r, "State");
                    return state.IndexOf("On", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (Exception) { }
            return true;
        }

        /// <summary>
        /// 列出所有已配对蓝牙设备及其连接状态。
        /// 蓝牙关闭时直接返回空列表，由调用方决定如何提示。
        /// </summary>
        private static List<Dev> Enumerate(int timeoutMs)
        {
            List<Dev> result = new List<Dev>();

            Type btDev = FindType("Windows.Devices.Bluetooth.BluetoothDevice");
            Type di = FindType("Windows.Devices.Enumeration.DeviceInformation");
            if (btDev == null || di == null)
            {
                Console.WriteLine("  找不到 WinRT 类型");
                return result;
            }

            if (!RadioOn())
            {
                Console.WriteLine("  蓝牙已关闭，跳过枚举");
                return result;
            }

            MethodInfo sel = btDev.GetMethod("GetDeviceSelector",
                BindingFlags.Public | BindingFlags.Static);
            string selector = (string)sel.Invoke(null, null);

            MethodInfo findAll = null;
            foreach (MethodInfo m in di.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "FindAllAsync") continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != 1 || ps[0].ParameterType != typeof(string)) continue;
                findAll = m;
                break;
            }
            if (findAll == null) return result;

            object op = findAll.Invoke(null, new object[] { selector });
            object res = AwaitOp(op, findAll.ReturnType, timeoutMs);
            IEnumerable items = res as IEnumerable;
            if (items == null) return result;

            MethodInfo fromId = null;
            foreach (MethodInfo m in btDev.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "FromIdAsync") continue;
                if (m.GetParameters().Length != 1) continue;
                fromId = m;
                break;
            }

            foreach (object devInfo in items)
            {
                Dev d = new Dev();
                d.Name = Prop(devInfo, "Name");
                d.Id = Prop(devInfo, "Id");

                // 连接状态只能通过 BluetoothDevice 拿
                if (fromId != null)
                {
                    object dop = fromId.Invoke(null, new object[] { d.Id });
                    object bt = AwaitOp(dop, fromId.ReturnType, 3000);
                    if (bt != null)
                    {
                        d.Status = Prop(bt, "ConnectionStatus");
                        MethodInfo close = bt.GetType().GetMethod("Close");
                        if (close != null) { try { close.Invoke(bt, null); } catch (Exception) { } }
                    }
                    else d.Status = "(取不到)";
                }

                ClassifyAudio(d, devInfo);
                result.Add(d);
            }

            return result;
        }

        /// <summary>
        /// 判定是不是「音频」设备。
        ///
        /// 判据优先级：
        ///   1) 设备的 Class / Category 属性里出现 Audio
        ///   2) 已配对的 PnP 设备里有对应的 AudioEndpoint（耳机/扬声器）
        /// 两条都不中就当普通蓝牙设备处理（不上岛）。
        /// </summary>
        private static void ClassifyAudio(Dev d, object devInfo)
        {
            // --- 途径 1：DeviceInformation.Properties 里的类别字段 ---
            try
            {
                IDictionary dict = PropObj(devInfo, "Properties") as IDictionary;
                if (dict != null)
                {
                    foreach (DictionaryEntry e in dict)
                    {
                        string key = e.Key == null ? "" : e.Key.ToString();
                        string val = e.Value == null ? "" : e.Value.ToString();
                        if (key.IndexOf("Class", StringComparison.OrdinalIgnoreCase) >= 0
                            || key.IndexOf("Category", StringComparison.OrdinalIgnoreCase) >= 0
                            || key.IndexOf("Cod", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (val.IndexOf("Audio", StringComparison.OrdinalIgnoreCase) >= 0
                                || val.IndexOf("Headset", StringComparison.OrdinalIgnoreCase) >= 0
                                || val.IndexOf("Headphone", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                d.IsAudio = true;
                                d.Evidence = key + "=" + val;
                            }
                        }
                    }
                }
            }
            catch (Exception) { }

            if (d.IsAudio) return;

            // --- 途径 2：PnP 里查有没有同名音频端点 ---
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    Microsoft.Win32.RegistryKey root = i == 0
                        ? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                            @"SYSTEM\CurrentControlSet\Enum\BTHENUM")
                        : null;
                    if (root == null) continue;
                    root.Close();
                }

                // 用 MMDevices 枚举音频端点，设备名里通常包含耳机名称
                using (Microsoft.Win32.RegistryKey ep = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render"))
                {
                    if (ep != null)
                    {
                        foreach (string guid in ep.GetSubKeyNames())
                        {
                            using (Microsoft.Win32.RegistryKey props =
                                ep.OpenSubKey(guid + @"\Properties"))
                            {
                                if (props == null) continue;
                                foreach (string vn in props.GetValueNames())
                                {
                                    // PKEY_Device_FriendlyName
                                    if (vn.IndexOf("a45c254e-df1c-4efd-8020-67d146a850e0",
                                                   StringComparison.OrdinalIgnoreCase) < 0) continue;
                                    object raw = props.GetValue(vn);
                                    string friendly = DecodePropString(raw);
                                    if (string.IsNullOrEmpty(friendly)) continue;
                                    if (!string.IsNullOrEmpty(d.Name)
                                        && friendly.IndexOf(d.Name,
                                            StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        d.IsAudio = true;
                                        d.Evidence = "音频端点: " + friendly;
                                        return;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>注册表里的 PROPVARIANT 字符串是 UTF-16 字节数组，需手动解码。</summary>
        private static string DecodePropString(object raw)
        {
            try
            {
                byte[] bytes = raw as byte[];
                if (bytes == null || bytes.Length < 4) return null;
                // 前 4 字节是 PROPVARIANT 头（vt + 保留），后面是 UTF-16
                return System.Text.Encoding.Unicode.GetString(bytes, 4, bytes.Length - 4)
                    .TrimEnd('\0');
            }
            catch (Exception) { return null; }
        }

        // ---------------------------------------------------------------
        // 入口
        // ---------------------------------------------------------------

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            Console.WriteLine("===== 蓝牙连接状态探针 =====");
            Console.WriteLine();

            Console.WriteLine("--- 1. 主线程（STA）枚举 ---");
            List<Dev> main = Enumerate(10000);
            Dump(main);

            Console.WriteLine();
            Console.WriteLine("--- 2. 后台线程（MTA 线程池）枚举 ---");
            Console.WriteLine("    （Provider 由 Timer 驱动，必须确认这里也能用）");
            bool ok = false;
            List<Dev> bg = null;
            Exception bgErr = null;
            Thread t = new Thread(delegate()
            {
                try
                {
                    bg = Enumerate(10000);
                    ok = bg != null && bg.Count > 0;
                }
                catch (Exception ex) { bgErr = ex; }
            });
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            t.Join(15000);

            if (bgErr != null)
                Console.WriteLine("    后台线程抛异常: " + Inner(bgErr));
            else if (bg == null)
                Console.WriteLine("    后台线程没返回结果（超时或空）");
            else
            {
                Dump(bg);
                Console.WriteLine("    >>> 后台线程可用 = " + ok);
            }

            // 可选：持续观察
            if (args.Length >= 2 && args[0] == "--watch")
            {
                int secs = int.Parse(args[1]);
                Console.WriteLine();
                Console.WriteLine("--- 3. 连续观察 " + secs + " 秒（每 2 秒一次）---");
                string lastSig = Signature(main);
                int ticks = secs / 2;
                for (int i = 0; i < ticks; i++)
                {
                    Thread.Sleep(2000);
                    List<Dev> now = Enumerate(6000);
                    string sig = Signature(now);
                    bool changed = sig != lastSig;
                    Console.WriteLine(string.Format("    t={0,3}s {1}{2}",
                        (i + 1) * 2, sig, changed ? "   <<< 变化！" : ""));
                    if (changed) lastSig = sig;
                }
            }

            return 0;
        }

        private static string Signature(List<Dev> list)
        {
            if (list == null) return "(null)";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (Dev d in list)
            {
                if (!d.IsAudio) continue;
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(d.Name).Append("=").Append(d.Status);
            }
            if (sb.Length == 0) sb.Append("(无音频设备)");
            return sb.ToString();
        }

        private static void Dump(List<Dev> list)
        {
            if (list == null) { Console.WriteLine("  (空)"); return; }
            int audio = 0;
            foreach (Dev d in list)
            {
                Console.WriteLine(string.Format("  {0,-26} {1,-14} 音频={2}  {3}",
                    Cut(d.Name, 26), d.Status, d.IsAudio ? "是" : "否",
                    d.Evidence == null ? "" : d.Evidence));
                if (d.IsAudio) audio++;
            }
            Console.WriteLine("  合计 " + list.Count + " 个已配对设备，其中音频 " + audio + " 个");
        }

        private static string Cut(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }
    }
}
