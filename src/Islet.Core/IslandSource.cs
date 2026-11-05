using System;

namespace Islet.Core
{
    /// <summary>事件来源。顺序即默认权重顺序的参考，但实际权重见 IslandScore.BaseWeight。</summary>
    public enum IslandSource
    {
        Media = 0,
        Clipboard = 1,
        Progress = 2,
        Timer = 3,
        Health = 4,
        Privacy = 5,
        Meeting = 6
    }
}
