using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Islet.Sources
{
    /// <summary>
    /// SMTC 的反射桥接层。
    ///
    /// 为什么要反射：本机只有 .NET Framework 4.8 的 csc，没有 Windows SDK 投影，
    /// 无法在编译期引用 Windows.Media.Control。但运行时类型投影是完整的，
    /// 因此用 Type.GetType + AsTask 泛型桥接即可拿到强类型对象。
    /// 这条路径已用探针验证（能读到真实播放会话）。
    /// </summary>
    internal static class SmtcBridge
    {
        private const string MgrTypeName =
            "Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows, ContentType=WindowsRuntime";

        private static Type _mgrType;
        private static MethodInfo _asTaskOpen;

        public static bool IsAvailable
        {
            get { return ResolveManagerType() != null && AsTaskOpen != null; }
        }

        public static object RequestManager(int timeoutMs)
        {
            Type t = ResolveManagerType();
            if (t == null) throw new InvalidOperationException("SMTC 类型不可用");

            MethodInfo ra = t.GetMethod("RequestAsync", BindingFlags.Public | BindingFlags.Static);
            if (ra == null) throw new InvalidOperationException("找不到 RequestAsync");

            object op = ra.Invoke(null, null);
            return Await(ra.ReturnType, op, timeoutMs);
        }

        /// <summary>把 WinRT IAsyncOperation&lt;T&gt; 转成 Task 并取结果。</summary>
        public static object Await(Type asyncOperationType, object op, int timeoutMs)
        {
            if (asyncOperationType == null) throw new ArgumentNullException("asyncOperationType");
            if (!asyncOperationType.IsGenericType)
                throw new ArgumentException("不是泛型 IAsyncOperation", "asyncOperationType");

            Type resultType = asyncOperationType.GetGenericArguments()[0];
            MethodInfo closed = AsTaskOpen.MakeGenericMethod(resultType);
            Task task = (Task)closed.Invoke(null, new object[] { op });

            if (!task.Wait(timeoutMs))
                throw new TimeoutException("SMTC 异步操作超时");

            return task.GetType().GetProperty("Result").GetValue(task);
        }

        /// <summary>反射读取属性；属性不存在时返回 null 而不抛。</summary>
        public static object Prop(object obj, string name)
        {
            if (obj == null) return null;
            PropertyInfo p = obj.GetType().GetProperty(name);
            return p == null ? null : p.GetValue(obj);
        }

        public static string Str(object obj, string name)
        {
            object v = Prop(obj, name);
            return v == null ? null : v.ToString();
        }

        private static Type ResolveManagerType()
        {
            if (_mgrType != null) return _mgrType;
            try { _mgrType = Type.GetType(MgrTypeName, false); }
            catch (Exception) { _mgrType = null; }
            return _mgrType;
        }

        private static MethodInfo AsTaskOpen
        {
            get
            {
                if (_asTaskOpen != null) return _asTaskOpen;
                try
                {
                    _asTaskOpen = typeof(System.WindowsRuntimeSystemExtensions)
                        .GetMethods()
                        .FirstOrDefault(x => x.Name == "AsTask"
                                          && x.GetParameters().Length == 1
                                          && x.GetParameters()[0].ParameterType.IsGenericType
                                          && x.GetParameters()[0].ParameterType
                                                .GetGenericTypeDefinition().Name
                                                .StartsWith("IAsyncOperation", StringComparison.Ordinal));
                }
                catch (Exception) { _asTaskOpen = null; }
                return _asTaskOpen;
            }
        }
    }
}
