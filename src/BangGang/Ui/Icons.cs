using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>线框图标（统一 1.6px 圆头线条，风格与主界面一致）。</summary>
internal enum Glyph { Sliders, Spark, Palette, Bubble, Link, Eye, EyeOff, Close, Reset, Check }

internal static class Gfx
{
    /// <summary>垃圾桶图标（会话列表的删除按钮）：盖子 + 提手 + 上宽下窄的桶身 + 两条竖线。</summary>
    public static void DrawTrash(Graphics g, RectangleF r, Color c, float w = 1.5f)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Math.Min(r.Width, r.Height) * 0.5f;
        float cx = r.X + r.Width / 2f;
        float cy = r.Y + r.Height / 2f + s * 0.08f;
        using var pen = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLine(pen, cx - s * 0.76f, cy - s * 0.5f, cx + s * 0.76f, cy - s * 0.5f);          // 盖子
        g.DrawLine(pen, cx - s * 0.28f, cy - s * 0.5f, cx - s * 0.28f, cy - s * 0.8f);          // 提手
        g.DrawLine(pen, cx - s * 0.28f, cy - s * 0.8f, cx + s * 0.28f, cy - s * 0.8f);
        g.DrawLine(pen, cx + s * 0.28f, cy - s * 0.8f, cx + s * 0.28f, cy - s * 0.5f);
        g.DrawLine(pen, cx - s * 0.58f, cy - s * 0.5f, cx - s * 0.44f, cy + s * 0.76f);         // 桶身
        g.DrawLine(pen, cx + s * 0.58f, cy - s * 0.5f, cx + s * 0.44f, cy + s * 0.76f);
        g.DrawLine(pen, cx - s * 0.44f, cy + s * 0.76f, cx + s * 0.44f, cy + s * 0.76f);
        g.DrawLine(pen, cx - s * 0.15f, cy - s * 0.18f, cx - s * 0.15f, cy + s * 0.48f);        // 竖线
        g.DrawLine(pen, cx + s * 0.15f, cy - s * 0.18f, cx + s * 0.15f, cy + s * 0.48f);
        g.SmoothingMode = old;
    }

    /// <summary>
    /// 画一个线框图标。<paramref name="back"/> 只有「眼睛关闭」用得上：
    /// 斜杠要在眼眶上留一道底色缺口，否则和虹膜糊成一团。
    /// </summary>
    public static void DrawGlyph(Graphics g, Glyph gl, RectangleF r, Color c, float w = 1.6f, Color? back = null)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Math.Min(r.Width, r.Height) * 0.5f;
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        using var pen = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var br = new SolidBrush(c);
        switch (gl)
        {
            case Glyph.Sliders:
                float[] kx = { -0.34f, 0.40f, -0.10f };
                for (int i = 0; i < 3; i++)
                {
                    float y = cy + (i - 1) * s * 0.62f;
                    g.DrawLine(pen, cx - s * 0.92f, y, cx + s * 0.92f, y);
                    g.FillEllipse(br, cx + kx[i] * s - s * 0.24f, y - s * 0.24f, s * 0.48f, s * 0.48f);
                }
                break;

            case Glyph.Spark:
                g.FillPolygon(br, new[]
                {
                    new PointF(cx, cy - s * 0.95f), new PointF(cx + s * 0.27f, cy - s * 0.27f),
                    new PointF(cx + s * 0.95f, cy), new PointF(cx + s * 0.27f, cy + s * 0.27f),
                    new PointF(cx, cy + s * 0.95f), new PointF(cx - s * 0.27f, cy + s * 0.27f),
                    new PointF(cx - s * 0.95f, cy), new PointF(cx - s * 0.27f, cy - s * 0.27f),
                });
                break;

            case Glyph.Palette:
                g.DrawEllipse(pen, cx - s * 0.9f, cy - s * 0.9f, s * 1.8f, s * 1.8f);
                g.FillEllipse(br, cx - s * 0.46f, cy - s * 0.46f, s * 0.34f, s * 0.34f);
                g.FillEllipse(br, cx + s * 0.12f, cy - s * 0.46f, s * 0.34f, s * 0.34f);
                g.FillEllipse(br, cx - s * 0.17f, cy + s * 0.06f, s * 0.34f, s * 0.34f);
                break;

            case Glyph.Bubble:
                // 对话气泡：一个圆角方框 + 左下角伸出去的小尾巴。
                // 方框整体偏上，尾巴才有地方画 —— 尾巴和框必须**连着**，否则 14px 下读成两个东西。
                using (var bubble = RP.Path(
                    new Rectangle((int)Math.Round(cx - s * 0.96f), (int)Math.Round(cy - s * 0.86f),
                                  (int)Math.Round(s * 1.92f), (int)Math.Round(s * 1.44f)),
                    (int)Math.Round(s * 0.44f)))
                    g.DrawPath(pen, bubble);
                g.DrawLine(pen, cx - s * 0.42f, cy + s * 0.56f, cx - s * 0.46f, cy + s * 0.98f);
                g.DrawLine(pen, cx - s * 0.46f, cy + s * 0.98f, cx + s * 0.06f, cy + s * 0.56f);
                break;

            case Glyph.Link:
                // 外部链接：一个**开口朝右上**的方框 + 一支从框里指到框外的斜箭头。
                // 三条边留下缺口是它的读法所在 —— 画成闭合方框就成了「复制」之类的另一个图标。
                g.DrawLine(pen, cx - s * 0.92f, cy - s * 0.28f, cx - s * 0.92f, cy + s * 0.92f);   // 左边
                g.DrawLine(pen, cx - s * 0.92f, cy + s * 0.92f, cx + s * 0.28f, cy + s * 0.92f);   // 下边
                g.DrawLine(pen, cx + s * 0.28f, cy + s * 0.92f, cx + s * 0.28f, cy + s * 0.24f);   // 右边（半截）
                g.DrawLine(pen, cx - s * 0.92f, cy - s * 0.28f, cx - s * 0.34f, cy - s * 0.28f);   // 上边（半截）
                g.DrawLine(pen, cx - s * 0.06f, cy + s * 0.06f, cx + s * 0.92f, cy - s * 0.92f);   // 斜箭头
                g.DrawLine(pen, cx + s * 0.28f, cy - s * 0.92f, cx + s * 0.92f, cy - s * 0.92f);   // 箭头两撇
                g.DrawLine(pen, cx + s * 0.92f, cy - s * 0.92f, cx + s * 0.92f, cy - s * 0.28f);
                break;

            case Glyph.Eye:
            case Glyph.EyeOff:
                Eye(g, pen, br, cx, cy, s, gl == Glyph.EyeOff, back);
                break;

            case Glyph.Close:
                g.DrawLine(pen, cx - s * 0.58f, cy - s * 0.58f, cx + s * 0.58f, cy + s * 0.58f);
                g.DrawLine(pen, cx + s * 0.58f, cy - s * 0.58f, cx - s * 0.58f, cy + s * 0.58f);
                break;

            case Glyph.Check:
                g.DrawLine(pen, cx - s * 0.7f, cy + s * 0.04f, cx - s * 0.16f, cy + s * 0.58f);
                g.DrawLine(pen, cx - s * 0.16f, cy + s * 0.58f, cx + s * 0.74f, cy - s * 0.54f);
                break;

            case Glyph.Reset:
                g.DrawArc(pen, cx - s * 0.82f, cy - s * 0.82f, s * 1.64f, s * 1.64f, 35, 280);
                g.DrawLine(pen, cx + s * 0.60f, cy - s * 0.98f, cx + s * 0.92f, cy - s * 0.38f);
                g.DrawLine(pen, cx + s * 0.60f, cy - s * 0.98f, cx + s * 0.06f, cy - s * 0.92f);
                break;
        }
        g.SmoothingMode = old;
    }

    /// <summary>
    /// 眼睛：杏仁形眼眶（上下两条三次贝塞尔交于左右眼角）+ 虹膜环 + 瞳孔点。
    ///
    /// 用贝塞尔而不是 <c>DrawEllipse</c> —— 椭圆眼眶的四个角是圆的，看着像个「鱼眼」；
    /// 真实的眼睛两端是**尖角**，只有贝塞尔能画出那个收拢的弧度。
    /// 对称三次贝塞尔的中点正好落在控制点偏移的 3/4 处，所以控制点要按 peak * 4/3 给。
    ///
    /// 关闭态先拿 <paramref name="back"/>（输入框底色）画一道加粗斜杠再画细斜杠：
    /// 直接画细线的话斜杠和虹膜会糊在一起，看不出「划掉」的意思。
    /// </summary>
    private static void Eye(Graphics g, Pen pen, Brush br, float cx, float cy, float s, bool off, Color? back)
    {
        float ex = s * 0.92f;              // 眼眶半宽（留出笔宽，别贴到矩形边上被裁）
        float peak = s * 0.60f;            // 眼睑顶点到中线的距离
        float ctl = peak * 4f / 3f;        // 对称三次贝塞尔：中点峰值 = 3/4 控制点偏移
        float bend = ex * 0.44f;           // 控制点横向内收，眼角才会收成尖的

        using var lens = new GraphicsPath();
        lens.AddBezier(cx - ex, cy, cx - bend, cy - ctl, cx + bend, cy - ctl, cx + ex, cy);
        lens.AddBezier(cx + ex, cy, cx + bend, cy + ctl, cx - bend, cy + ctl, cx - ex, cy);
        g.DrawPath(pen, lens);

        g.DrawEllipse(pen, cx - s * 0.33f, cy - s * 0.33f, s * 0.66f, s * 0.66f);
        if (!off) g.FillEllipse(br, cx - s * 0.13f, cy - s * 0.13f, s * 0.26f, s * 0.26f);

        if (!off) return;
        float ax = s * 0.84f, ay = s * 0.84f;
        if (back.HasValue)
        {
            using var cut = new Pen(back.Value, pen.Width * 2.6f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(cut, cx - ax, cy + ay, cx + ax, cy - ay);
        }
        g.DrawLine(pen, cx - ax, cy + ay, cx + ax, cy - ay);
    }
}
