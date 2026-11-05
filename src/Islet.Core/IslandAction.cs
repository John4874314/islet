using System;

namespace Islet.Core
{
    /// <summary>岛上可点击的动作。最多 3 个。</summary>
    public sealed class IslandAction
    {
        public IslandAction(string label, Action invoke)
        {
            if (label == null) throw new ArgumentNullException("label");
            if (invoke == null) throw new ArgumentNullException("invoke");
            Label = label;
            Invoke = invoke;
        }

        public string Label { get; private set; }

        /// <summary>点击时执行。异常由调用方隔离，不得冒泡到 UI。</summary>
        public Action Invoke { get; private set; }
    }
}
