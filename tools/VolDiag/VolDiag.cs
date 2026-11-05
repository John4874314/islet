// 诊断：对比两种获取「每个会话音量控制」的方式，并实测写入是否真的生效。
//
// 背景：岛内音量滑块调了没效果。怀疑点有两个：
//   A. GetSimpleAudioVolume(GroupingParam) —— 多数应用 grouping 是 GUID_NULL，
//      这样会拿到「默认分组」的音量控制，不是该应用自己的。
//   B. 缓存了旧默认设备的 manager —— 连耳机后还指着旧设备。
//
// 本探针对 A 做实证：同一个会话，分别用两条路径读/写，看结果是否一致。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    int NotImpl1();
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
    int NotImpl2();
    int NotImpl3();
    int NotImpl4();
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
                 [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    int NotImpl1();
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    int NotImpl3();
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    int GetAudioSessionControl(ref Guid g, int f, out IntPtr c);
    int GetSimpleAudioVolume(ref Guid g, int flags, out ISimpleAudioVolume v);
    int GetSessionEnumerator(out IAudioSessionEnumerator e);
    int NotImpl1();
    int NotImpl2();
    int NotImpl3();
    int NotImpl4();
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    int GetCount(out int n);
    int GetSession(int i, out IAudioSessionControl c);
}

[ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
    int GetState(out int s);
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string n);
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string n, ref Guid ctx);
    int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string p);
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string p, ref Guid ctx);
    int GetGroupingParam(out Guid g);
    int SetGroupingParam(ref Guid g, ref Guid ctx);
    int NotImpl1();
    int NotImpl2();
}

[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    int GetState(out int s);
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string n);
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string n, ref Guid ctx);
    int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string p);
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string p, ref Guid ctx);
    int GetGroupingParam(out Guid g);
    int SetGroupingParam(ref Guid g, ref Guid ctx);
    int NotImpl1();
    int NotImpl2();
    int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string r);
    int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string r);
    int GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();
    int SetDuckingPreference(bool b);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    int SetMasterVolume(float level, ref Guid ctx);
    int GetMasterVolume(out float level);
    int SetMute(bool mute, ref Guid ctx);
    int GetMute(out bool mute);
}

internal static class Probe
{
    private static void Main()
    {
        IMMDeviceEnumerator en = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
        IMMDevice dev;
        if (en.GetDefaultAudioEndpoint(0, 1, out dev) != 0 || dev == null)
        {
            Console.WriteLine("no default render device");
            return;
        }

        string id;
        dev.GetId(out id);
        Console.WriteLine("默认设备 ID = " + id);

        Guid mgrIid = typeof(IAudioSessionManager2).GUID;
        object mgrObj;
        dev.Activate(ref mgrIid, 23, IntPtr.Zero, out mgrObj);
        IAudioSessionManager2 mgr = (IAudioSessionManager2)mgrObj;

        IAudioSessionEnumerator ses;
        mgr.GetSessionEnumerator(out ses);
        int n;
        ses.GetCount(out n);
        Console.WriteLine("会话数 = " + n);
        Console.WriteLine();

        int targetIdx = -1;
        string targetPid = "";

        for (int i = 0; i < n; i++)
        {
            IAudioSessionControl ctl;
            if (ses.GetSession(i, out ctl) != 0 || ctl == null) continue;
            IAudioSessionControl2 ctl2 = ctl as IAudioSessionControl2;

            int st;
            ctl.GetState(out st);
            uint pid = 0;
            if (ctl2 != null) ctl2.GetProcessId(out pid);

            Guid grouping;
            ctl.GetGroupingParam(out grouping);

            // 路径 A：GetSimpleAudioVolume(GroupingParam)
            float volA = -1f;
            ISimpleAudioVolume va;
            if (mgr.GetSimpleAudioVolume(ref grouping, 0, out va) == 0 && va != null)
                va.GetMasterVolume(out volA);

            // 路径 B：对会话控件做 QI 拿 ISimpleAudioVolume
            float volB = -1f;
            ISimpleAudioVolume vb = ctl as ISimpleAudioVolume;
            if (vb != null) vb.GetMasterVolume(out volB);

            Console.WriteLine(string.Format(
                "[{0}] pid={1,-6} state={2} grouping={3} A(grouping)={4,6:F1}% B(QI)={5,6:F1}% {6}",
                i, pid, st,
                grouping == Guid.Empty ? "NULL" : grouping.ToString().Substring(0, 8),
                volA * 100f, volB * 100f,
                (va == null ? "[A null]" : "") + (vb == null ? "[B null]" : "")));

            if (st == 1 && pid != 0 && targetIdx < 0)
            {
                // 记下第一个正在出声的会话，稍后实测写入
                targetIdx = i;
                targetPid = pid.ToString();
            }
        }

        Console.WriteLine();
        if (targetIdx < 0)
        {
            Console.WriteLine("没有正在出声的会话，跳过写入测试");
            return;
        }

        // ---- 实测：对目标会话分别用两条路径写 30%，看回读与真实状态 ----
        IAudioSessionControl tctl;
        ses.GetSession(targetIdx, out tctl);
        Guid tg; tctl.GetGroupingParam(out tg);

        Console.WriteLine("目标会话 [" + targetIdx + "] pid=" + targetPid + " 写入测试");

        // 记录原始值
        float origB = -1f;
        ISimpleAudioVolume tb = tctl as ISimpleAudioVolume;
        if (tb != null) tb.GetMasterVolume(out origB);
        Console.WriteLine("  原始（QI 路径） = " + (origB * 100f).ToString("F1") + "%");

        Guid ctx = Guid.Empty;

        // 1) 通过 QI 写 30%
        if (tb != null)
        {
            int hr = tb.SetMasterVolume(0.30f, ref ctx);
            float back; tb.GetMasterVolume(out back);
            Console.WriteLine("  QI 写 30% -> hr=0x" + hr.ToString("X8")
                + " 回读=" + (back * 100f).ToString("F1") + "%");
        }

        // 2) 再通过 GetSimpleAudioVolume 读，看它是否反映 30%
        ISimpleAudioVolume va2;
        float viaA = -1f;
        if (mgr.GetSimpleAudioVolume(ref tg, 0, out va2) == 0 && va2 != null)
            va2.GetMasterVolume(out viaA);
        Console.WriteLine("  此刻 GetSimpleAudioVolume(grouping) 读到 = "
            + (viaA * 100f).ToString("F1") + "%");

        // 3) 通过 GetSimpleAudioVolume(GUID_NULL) 读，看是否串到别处
        Guid nullG = Guid.Empty;
        ISimpleAudioVolume vnull;
        float viaNull = -1f;
        if (mgr.GetSimpleAudioVolume(ref nullG, 0, out vnull) == 0 && vnull != null)
            vnull.GetMasterVolume(out viaNull);
        Console.WriteLine("  此刻 GetSimpleAudioVolume(NULL) 读到 = "
            + (viaNull * 100f).ToString("F1") + "%");

        // 恢复原值
        if (tb != null && origB >= 0f) tb.SetMasterVolume(origB, ref ctx);
        float restored = -1f;
        if (tb != null) tb.GetMasterVolume(out restored);
        Console.WriteLine("  已恢复 = " + (restored * 100f).ToString("F1") + "%");
    }
}
