using System;

namespace Islet.Core
{
    /// <summary>
    /// 胶囊显隐策略。抽成纯函数是为了能单测——
    /// 这里曾经出过一个很隐蔽的 bug：切换「显示/隐藏」时先把
    /// 「当前是否隐藏」的标志清零再判定，导致恢复显示的分支永远不成立，
    /// 用户按一次隐藏之后胶囊再也回不来。
    ///
    /// 现在的约定很硬：**判定只看目标状态与当前实际状态的差**，
    /// 调用方不得在判定前自行重置状态标志。
    /// </summary>
    public static class VisibilityPolicy
    {
        /// <summary>需要执行的动作。</summary>
        public enum Action
        {
            /// <summary>状态已一致，什么都不用做。</summary>
            None = 0,

            /// <summary>把窗口藏起来。</summary>
            Hide = 1,

            /// <summary>把窗口显示出来。</summary>
            Show = 2
        }

        /// <summary>
        /// 是否应当隐藏。两个来源：用户手动隐藏，或全屏自动隐身。
        /// </summary>
        public static bool ShouldHide(bool userHidden, bool hideInFullscreen, bool fullscreenNow)
        {
            return userHidden || (hideInFullscreen && fullscreenNow);
        }

        /// <summary>
        /// 由「当前实际是否隐藏」和「目标是否应隐藏」推导要执行的动作。
        /// 这个函数必须是幂等的：同样的输入永远给出同样的动作，
        /// 且 shouldHide=false 而 currentlyHidden=true 时必须返回 Show。
        /// </summary>
        public static Action Decide(bool currentlyHidden, bool shouldHide)
        {
            if (shouldHide == currentlyHidden) return Action.None;
            return shouldHide ? Action.Hide : Action.Show;
        }
    }
}
