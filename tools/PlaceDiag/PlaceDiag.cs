// 直接调用 IslandWindow 的定位逻辑，复现「贴右边缘展开时瞬移」。
//
// 这是纯几何验证：不启动 UI，直接按 PlaceAroundAnchor 的算法算一遍，
// 对比「贴右边缘的圆点」展开成条之后，圆环中心是否还在原处。
//
// 如果圆环中心移动了，说明锚点没保住 —— 那就是用户看到的「瞬移」。
using System;

internal static class PlaceDiag
{
    // 与 IslandWindow 保持一致的常量
    private const double PadSide = 18;
    private const double PadTop = 6;
    private const double PadBottom = 18;
    private const double DotSize = 26;

    private static void Main()
    {
        // 屏幕 2560x1600（物理），逻辑按 150% 缩放 → 1707x1067
        double vLeft = 0, vTop = 0, vRight = 1707, vBottom = 1067;

        // 圆点窗口：26 + 18*2 = 62 宽，26 + 6 + 18 = 50 高
        double dotW = DotSize + PadSide * 2;
        double dotH = DotSize + PadTop + PadBottom;

        // 条窗口实测 430x93（含留白）
        double barW = 430, barH = 93;

        Console.WriteLine("屏幕逻辑尺寸: " + vRight + "x" + vBottom);
        Console.WriteLine("圆点窗口: " + dotW + "x" + dotH);
        Console.WriteLine("条窗口:   " + barW + "x" + barH);
        Console.WriteLine();

        // 测几个靠右的位置
        double[] anchors =
        {
            vRight - 40,          // 很靠右
            vRight - 60,
            vRight - 100,
            vRight - 200,
            vRight - 300,         // 这里条应该刚好放得下
            vRight - 400,
        };

        Console.WriteLine("锚点X     | 圆点窗口Left | 条窗口Left | 条态圆环中心X | 位移");
        Console.WriteLine("----------|--------------|------------|---------------|------");

        double maxDrift = 0;
        foreach (double ax in anchors)
        {
            // 圆点态：窗口左上角
            double dotLeft = Place(ax, dotW, vLeft, vRight);

            // 条态
            double barLeft = Place(ax, barW, vLeft, vRight);

            // 条态下圆环中心在哪：条态时圆环贴在窗口左边
            double centerInBar = barLeft + PadSide + DotSize / 2.0;

            double drift = centerInBar - ax;
            if (Math.Abs(drift) > Math.Abs(maxDrift)) maxDrift = drift;

            Console.WriteLine(string.Format(
                "{0,9:F0} | {1,12:F0} | {2,10:F0} | {3,13:F0} | {4,6:F1}",
                ax, dotLeft, barLeft, centerInBar, drift));
        }

        Console.WriteLine();
        Console.WriteLine("=== 关键验证：展开 → 收起 的往返一致性 ===");
        Console.WriteLine("锚点X  | 展开后玻璃左边缘 | 展开后右边缘 | 收起后玻璃左边缘 | 往返差");
        Console.WriteLine("-------|------------------|--------------|------------------|--------");

        foreach (double ax in anchors)
        {
            double barLeft = Place(ax, barW, vLeft, vRight);
            double barRight = barLeft + barW;

            // 收起：圆点窗口，锚点还是同一个 ax
            double dotLeft = Place(ax, dotW, vLeft, vRight);

            // 收起后圆点中心应该回到 ax
            double dotCenter = dotLeft + PadSide + DotSize / 2.0;
            double roundTrip = dotCenter - ax;

            Console.WriteLine(string.Format(
                "{0,6:F0} | {1,16:F0} | {2,12:F0} | {3,16:F0} | {4,7:F1}",
                ax, barLeft, barRight, dotLeft, roundTrip));
        }

        Console.WriteLine();
        Console.WriteLine("往返差 = 0 表示收起后圆点回到原处，不会漂移。");
        Console.WriteLine("右边缘 ≤ 1705 表示没有越出屏幕。");
    }

    /// <summary>复刻 PlaceAroundAnchor 的水平部分（新语义：贴右边缘，不整体跳）。</summary>
    private static double Place(double anchorX, double w2, double vLeft, double vRight)
    {
        double glassW = DotSize;
        double winLeftIfRight = anchorX - PadSide - glassW / 2.0;

        double winLeft;
        if (winLeftIfRight + w2 <= vRight - 2)
        {
            winLeft = winLeftIfRight;
        }
        else
        {
            winLeft = vRight - w2;
        }

        if (winLeft < vLeft) winLeft = vLeft;
        return winLeft;
    }
}
