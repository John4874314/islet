using System;

namespace Islet.Core
{
    /// <summary>数据源。实现方推事件给 IslandSlotManager，不得直接碰 UI。</summary>
    public interface IIslandProvider : IDisposable
    {
        /// <summary>用于日志与熔断统计的稳定标识。</summary>
        string Name { get; }

        /// <summary>由宿主注入的回调。Provider 只通过它上报。</summary>
        void Attach(IIslandSink sink);
    }

    /// <summary>Provider 与仲裁器之间的唯一通道。</summary>
    public interface IIslandSink
    {
        /// <summary>上报或更新一个事件（按 Id 去重）。</summary>
        void Publish(IslandEvent evt);

        /// <summary>移除一个事件。</summary>
        void Retract(string id);

        /// <summary>移除某个来源的全部事件。</summary>
        void RetractSource(IslandSource source);
    }
}
