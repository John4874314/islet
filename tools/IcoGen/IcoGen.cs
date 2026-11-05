// 浮屿（Islet）图标生成器 v2 —— 三个方案，供挑选。
//
// 上一版为什么不好看（技术层面，不是审美口味问题）：
//   1. 直接按目标尺寸渲染。16px 下要给 0.8px 的描边做抗锯齿，
//      结果就是糊成一片灰；而 GDI+ 的抗锯齿在极细线上质量很差。
//   2. 「球压在胶囊上」这个构图，本质是「一个点 + 一条杠」。
//      缩到 16px 时两块形状互相争夺像素，谁都不清楚。
//   3. 渐变色选择偏「浅蓝+白」，在浅色桌面上对比度低，看着发灰。
//
// 这一版的做法：
//   · **8 倍超采样**：一律在 size*8 的画布上绘制，最后高质量缩下来。
//     这样 16px 图标其实是 128px 渲染后的降采样，细线自然干净。
//   · 采用 macOS Big Sur / Windows 11 通行的「圆角底板 + 单一符号」构图：
//     底板负责「这是一个 App」，符号负责「是哪个 App」。
//     小尺寸下符号永远是实心大形状，不依赖细线。
//   · 三个方案在**色相**和**符号**上都拉开，不是同一套换个颜色。
//
// 用法：
//   IcoGen.exe <输出.ico> [方案]      方案 = A|B|C，默认 A
//   IcoGen.exe --preview <目录>        导出三方案的 256px / 32px 预览 PNG
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class IcoGen
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };

    /// <summary>超采样倍数。8 倍是为了让 16px 也有 128px 的绘制精度。</summary>
    private const int SuperSample = 8;

    private static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception ex)
        {
            // 这台机器上某些 GDI+ 异常在 ToString() 时会二次崩溃，逐字段输出。
            Console.Error.WriteLine("FAIL type=" + ex.GetType().FullName);
            Console.Error.WriteLine("FAIL msg=" + ex.Message);
            Console.Error.WriteLine("FAIL stack=" + ex.StackTrace);
            Environment.Exit(2);
        }
    }

    private static void Run(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--preview")
        {
            string dir = Path.GetFullPath(args.Length > 1 ? args[1] : "preview");
            Directory.CreateDirectory(dir);
            foreach (string style in new[] { "D", "E", "F", "G" })
            {
                foreach (int px in new[] { 256, 64, 48, 32, 24, 16 })
                {
                    using (Bitmap b = Render(px, style))
                    {
                        b.Save(Path.Combine(dir, "islet-" + style + "-" + px + ".png"),
                               ImageFormat.Png);
                    }
                }
            }
            Console.WriteLine("预览已导出到 " + dir);
            return;
        }

        string outPath = Path.GetFullPath(args.Length > 0 && args[0] != "-" ? args[0] : "islet.ico");
        string chosen = args.Length > 1 ? args[1].ToUpperInvariant() : "D";

        List<byte[]> images = new List<byte[]>();
        foreach (int s in Sizes)
        {
            using (Bitmap bmp = Render(s, chosen))
                images.Add(ToPng(bmp));
        }

        WriteIco(outPath, images, Sizes);
        Console.WriteLine("已生成 " + outPath + "（方案 " + chosen + "，" + Sizes.Length + " 个尺寸）");
    }

    // =================================================================
    // 渲染入口：先超采样画，再降采样
    // =================================================================

    private static Bitmap Render(int size, string style)
    {
        int big = size * SuperSample;

        using (Bitmap canvas = new Bitmap(big, big, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);

                switch (style)
                {
                    case "E": DrawE(g, big); break;
                    case "F": DrawF(g, big); break;
                    case "G": DrawG(g, big); break;
                    default: DrawD(g, big); break;
                }
            }

            // 降采样。小尺寸时用双三次就够了，HQ 版会更锐但边缘会出振铃。
            Bitmap result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g2 = Graphics.FromImage(result))
            {
                g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g2.CompositingQuality = CompositingQuality.HighQuality;
                g2.Clear(Color.Transparent);
                g2.DrawImage(canvas, new Rectangle(0, 0, size, size),
                             new Rectangle(0, 0, big, big), GraphicsUnit.Pixel);
            }
            return result;
        }
    }

    // =================================================================
    // 共同的底板：圆角方（squircle 近似）
    // =================================================================

    /// <summary>
    /// 画底板并返回其路径。
    /// macOS 的 squircle 不是简单圆角矩形——超椭圆边角更「饱」。
    /// 这里用「圆角矩形 + 稍大的圆角半径」近似，缩到小尺寸时视觉差异可以忽略。
    /// </summary>
    private static GraphicsPath Plate(Graphics g, float size, Color top, Color bottom,
                                      float radiusRatio, int highlightAlpha)
    {
        float inset = size * 0.055f;                 // 留白，避免图标贴边
        RectangleF r = new RectangleF(inset, inset, size - inset * 2, size - inset * 2);
        float radius = r.Width * radiusRatio;

        GraphicsPath p = RoundedRect(r, radius);

        // 底板阴影：现代图标普遍带一点点，让它从桌面上「浮起来」
        using (GraphicsPath sp = RoundedRect(
            new RectangleF(r.X, r.Y + r.Height * 0.035f, r.Width, r.Height), radius))
        using (PathGradientBrush pg = new PathGradientBrush(sp))
        {
            pg.CenterColor = Color.FromArgb(46, 8, 12, 24);
            pg.SurroundColors = new[] { Color.FromArgb(0, 8, 12, 24) };
            g.FillPath(pg, sp);
        }

        // 对角线渐变：左上亮、右下暗。单一纯色会显得廉价。
        using (LinearGradientBrush lg = new LinearGradientBrush(
            new PointF(r.X, r.Y), new PointF(r.Right, r.Bottom), top, bottom))
        {
            g.FillPath(lg, p);
        }

        // 顶部高光。
        //
        // 这里试过两版都不对：
        //   1. SetClip + FillRectangle → 在 y≈55% 留下一道横贯硬边，像被切成两截。
        //   2. 椭圆 + PathGradient → 椭圆自身轮廓在底板内显成一个圆形亮斑。
        //
        // 现在：竖直 LinearGradientBrush，填充范围严格等于底板路径，
        // 从顶部 alpha 平滑降到 0，且零点落在路径内部。
        // 唯一不需要额外裁剪、也不暴露任何边界的做法。
        RectangleF hiRect = new RectangleF(r.X, r.Y, r.Width, r.Height * 0.60f);
        using (LinearGradientBrush hb = new LinearGradientBrush(
            hiRect,
            Color.FromArgb(highlightAlpha, 255, 255, 255),
            Color.FromArgb(0, 255, 255, 255),
            LinearGradientMode.Vertical))
        using (GraphicsPath clip = (GraphicsPath)p.Clone())
        {
            g.SetClip(clip);           // 只用底板裁剪圆角，不用它造边界
            g.FillRectangle(hb, hiRect);
            g.ResetClip();
        }

        return p;
    }

    // =================================================================
    // 方案 A：深青底 + 悬浮岛（一横 + 一球）
    //   —— 最贴近「浮屿」字面意象，但用实心大形状，不做细线
    // =================================================================

    // =================================================================
    // 抽象几何符号组（D / E / F / G）
    //
    // 这一轮刻意**不画任何具体物体**。上一版「球 + 岛」的问题是比喻不成立：
    // 用户看到的是「一个点和一条杠」，读不出「浮」，只觉得像模板。
    //
    // 改用四个互不相同的几何字，共享同一句话：**上方悬着的东西 + 下方托着它的东西**。
    // 但每个方案用不同的几何语法表达这件事，不是同一套换颜色。
    // =================================================================

    /// <summary>统一的符号绘制前导：拿到安全的绘图区。</summary>
    private static RectangleF SafeArea(float size)
    {
        float inset = size * 0.055f;
        return new RectangleF(inset, inset, size - inset * 2, size - inset * 2);
    }

    // -----------------------------------------------------------------
    // 方案 D：弧 + 影
    //   一道上凸的弧，下面一道更短更淡的镜像弧。
    //   表达「地平线被抬起来」——最克制，也最不容易过时。
    // -----------------------------------------------------------------
    private static void DrawD(Graphics g, float size)
    {
        using (GraphicsPath plate = Plate(g, size,
            Color.FromArgb(255, 46, 74, 102),      // 石墨蓝
            Color.FromArgb(255, 22, 36, 52),
            0.245f, 48))
        {
            RectangleF a = SafeArea(size);
            float cx = size / 2f;

            // 主弧：一段大半径圆弧的上沿
            float r = a.Width * 0.40f;
            RectangleF arcRect = new RectangleF(cx - r, a.Y + a.Height * 0.30f,
                                                r * 2, r * 1.55f);

            float pen = size * 0.085f;
            using (Pen p = new Pen(Color.FromArgb(255, 246, 250, 255), pen))
            using (GraphicsPath ap = new GraphicsPath())
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                ap.AddArc(arcRect, 200f, 140f);
                g.DrawPath(p, ap);
            }

            // 影：同一段弧，水平镜像压扁，透明度很低
            float pen2 = size * 0.045f;
            using (Pen p2 = new Pen(Color.FromArgb(90, 190, 215, 240), pen2))
            using (GraphicsPath ap2 = new GraphicsPath())
            {
                p2.StartCap = LineCap.Round;
                p2.EndCap = LineCap.Round;
                ap2.AddArc(new RectangleF(arcRect.X + r * 0.22f,
                                          arcRect.Y + arcRect.Height * 0.60f,
                                          r * 1.56f, r * 0.70f), 200f, 140f);
                g.DrawPath(p2, ap2);
            }
        }
    }

    // -----------------------------------------------------------------
    // 方案 E：撇 + 悬点
    //   一道从右上到左下的粗斜线，末梢悬一颗实心点，其余全部留白。
    //   不对称，最「不像图标模板」。
    // -----------------------------------------------------------------
    private static void DrawE(Graphics g, float size)
    {
        using (GraphicsPath plate = Plate(g, size,
            Color.FromArgb(255, 92, 66, 58),       // 陶土棕
            Color.FromArgb(255, 44, 30, 28),
            0.255f, 46))
        {
            RectangleF a = SafeArea(size);

            float pen = size * 0.105f;
            PointF p1 = new PointF(a.X + a.Width * 0.72f, a.Y + a.Height * 0.24f);
            PointF p2 = new PointF(a.X + a.Width * 0.30f, a.Y + a.Height * 0.66f);

            using (Pen p = new Pen(Color.FromArgb(255, 250, 244, 238), pen))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                g.DrawLine(p, p1, p2);
            }

            // 悬点：撇的延长线上，但**断开**——这个断开就是「浮」的意思
            float dotR = size * 0.062f;
            float dx = a.X + a.Width * 0.755f;
            float dy = a.Y + a.Height * 0.795f;

            using (GraphicsPath dp = new GraphicsPath())
            {
                dp.AddEllipse(dx - dotR, dy - dotR, dotR * 2, dotR * 2);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 255, 226, 196)))
                    g.FillPath(b, dp);
            }
        }
    }

    // -----------------------------------------------------------------
    // 方案 F：双弧托举
    //   左右各一道竖向弧，像两只手；中间悬一道横线。
    //   对称、稳重，最像「界面被托住」。
    // -----------------------------------------------------------------
    private static void DrawF(Graphics g, float size)
    {
        using (GraphicsPath plate = Plate(g, size,
            Color.FromArgb(255, 38, 92, 88),       // 深松绿
            Color.FromArgb(255, 18, 46, 46),
            0.250f, 50))
        {
            RectangleF a = SafeArea(size);
            float cx = size / 2f;
            float pen = size * 0.072f;

            using (Pen p = new Pen(Color.FromArgb(238, 232, 250, 246), pen))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;

                // 左弧：向右开口
                using (GraphicsPath l = new GraphicsPath())
                {
                    l.AddArc(new RectangleF(cx - a.Width * 0.62f,
                                            a.Y + a.Height * 0.16f,
                                            a.Width * 0.86f, a.Height * 0.72f),
                             300f, 120f);
                    g.DrawPath(p, l);
                }

                // 右弧：向左开口（镜像）
                using (GraphicsPath r2 = new GraphicsPath())
                {
                    r2.AddArc(new RectangleF(cx - a.Width * 0.24f,
                                             a.Y + a.Height * 0.16f,
                                             a.Width * 0.86f, a.Height * 0.72f),
                              120f, 120f);
                    g.DrawPath(p, r2);
                }
            }

            // 中间悬着的横线
            float lw = a.Width * 0.30f;
            float lh = Math.Max(size * 0.030f, pen * 0.62f);
            using (GraphicsPath lp = RoundedRect(
                new RectangleF(cx - lw / 2f, a.Y + a.Height * 0.475f, lw, lh), lh / 2f))
            using (LinearGradientBrush lb = new LinearGradientBrush(
                new RectangleF(cx - lw / 2f, a.Y + a.Height * 0.475f, lw, lh),
                Color.FromArgb(255, 226, 255, 246),
                Color.FromArgb(150, 150, 220, 205), 90f))
            {
                g.FillPath(lb, lp);
            }
        }
    }

    // -----------------------------------------------------------------
    // 方案 G：三道涟漪
    //   三条水平横线，长度与不透明度递减，最上一条最长且带弧。
    //   「水面上泛起」——最轻，浅色背景下也站得住。
    // -----------------------------------------------------------------
    private static void DrawG(Graphics g, float size)
    {
        using (GraphicsPath plate = Plate(g, size,
            Color.FromArgb(255, 96, 104, 128),     // 冷灰
            Color.FromArgb(255, 44, 48, 62),
            0.240f, 52))
        {
            RectangleF a = SafeArea(size);
            float cx = size / 2f;

            // 最上：带弧的最长一条
            float r = a.Width * 0.34f;
            using (Pen p = new Pen(Color.FromArgb(255, 248, 250, 255), size * 0.070f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                using (GraphicsPath ap = new GraphicsPath())
                {
                    ap.AddArc(new RectangleF(cx - r, a.Y + a.Height * 0.30f,
                                             r * 2, r * 1.05f), 195f, 150f);
                    g.DrawPath(p, ap);
                }
            }

            // 中间：稍短，半透明
            float w2 = a.Width * 0.46f;
            using (GraphicsPath lp = RoundedRect(
                new RectangleF(cx - w2 / 2f, a.Y + a.Height * 0.615f,
                               w2, Math.Max(size * 0.036f, size * 0.030f)),
                size * 0.018f))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(185, 226, 238, 255)))
                g.FillPath(b, lp);

            // 最下：更短，更淡
            float w3 = a.Width * 0.28f;
            using (GraphicsPath lp = RoundedRect(
                new RectangleF(cx - w3 / 2f, a.Y + a.Height * 0.755f,
                               w3, Math.Max(size * 0.030f, size * 0.024f)),
                size * 0.015f))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(110, 210, 228, 250)))
                g.FillPath(b, lp);
        }
    }

    // =================================================================
    // 工具
    // =================================================================

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        GraphicsPath p = new GraphicsPath();
        float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
        if (d <= 0.01f) { p.AddRectangle(r); return p; }

        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static byte[] ToPng(Bitmap bmp)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    private static void WriteIco(string path, List<byte[]> images, int[] sizes)
    {
        using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write((ushort)0);
            w.Write((ushort)1);
            w.Write((ushort)images.Count);

            int offset = 6 + 16 * images.Count;
            for (int i = 0; i < images.Count; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((ushort)1);
                w.Write((ushort)32);
                w.Write((uint)images[i].Length);
                w.Write((uint)offset);
                offset += images[i].Length;
            }

            foreach (byte[] img in images) w.Write(img);
        }
    }
}
