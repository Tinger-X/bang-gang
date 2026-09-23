using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>
/// 图标形状。除 <see cref="Glyph.Link"/> 外都是 1.6px 圆头线条；
/// <see cref="Glyph.Link"/> 照搬的是一张**实心**参考图，笔画粗细由形状自己定，见那里的注释。
/// </summary>
internal enum Glyph
{
    // ---- 设置页左栏那六个（形状照 shoots/ 里那几张 SVG 重画成线框版）----
    Win, Nodes, Tune, Tools, Wand, Info,
    // ---- 其余各处零散用的 ----
    Link, Eye, EyeOff, Close, Check, Copy, ChevDown, ChevRight,
}

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
            case Glyph.Win:
                // 快捷按键：一扇四格的窗户。
                //
                // 原图是 Windows 徽标那种四块**分离**的窗格，但 18px 下每格只有 4px 见方、
                // 而笔宽就要占掉 1.5px，四格里那个洞会糊死。所以收成一个方框加贯通十字 ——
                // 四格的读法一样成立，而且这个尺寸下才真的看得见格子。
                using (var win = RP.Path(
                    new Rectangle((int)Math.Round(cx - s * 0.84f), (int)Math.Round(cy - s * 0.84f),
                                  (int)Math.Round(s * 1.68f), (int)Math.Round(s * 1.68f)),
                    (int)Math.Round(s * 0.26f)))
                    g.DrawPath(pen, win);
                g.DrawLine(pen, cx, cy - s * 0.84f, cx, cy + s * 0.84f);
                g.DrawLine(pen, cx - s * 0.84f, cy, cx + s * 0.84f, cy);
                break;

            case Glyph.Nodes:
                // 模型接入：中心一个圆点，四条朝对角伸出去的短柱。照 llm.svg 的构成 ——
                // 那个图形就是四段圆头短柱围着一个点。圆头笔帽本身就给出了短柱两端的样子。
                {
                    // 沿 45° 走 d 距离时，x/y 上各走 d·k —— 这两个 k 是在**算坐标**，
                    // 不是在缩图形。写错成「把已经算好的坐标再乘一次 k」会让整个图形
                    // 缩到 0.44s，比旁边几个小一圈（第一版就是这么错的）。
                    const float k = 0.70710678f;
                    float from = s * 0.32f * k, to = s * 1.06f * k;
                    foreach (var (dx, dy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
                        g.DrawLine(pen, cx + dx * from, cy + dy * from, cx + dx * to, cy + dy * to);
                    g.FillEllipse(br, cx - s * 0.17f, cy - s * 0.17f, s * 0.34f, s * 0.34f);
                }
                break;

            case Glyph.Tune:
                // 对话参数：三条滑轨加旋钮，三个旋钮位置错开（照 param.svg）。
                {
                    float[] kx = { -0.30f, 0.36f, -0.10f };
                    for (int i = 0; i < 3; i++)
                    {
                        float y = cy + (i - 1) * s * 0.62f;
                        g.DrawLine(pen, cx - s * 0.92f, y, cx + s * 0.92f, y);
                        g.FillEllipse(br, cx + kx[i] * s - s * 0.22f, y - s * 0.22f, s * 0.44f, s * 0.44f);
                    }
                }
                break;

            case Glyph.Tools:
                // 工具调用：交叉的扳手与螺丝刀（照 tool.svg）。
                //
                // 原图是两把**实心**工具叠在一起，那个密度线框画不出来。只保留「一眼认得出是哪两把」
                // 的特征：一把的末端开叉（开口扳手的钳口），另一把两端各一道横杠（螺丝刀的刀口与手柄），
                // 其余全收成一根斜杆。再多加一笔这个尺寸就成一团墨了。
                {
                    void L(float x1, float y1, float x2, float y2) =>
                        g.DrawLine(pen, cx + x1 * s, cy + y1 * s, cx + x2 * s, cy + y2 * s);

                    // 扳手：左上 → 右下的一根斜杆，右下端一个开口叉
                    L(-0.65f, -0.65f, 0.35f, 0.35f);
                    L(0.35f, 0.35f, 0.79f, 0.40f);
                    L(0.35f, 0.35f, 0.40f, 0.79f);

                    // 螺丝刀：左下 → 右上的一根斜杆，右上端一道刀口、左下端一道手柄
                    L(-0.60f, 0.60f, 0.60f, -0.60f);
                    L(0.42f, -0.78f, 0.78f, -0.42f);
                    L(-0.76f, 0.44f, -0.44f, 0.76f);
                }
                break;

            case Glyph.Wand:
                // 界面外观：一根斜魔杖加两颗星（照 ui.svg）。左上那头一道小方框是握柄的分界，
                // 原图上就有；两颗星一大一小错开，比排成一样大更像「撒出去的」。
                {
                    g.DrawLine(pen, cx - s * 0.26f, cy - s * 0.26f, cx + s * 0.72f, cy + s * 0.72f);
                    using (var grip = RP.Path(
                        new Rectangle((int)Math.Round(cx - s * 0.56f), (int)Math.Round(cy - s * 0.56f),
                                      (int)Math.Round(s * 0.30f), (int)Math.Round(s * 0.30f)),
                        (int)Math.Round(s * 0.08f)))
                        g.DrawPath(pen, grip);

                    FourStar(g, br, cx + s * 0.50f, cy - s * 0.58f, s * 0.34f);
                    FourStar(g, br, cx - s * 0.64f, cy + s * 0.12f, s * 0.19f);
                }
                break;

            case Glyph.Info:
                // 软件说明：一个带缺口的圆环加一个感叹号（照 about.svg）。缺口留在左上，
                // 原图那个环就是断在那儿的 —— 断口是它和「一个圆圈」唯一的区别，别填上。
                g.DrawArc(pen, cx - s * 0.84f, cy - s * 0.84f, s * 1.68f, s * 1.68f, 245f, 300f);
                g.DrawLine(pen, cx, cy - s * 0.32f, cx, cy + s * 0.16f);
                g.FillEllipse(br, cx - s * 0.13f, cy + s * 0.30f, s * 0.26f, s * 0.26f);
                break;

            case Glyph.Link:
                // 外部链接：一圈**粗圆角方框**，右上角断开，一支同样粗的箭头穿过缺口指到框外。
                //
                // 形状照搬参考图 shoots/open.svg（那是一张**实心**图标），但它是**等宽**的：
                // 框壁和箭杆都是 1024 网格里的 90 个单位，四个自由端都是半径 45 的半圆。
                // 所以不必走填充路径 —— 一条同宽的圆头画笔沿中轴走一遍就得到同一张图，
                // 于是它和别的线框图标共用 DrawGlyph 这一条路（同一套圆头、圆角连接、抗锯齿），
                // 也不用在这里另开一段 Graphics 状态或自己管填充色。
                //
                // 下面的数是参考图 1024 网格里的原值（原点是 viewBox 中心、y 向下），乘 u 换成像素：
                //   框    中心线半宽 355、圆角半径 105
                //   缺口  上边停在中心线右侧 185 处，右边停在中心线下方 185 处
                //   箭头  两条臂交在框缺掉的那个角 (355,-355) 上，各自只伸到离中心线 55 处；
                //         箭杆从那个角一路连到中心 —— 长的那一笔是杆，短的两笔是头
                {
                    // 1.10：参考图的墨迹只占 1024 里的 800（78%），原样铺进 20px 的图标框偏小；
                    // 放大一成后最远处的墨迹离圆心 10.8px，28px 圆钮（半径 14）里仍留 3px 余量。
                    const float k = 1.10f;
                    float u = s * k / 512f;
                    float e = 355f * u, rr = 105f * u, stop = 185f * u, arm = 55f * u;

                    // 笔宽由形状自己定，不取 DrawGlyph 的 w：等宽是这张图的一部分，
                    // 而 90/1024 换算到本应用是 2.1px，比线框图标的 1.6 略粗 —— 实心图标就得这么读。
                    using var lp = new Pen(c, 90f * u)
                    { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

                    // 框：从右边缺口处起笔，绕过左下三个圆角，收在上边缺口处。两端的半圆端头
                    // 由圆头笔帽给出 —— 和参考图里的那两段弧是同一个东西。
                    using (var frame = new GraphicsPath())
                    {
                        frame.AddLine(cx + e, cy + stop, cx + e, cy + e - rr);
                        frame.AddArc(cx + e - 2 * rr, cy + e - 2 * rr, 2 * rr, 2 * rr, 0, 90);
                        frame.AddLine(cx + e - rr, cy + e, cx - e + rr, cy + e);
                        frame.AddArc(cx - e, cy + e - 2 * rr, 2 * rr, 2 * rr, 90, 90);
                        frame.AddLine(cx - e, cy + e - rr, cx - e, cy - e + rr);
                        frame.AddArc(cx - e, cy - e, 2 * rr, 2 * rr, 180, 90);
                        frame.AddLine(cx - e + rr, cy - e, cx - stop, cy - e);
                        g.DrawPath(lp, frame);
                    }

                    // 杆和两条臂画成**两个图形**：并成一条折线的话它会在这个角上来回折返，
                    // 而 180° 的折返是个退化的连接；分成两个图形在那儿只是互相叠上。
                    using (var arrow = new GraphicsPath())
                    {
                        arrow.AddLine(cx, cy, cx + e, cy - e);
                        arrow.StartFigure();
                        arrow.AddLine(cx + arm, cy - e, cx + e, cy - e);
                        arrow.AddLine(cx + e, cy - e, cx + e, cy - arm);
                        g.DrawPath(lp, arrow);
                    }
                }
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

            case Glyph.Copy:
                // 两张叠着的纸：后一张偏右上，前一张偏左下。前一张先拿底色（back）
                // 填掉再描边，两张框相交的那几段线才不会糊成一团（和 EyeOff 的斜杠同理）。
                {
                    float bw = s * 1.04f, cr = s * 0.26f;
                    using (var bp = RoundRect(new RectangleF(cx - s * 0.16f, cy - s * 0.82f, bw, bw), cr))
                        g.DrawPath(pen, bp);
                    using (var fp = RoundRect(new RectangleF(cx - s * 0.88f, cy - s * 0.22f, bw, bw), cr))
                    {
                        if (back.HasValue)
                        {
                            using var eb = new SolidBrush(back.Value);
                            g.FillPath(eb, fp);
                        }
                        g.DrawPath(pen, fp);
                    }
                }
                break;

            case Glyph.ChevDown:     // 代码块展开时：点它收起来（和思考块的「展开时箭头朝下」一套）
                g.DrawLines(pen, new[]
                {
                    new PointF(cx - s * 0.62f, cy - s * 0.30f),
                    new PointF(cx, cy + s * 0.36f),
                    new PointF(cx + s * 0.62f, cy - s * 0.30f),
                });
                break;

            case Glyph.ChevRight:    // 代码块收起时：点它展开
                g.DrawLines(pen, new[]
                {
                    new PointF(cx - s * 0.30f, cy - s * 0.62f),
                    new PointF(cx + s * 0.36f, cy),
                    new PointF(cx - s * 0.30f, cy + s * 0.62f),
                });
                break;
        }
        g.SmoothingMode = old;
    }

    /// <summary>圆角矩形路径（float 版；<c>RP.Path</c> 那组是整数网格的）。</summary>
    private static GraphicsPath RoundRect(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
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
    /// <summary>
    /// 四角星：魔杖旁边那些「闪一下」用的。做成四角而不是五角 —— 18px 下五角星的那几个
    /// 内凹角会糊成一个圆点，四角星至少还看得出是个星。
    /// </summary>
    private static void FourStar(Graphics g, Brush br, float cx, float cy, float r)
    {
        float w = r * 0.30f;
        g.FillPolygon(br, new[]
        {
            new PointF(cx, cy - r), new PointF(cx + w, cy - w),
            new PointF(cx + r, cy), new PointF(cx + w, cy + w),
            new PointF(cx, cy + r), new PointF(cx - w, cy + w),
            new PointF(cx - r, cy), new PointF(cx - w, cy - w),
        });
    }

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
