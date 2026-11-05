// BtProbe -- 验证蓝牙耳机状态的可行路径。
//
// 「读蓝牙耳机电量」在 Windows 上没有统一 API，可行性与设备强相关：
//   - 经典蓝牙（A2DP/HFP）耳机：Windows 基本不暴露电量
//   - 支持 GATT Battery Service 的 BLE 设备：可以读
//   - 厂商私有协议：读不到
// 所以必须先在真机上确认「这台机器到底能拿到什么」，再决定功能边界。
//
// 本探针不用 WinRT 强类型（本机没有 SDK 的 Windows.winmd），
// 全程反射 + AsTask 等待，只依赖系统自带的 WinMetadata。
//
// 编译：
//   csc /nologo /target:exe /out:build\BtProbe.exe ^
//       /r:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll ^
//       islet\tools\BtProbe.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace Islet.Tools
{
    internal static class BtProbe
    {
        private const string WinRtSuffix =
            ", Version=255.255.255.255, Culture=neutral, PublicKeyToken=null, "
            + "ContentType=WindowsRuntime";

        private static Type FindType(string nsAndName, string assembly)
        {
            return Type.GetType(nsAndName + ", " + assembly + WinRtSuffix, false);
        }

        // ---------------------------------------------------------------
        // WinRT 异步等待
        // ---------------------------------------------------------------

        /// <summary>
        /// 等待 WinRT 异步操作并取回结果。
        ///
        /// 本机没有 SDK 的 Windows.winmd，编译器看不到 IAsyncOperation&lt;T&gt;，
        /// 所以全程走反射。这里有三个坑，都记下来：
        ///
        ///   1) op.GetType() 是 __ComObject，看不到任何接口成员，
        ///      既不能拿泛型参数、也不能直接反射 Status/GetResults。
        ///
        ///   2) AsTask&lt;TResult&gt;(IAsyncOperation&lt;TResult&gt;) 里的 TResult
        ///      必须是**具体类型**。从「开放泛型」上取 GetGenericArguments()[0]
        ///      拿到的是泛型参数本身，MakeGenericMethod 会抛
        ///      「不能对 ContainsGenericParameters 为 True 的类型执行后期绑定」。
        ///      正确来源是**方法签名的 ReturnType** —— 它已经闭合。
        ///
        ///   3) 必须挑对重载：1 个泛型参数 + 1 个普通参数，且参数类型是
        ///      IAsyncOperation`1。写成「参数类型是泛型参数」永远匹配不到，
        ///      表现是静默超时，极难排查。
        /// </summary>
        private static object AwaitOp(object op, Type closedAsyncType, int timeoutMs)
        {
            if (op == null) return null;
            if (closedAsyncType == null || !closedAsyncType.IsGenericType)
            {
                Console.WriteLine("  [await] 缺少闭合的 IAsyncOperation 类型");
                return null;
            }

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

                if (asTask == null)
                {
                    Console.WriteLine("  [await] 找不到匹配的 AsTask 重载");
                    return null;
                }

                MethodInfo closed = asTask.MakeGenericMethod(resultType);
                object task = closed.Invoke(null, new object[] { op });

                Type tt = task.GetType();
                MethodInfo waitM = tt.GetMethod("Wait", new Type[] { typeof(int) });
                bool done = (bool)waitM.Invoke(task, new object[] { timeoutMs });
                if (!done)
                {
                    Console.WriteLine("  [await] 超时 " + timeoutMs + "ms");
                    return null;
                }

                PropertyInfo resultP = tt.GetProperty("Result");
                return resultP == null ? null : resultP.GetValue(task, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [await 失败] " + Inner(ex));
                return null;
            }
        }

        private static string Inner(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.GetType().Name + ": " + ex.Message;
        }

        private static string Prop(object o, string name)
        {
            if (o == null) return "?";
            PropertyInfo p = o.GetType().GetProperty(name);
            if (p == null) return "?";
            try
            {
                object v = p.GetValue(o, null);
                return v == null ? "(null)" : v.ToString();
            }
            catch (Exception) { return "?"; }
        }

        private static object PropObj(object o, string name)
        {
            if (o == null) return null;
            PropertyInfo p = o.GetType().GetProperty(name);
            if (p == null) return null;
            try { return p.GetValue(o, null); }
            catch (Exception) { return null; }
        }

        // ---------------------------------------------------------------
        // 入口
        // ---------------------------------------------------------------

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            Console.WriteLine("===== 蓝牙状态探针 =====");
            Console.WriteLine();

            TryAdapter();
            Console.WriteLine();

            TryPairedDevices();
            Console.WriteLine();

            TryBleBattery();
            Console.WriteLine();

            Console.WriteLine("说明：经典蓝牙（A2DP）耳机通常不暴露电量；");
            Console.WriteLine("      只有支持 GATT Battery Service 的 BLE 设备能读到。");
            return 0;
        }

        // ---------------------------------------------------------------
        // 1. 适配器
        // ---------------------------------------------------------------

        private static void TryAdapter()
        {
            Console.WriteLine("--- 1. 蓝牙适配器 ---");
            try
            {
                Type t = FindType("Windows.Devices.Radios.Radio", "Windows.Devices");
                if (t == null) { Console.WriteLine("  找不到 Radio 类型"); return; }

                MethodInfo getAllAsync = t.GetMethod("GetRadiosAsync",
                    BindingFlags.Public | BindingFlags.Static);
                object op = getAllAsync.Invoke(null, null);
                object listObj = AwaitOp(op, getAllAsync.ReturnType, 5000);
                IEnumerable list = listObj as IEnumerable;
                if (list == null) { Console.WriteLine("  取适配器列表失败"); return; }

                int n = 0;
                foreach (object radio in list)
                {
                    n++;
                    Console.WriteLine(string.Format("  [{0}] name={1} kind={2} state={3}",
                        n, Prop(radio, "Name"), Prop(radio, "Kind"), Prop(radio, "State")));
                }
                if (n == 0) Console.WriteLine("  没有无线电设备");
            }
            catch (Exception ex) { Console.WriteLine("  [异常] " + Inner(ex)); }
        }

        // ---------------------------------------------------------------
        // 2. 已配对设备 + 连接状态
        // ---------------------------------------------------------------

        private static void TryPairedDevices()
        {
            Console.WriteLine("--- 2. 已配对蓝牙设备（含连接状态）---");
            try
            {
                Type btDev = FindType("Windows.Devices.Bluetooth.BluetoothDevice",
                                      "Windows.Devices");
                Type di = FindType("Windows.Devices.Enumeration.DeviceInformation",
                                   "Windows.Devices");
                if (btDev == null || di == null)
                {
                    Console.WriteLine("  找不到 BluetoothDevice / DeviceInformation");
                    return;
                }

                MethodInfo sel = btDev.GetMethod("GetDeviceSelector",
                    BindingFlags.Public | BindingFlags.Static);
                string selector = (string)sel.Invoke(null, null);

                foreach (object dev in FindAll(di, selector))
                {
                    Console.WriteLine(string.Format("  {0}  enabled={1}",
                        Prop(dev, "Name"), Prop(dev, "IsEnabled")));

                    TryConnectionStatus(btDev, dev);
                    TryBatteryFromDeviceInfo(dev);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [异常] " + Inner(ex)); }
        }

        /// <summary>DeviceInformation → BluetoothDevice → ConnectionStatus。</summary>
        private static void TryConnectionStatus(Type btDev, object devInfo)
        {
            try
            {
                MethodInfo fromId = null;
                foreach (MethodInfo m in btDev.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "FromIdAsync") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    fromId = m;
                    break;
                }
                if (fromId == null) return;

                string id = Prop(devInfo, "Id");
                if (string.IsNullOrEmpty(id) || id == "?") return;

                object op = fromId.Invoke(null, new object[] { id });
                object bt = AwaitOp(op, fromId.ReturnType, 4000);
                if (bt == null) { Console.WriteLine("      (FromId 失败)"); return; }

                Console.WriteLine("      ConnectionStatus = " + Prop(bt, "ConnectionStatus"));

                MethodInfo close = bt.GetType().GetMethod("Close");
                if (close != null) { try { close.Invoke(bt, null); } catch (Exception) { } }
            }
            catch (Exception ex) { Console.WriteLine("      [连接状态] " + Inner(ex)); }
        }

        /// <summary>从 DeviceInformation 的属性里找电量相关项。</summary>
        private static void TryBatteryFromDeviceInfo(object devInfo)
        {
            try
            {
                object props = PropObj(devInfo, "Properties");
                IDictionary dict = props as IDictionary;
                if (dict == null) return;

                bool any = false;
                foreach (DictionaryEntry e in dict)
                {
                    string key = e.Key == null ? "" : e.Key.ToString();
                    if (key.IndexOf("Battery", StringComparison.OrdinalIgnoreCase) < 0
                        && key.IndexOf("Power", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    any = true;
                    Console.WriteLine("      " + key + " = " + e.Value);
                }
                if (!any) Console.WriteLine("      (属性里没有电量字段)");
            }
            catch (Exception ex) { Console.WriteLine("      [属性] " + Inner(ex)); }
        }

        private static List<object> FindAll(Type di, string selector)
        {
            List<object> result = new List<object>();
            if (string.IsNullOrEmpty(selector)) return result;

            try
            {
                foreach (MethodInfo m in di.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "FindAllAsync") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1 || ps[0].ParameterType != typeof(string)) continue;

                    object op = m.Invoke(null, new object[] { selector });
                    object res = AwaitOp(op, m.ReturnType, 8000);
                    IEnumerable items = res as IEnumerable;
                    if (items == null) return result;

                    foreach (object d in items) result.Add(d);
                    break;
                }
            }
            catch (Exception ex) { Console.WriteLine("  [FindAll] " + Inner(ex)); }
            return result;
        }

        // ---------------------------------------------------------------
        // 3. BLE 电量
        // ---------------------------------------------------------------

        private static void TryBleBattery()
        {
            Console.WriteLine("--- 3. BLE 设备电量（GATT Battery Service）---");
            try
            {
                Type ble = FindType("Windows.Devices.Bluetooth.BluetoothLEDevice",
                                    "Windows.Devices");
                Type di = FindType("Windows.Devices.Enumeration.DeviceInformation",
                                   "Windows.Devices");
                if (ble == null || di == null)
                {
                    Console.WriteLine("  找不到 BluetoothLEDevice");
                    return;
                }

                MethodInfo sel = ble.GetMethod("GetDeviceSelectorFromPairingState",
                    BindingFlags.Public | BindingFlags.Static);
                string selector = sel == null
                    ? null
                    : (string)sel.Invoke(null, new object[] { true });

                List<object> items = FindAll(di, selector);
                if (items.Count == 0)
                {
                    Console.WriteLine("  没有已配对的 BLE 设备");
                    Console.WriteLine("  → 若耳机是经典蓝牙（A2DP），这条路读不到电量。");
                    return;
                }

                foreach (object dev in items)
                {
                    Console.WriteLine("  BLE: " + Prop(dev, "Name"));
                    TryBleDeviceBattery(ble, dev);
                }
            }
            catch (Exception ex) { Console.WriteLine("  [异常] " + Inner(ex)); }
        }

        /// <summary>BluetoothLEDevice → GetGattServicesForUuidAsync → Battery Level。</summary>
        private static void TryBleDeviceBattery(Type ble, object devInfo)
        {
            try
            {
                MethodInfo fromId = null;
                foreach (MethodInfo m in ble.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "FromIdAsync") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1) continue;
                    fromId = m;
                    break;
                }
                if (fromId == null) return;

                string id = Prop(devInfo, "Id");
                object op = fromId.Invoke(null, new object[] { id });
                object device = AwaitOp(op, fromId.ReturnType, 5000);
                if (device == null) { Console.WriteLine("    FromId 失败"); return; }

                Console.WriteLine("    ConnectionStatus = " + Prop(device, "ConnectionStatus"));

                // Battery Service 标准 UUID = 0000180F-0000-1000-8000-00805F9B34FB
                MethodInfo getSvcs = null;
                foreach (MethodInfo m in device.GetType()
                             .GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "GetGattServicesForUuidAsync") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length < 1) continue;
                    if (ps[0].ParameterType.Name != "Guid") continue;
                    getSvcs = m;
                    break;
                }

                if (getSvcs == null)
                {
                    Console.WriteLine("    没有 GetGattServicesForUuidAsync");
                    return;
                }

                Guid batterySvc = new Guid("0000180F-0000-1000-8000-00805F9B34FB");
                object[] callArgs = new object[getSvcs.GetParameters().Length];
                callArgs[0] = batterySvc;
                for (int i = 1; i < callArgs.Length; i++)
                {
                    Type pt = getSvcs.GetParameters()[i].ParameterType;
                    callArgs[i] = pt.IsEnum
                        ? Enum.ToObject(pt, 0)
                        : (pt.IsValueType ? Activator.CreateInstance(pt) : null);
                }

                object svcOp = getSvcs.Invoke(device, callArgs);
                object svcResult = AwaitOp(svcOp, getSvcs.ReturnType, 6000);
                if (svcResult == null) { Console.WriteLine("    GATT 服务查询失败"); return; }

                Console.WriteLine("    服务查询 status = " + Prop(svcResult, "Status"));

                IEnumerable svcs = PropObj(svcResult, "Services") as IEnumerable;
                if (svcs == null) { Console.WriteLine("    没有 Services 集合"); return; }

                int count = 0;
                foreach (object svc in svcs)
                {
                    count++;
                    Console.WriteLine("    找到 Battery Service！");
                    ReadBatteryLevel(svc);
                }
                if (count == 0)
                {
                    Console.WriteLine("    该设备没有暴露 Battery Service（读不到电量）");
                }

                MethodInfo close = device.GetType().GetMethod("Close");
                if (close != null) { try { close.Invoke(device, null); } catch (Exception) { } }
            }
            catch (Exception ex) { Console.WriteLine("    [BLE] " + Inner(ex)); }
        }

        /// <summary>从 Battery Service 读 Battery Level 特征值（UUID 0x2A19）。</summary>
        private static void ReadBatteryLevel(object service)
        {
            try
            {
                MethodInfo getChars = null;
                foreach (MethodInfo m in service.GetType()
                             .GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "GetCharacteristicsAsync") continue;
                    if (m.GetParameters().Length != 0) continue;
                    getChars = m;
                    break;
                }
                if (getChars == null) { Console.WriteLine("      没有 GetCharacteristicsAsync"); return; }

                object op = getChars.Invoke(service, null);
                object res = AwaitOp(op, getChars.ReturnType, 6000);
                if (res == null) { Console.WriteLine("      特征值查询失败"); return; }

                IEnumerable chars = PropObj(res, "Characteristics") as IEnumerable;
                if (chars == null) { Console.WriteLine("      没有 Characteristics"); return; }

                foreach (object ch in chars)
                {
                    string uuid = Prop(ch, "Uuid");
                    if (uuid.IndexOf("2a19", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    MethodInfo readAsync = null;
                    foreach (MethodInfo m in ch.GetType()
                                 .GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (m.Name != "ReadValueAsync") continue;
                        if (m.GetParameters().Length != 0) continue;
                        readAsync = m;
                        break;
                    }
                    if (readAsync == null) { Console.WriteLine("      没有 ReadValueAsync"); continue; }

                    object rop = readAsync.Invoke(ch, null);
                    object rres = AwaitOp(rop, readAsync.ReturnType, 6000);
                    if (rres == null) { Console.WriteLine("      读值失败"); continue; }

                    Console.WriteLine("      BatteryLevel status = " + Prop(rres, "Status"));

                    object buffer = PropObj(rres, "Value");
                    if (buffer == null) continue;

                    MethodInfo toArray = null;
                    foreach (MethodInfo m in buffer.GetType()
                                 .GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (m.Name == "ToArray" && m.GetParameters().Length == 0)
                        {
                            toArray = m;
                            break;
                        }
                    }
                    if (toArray == null) continue;

                    byte[] bytes = toArray.Invoke(buffer, null) as byte[];
                    if (bytes != null && bytes.Length > 0)
                    {
                        Console.WriteLine("      >>> 电量 = " + bytes[0] + "%");
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("      [读取] " + Inner(ex)); }
        }
    }
}
