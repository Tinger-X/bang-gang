
using System.Drawing.Drawing2D;

namespace BangGang;

// ---------------- 语法树 ----------------
//
// 两种输入（TeX、MathML）都先解析成这一棵树，再交给大家伙 <see cref="MathLayout"/> 排版。
// 两套解析器共用一棵树，是因为「怎么排」和「从哪种语法来」本来就没关系 ——
// MathML 的 <msup> 和 TeX 的 ^ 是同一件事，各排一遍只会让两边慢慢长得不一样。

/// <summary>
/// 原子的**类别**。它唯一的用处是决定左右邻居之间要不要留空 ——
/// <c>a+b</c> 里那个 <c>+</c> 前后要有空、<c>f(x)</c> 里那个括号不要。
/// 不留空的公式挤成一坨，是「这公式看着不对」最常见的原因。
/// </summary>
internal enum MKind { Ord, Bin, Rel, Open, Close, Punct, Op, Func }

/// <summary>公式语法树的节点。</summary>
internal abstract class MNode
{
    public MKind Kind = MKind.Ord;
}

/// <summary>横着排的一串（TeX 的一组 <c>{}</c>、MathML 的 <c>&lt;mrow&gt;</c>）。</summary>
internal sealed class MRow : MNode
{
    public readonly List<MNode> Items = new();
}

/// <summary>一个可见的字（或者几个字：<c>\sin</c>、<c>\text{速度}</c>）。</summary>
internal sealed class MLeaf : MNode
{
    public string Text = "";

    /// <summary>斜体。**单个拉丁字母默认斜体** —— 数学里的变量就该是斜的，这是最省事也最像的一刀。</summary>
    public bool Italic;

    /// <summary>粗体（<c>\mathbf</c>）。</summary>
    public bool Bold;

    /// <summary>相对当前字号的倍数（大运算符会调大一点）。</summary>
    public float Scale = 1f;

    /// <summary>变高变宽的符号（<c>\sum</c> 这一类），做上下限判定时用。</summary>
    public bool Big;

    /// <summary>上下限挂在**角上**而不是正上下方（<c>\int_0^1</c>）。积分号是唯一这样的一类：
    /// 它的字形本身就比字母高出一截，上下限再顶上去，一行正文会被撑成三行高。</summary>
    public bool SideLimits;
}

/// <summary>分数。</summary>
internal sealed class MFrac : MNode
{
    public MNode Num = new MLeaf();
    public MNode Den = new MLeaf();

    /// <summary>是不是行间（display）公式。行内的分子分母要小一号，否则一条 12px 的正文里
    /// 插一个和正文一样大的分数，行高会被顶成两倍。</summary>
    public bool Display;
}

/// <summary>上下标（<c>x^2</c>、<c>x_i</c>、<c>x_i^2</c>）。</summary>
internal sealed class MScript : MNode
{
    public MNode Base = new MLeaf();
    public MNode? Sup, Sub;
}

/// <summary>根号。</summary>
internal sealed class MSqrt : MNode
{
    public MNode Rad = new MLeaf();
    public MNode? Index;
}

/// <summary>带缩放的括号（<c>\left( ... \right)</c>、<c>\begin{pmatrix}</c>）。</summary>
internal sealed class MDelim : MNode
{
    public MNode Inner = new MLeaf();
    public string Open = "(", Close = ")";
}

/// <summary>重音（<c>\hat x</c> / <c>\bar x</c> / <c>\vec x</c>）。</summary>
internal sealed class MAccent : MNode
{
    public MNode Base = new MLeaf();
    public string Acc = "^";

    /// <summary>用横线而不是一个字（<c>\bar</c> / <c>\overline</c>）。</summary>
    public bool Bar;

    /// <summary>画在下面（<c>\underline</c>）。</summary>
    public bool Below;
}

/// <summary>显式的空白（<c>\,</c> <c>\quad</c>）。单位是 em。</summary>
internal sealed class MSpace : MNode
{
    public float Em = 0.1667f;
}

/// <summary>矩阵 / 方程组（<c>\begin{pmatrix}</c>、MathML 的 <c>&lt;mtable&gt;</c>）。</summary>
internal sealed class MMatrix : MNode
{
    public readonly List<List<MNode>> Rows = new();
    public string Open = "(", Close = ")";
}

// ---------------- 画的东西 ----------------

/// <summary>公式里要画的一笔。颜色同样是**记号**不是颜色（见 <c>Markdown</c> 类注释第一条）。</summary>
internal abstract class MPrim;

/// <summary>一个字。位置是**基线左端**（不是左上角）—— 见 <see cref="MathLayout.Ascent"/>。</summary>
internal sealed class MGlyph : MPrim
{
    public string Text = "";
    public required Font Font;
    public Markdown.Ink Ink = Markdown.Ink.Text;
    public float X, Y;
}

/// <summary>一条实心矩形（分数线、根号上的横杠、矩阵的括号线）。Y 是**上边缘**。</summary>
internal sealed class MRule : MPrim
{
    public float X, Y, W, H;
    public Markdown.Ink Ink = Markdown.Ink.Text;
}

/// <summary>折线（根号那一撇）。点坐标与 <see cref="MRule"/> 同一套。</summary>
internal sealed class MPoly : MPrim
{
    public PointF[] Pts = Array.Empty<PointF>();
    public float Th = 1f;
    public Markdown.Ink Ink = Markdown.Ink.Text;
}

/// <summary>
/// 排好版的一个盒子。坐标系是**基线在 y=0、向右为正、向下为正**：
/// 顶边在 <c>-Height</c>、底边在 <c>+Depth</c>、左边在 0。
///
/// 用「基线 + 上下延伸」而不是「左上角 + 宽高」，是因为整条排版路上唯一真正要对齐的东西
/// 就是基线：行内公式要和正文的字坐在同一条线上，上下标要相对它抬起来，分数线要卡在它上面
/// 0.25em 处。用左上角表述的话，每往下走一层都要把高度和深度重新加一遍，加错的后果是
/// 公式看上去「浮」在半空或者「沉」到字下面，而且只在某些嵌套组合里才露头。
/// </summary>
internal sealed class MBox
{
    public float Width;
    public float Height;   // 基线以上的高度
    public float Depth;    // 基线以下的深度
    public readonly List<MPrim> Prims = new();
}

/// <summary>
/// 公式排版。TeX 和 MathML 两套解析器都排成同一棵树，这里是唯一的排法。
///
/// 字号阶梯照搬 TeX 的 10 / 7 / 5：正文一层、上下标一层、上下标的上下标再一层。
/// 少一层的话 <c>x^{y^z}</c> 里那个 z 会和 y 一样大，看上去像平方的平方没写完。
/// </summary>
internal static class MathLayout
{
    /// <summary>
    /// 脚本层相对上一层的字号。TeX 是 10 → 7 → 5，也就是每下一层乘 0.7 再取整 ——
    /// <c>x^{y^z}</c> 里 z 就这么小下去。
    ///
    /// **每层乘同一个系数、不要去查「这是第几层」**：字号本身已经把层级带在路上了，
    /// 再按层数查表就多出一份必须和字号同步维护的状态，两份一旦不同步，
    /// 上标会大一号或者小一号，而且只在某些嵌套里露头。下面的 0.7² ≈ 0.49 就是上面那个 5。
    /// </summary>
    private const float ScriptScale = 0.7f;

    /// <summary>行内分数里分子分母的相对字号。TeX 在这里用 0.7，但那是排铅字的规矩 ——
    /// 12.5px 的正文乘 0.7 只剩 8.75px，雅黑在这个字号下笔画已经开始糊了。</summary>
    private const float InlineFracScale = 0.85f;

    /// <summary>轴高：分数线卡在基线上方这么高（相对字号）。符号的上下居中也是按它算的。</summary>
    private const float AxisEm = 0.25f;

    // ---------------- 字体度量 ----------------

    /// <summary>
    /// 点 → 像素的换算系数。**整个公式盒模型的量纲就是它。**
    ///
    /// <c>Font.Size</c> 是**点**，画出来的是**像素**，两者差 DPI/72（96 DPI 下 1.333）。
    /// 盒子的高 / 深是按字体的上缘下缘算的，而它们的消费方（行高、基线、绘制坐标）
    /// 全是像素 —— 中间少了这一步，一个字在自己的盒子里只占 3/4 的高度，
    /// **每个字形都比自己的盒子高出一圈**，一层层叠上去就成了：
    /// 分子坐在分数线上、矩阵两行压在一起、∫ 的上下限陷进 ∫ 里、
    /// `lim` 的下标钻到 `sin x` 底下。看着像「间距配小了」，其实是量纲错了。
    ///
    /// 宽度那边**没有**这个问题：宽度走 <c>TextRenderer.MeasureText</c> 量，量出来的本来就是像素。
    /// 所以这个盒子一直是「宽像素、高点」的混合量纲，只不过横向的错看不出来 ——
    /// 直到公式开始往上下长。
    ///
    /// 换算只在两处做：<see cref="Build"/> 进去时把字号乘上，<see cref="FontOf"/> 出来时除掉。
    /// 盒子内部**一律像素**，这样新加一种笔（线、折线、字）都不需要记得有这么回事。
    /// </summary>
    private static readonly float Px = ScreenScale();

    private static float ScreenScale()
    {
        try
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            return g.DpiY / 72f;
        }
        catch { return 96f / 72f; }          // 拿不到屏幕 DC 就按 96 DPI，至少是个整数比
    }

    /// <summary>字体的 em，**像素**。<c>Font.Size</c> 是点，直接当像素用就会一直差着 <see cref="Px"/>。</summary>
    private static float Em(Font f) => f.Size * Px;

    /// <summary>
    /// 字体上缘到基线的距离，像素。
    ///
    /// **不能用 <c>Font.Height</c> 代替**：那是行高（上缘 + 下缘 + 行间隙），拿去当基线用，
    /// 公式会整体下沉几像素，而且下沉量随字族变 —— 换一套主题字体就会露馅。
    /// </summary>
    internal static float Ascent(Font f) =>
        f.FontFamily.GetCellAscent(f.Style) * Em(f) / f.FontFamily.GetEmHeight(f.Style);

    /// <summary>基线到字体下缘的距离，和 <see cref="Ascent"/> 配对使用。</summary>
    internal static float Descent(Font f) =>
        f.FontFamily.GetCellDescent(f.Style) * Em(f) / f.FontFamily.GetEmHeight(f.Style);

    private static float W(string s, Font f) => s.Length == 0 ? 0f : Markdown.TextWidth(s, f);

    /// <summary>字号取到 0.5 的整 —— <c>SF.Get</c> 是按字号做键的缓存，
    /// 括号缩放算出来的 12.3487… 会让缓存里堆满只差千分之一号的字体。</summary>
    private static float Snap(float size) => MathF.Max(4f, MathF.Round(size * 2f) / 2f);

    /// <summary>字号一律按**像素**传进来，这里换回点交给 <c>SF.Get</c>（见 <see cref="Px"/>）。</summary>
    private static Font FontOf(float size, bool italic, bool bold)
    {
        var st = FontStyle.Regular;
        if (italic) st |= FontStyle.Italic;
        if (bold) st |= FontStyle.Bold;
        return SF.Get(Snap(size / Px), st);
    }

    // ---------------- 入口 ----------------

    /// <summary>
    /// 把一整块**独立成行**的公式里的分数标成行间样式（分子分母用正文字号）。
    ///
    /// 不标的话 <c>$$\sum\frac{a}{b}$$</c> 里的分数会按**行内**那一档缩到 0.85 ——
    /// 结果就是"独立成行的公式里，除了分数以外的每一个字都比分数大"，
    /// 而画面上看起来只是"这个分数有点小"，很难联想到是样式没标。
    ///
    /// 脚本里的分数不用管：那边传下去的 <c>size</c> 本来就小了一号，
    /// 再乘 0.85 才是错的（<c>x^{\frac{a}{b}}</c> 的分子分母会小到看不清）。
    /// </summary>
    public static void MarkDisplay(MNode n)
    {
        switch (n)
        {
            case MFrac f:
                f.Display = true;
                MarkDisplay(f.Num);
                MarkDisplay(f.Den);
                break;
            case MRow r:
                foreach (var it in r.Items) MarkDisplay(it);
                break;
            case MScript s:
                MarkDisplay(s.Base);      // 上下标**不进去**，见注释
                break;
            case MSqrt q:
                MarkDisplay(q.Rad);
                if (q.Index != null) MarkDisplay(q.Index);
                break;
            case MDelim d:
                MarkDisplay(d.Inner);
                break;
            case MAccent a:
                MarkDisplay(a.Base);
                break;
            case MMatrix m:
                foreach (var row in m.Rows)
                    foreach (var cellNode in row) MarkDisplay(cellNode);
                break;
        }
    }

    /// <summary>
    /// 排一棵树。<paramref name="maxW"/> 是能给的最宽处（&lt;=0 表示不限）：
    /// 超宽时**整棵树按比例缩排一遍**，而不是把结果拉扁 —— 拉扁会把分数线变细、
    /// 圆括号变椭圆。缩排是重新走一遍排版，字和线一起小下去，看着才像「公式本来就小一号」。
    /// </summary>
    public static MBox Build(MNode node, float size, float maxW)
    {
        // 字号是**点**，进来就换成像素 —— 盒子内部一律像素，只有 FontOf 换回点（见 Px）。
        float s = size * Px;
        var box = Lay(node, s, 0);
        if (maxW > 12f && box.Width > maxW)
        {
            float k = maxW / box.Width;
            // 缩到 0.45 以下就没法看了，宁可让它横向溢出（调用方会裁），也不给一个读不出来的公式
            if (k > 0.45f) box = Lay(node, MathF.Max(6f, s * k), 0);
        }
        return box;
    }

    private static MBox Lay(MNode n, float size, int depth)
    {
        if (depth > 20) return new MBox();      // 畸形输入别把栈打穿

        switch (n)
        {
            case MLeaf l: return Leaf(l, size);
            case MFrac f: return Frac(f, size, depth);
            case MScript s: return Script(s, size, depth);
            case MSqrt q: return Sqrt(q, size, depth);
            case MDelim d: return Delim(d, size, depth);
            case MAccent a: return Accent(a, size, depth);
            case MMatrix m: return Matrix(m, size, depth);
            case MSpace sp: return new MBox { Width = sp.Em * size };
            case MRow r: return Row(r, size, depth);
            default: return new MBox();
        }
    }

    /// <summary>把 <paramref name="src"/> 整棵盒子搬到 <paramref name="dst"/> 的 (dx, dy) 处。</summary>
    private static void Append(MBox dst, MBox src, float dx, float dy)
    {
        foreach (var p in src.Prims)
        {
            switch (p)
            {
                case MGlyph g: dst.Prims.Add(new MGlyph { Text = g.Text, Font = g.Font, Ink = g.Ink, X = g.X + dx, Y = g.Y + dy }); break;
                case MRule r: dst.Prims.Add(new MRule { X = r.X + dx, Y = r.Y + dy, W = r.W, H = r.H, Ink = r.Ink }); break;
                case MPoly y:
                    var pts = new PointF[y.Pts.Length];
                    for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(y.Pts[i].X + dx, y.Pts[i].Y + dy);
                    dst.Prims.Add(new MPoly { Pts = pts, Th = y.Th, Ink = y.Ink });
                    break;
            }
        }
    }

    // ---------------- 各种节点 ----------------

    private static MBox Leaf(MLeaf l, float size)
    {
        var f = FontOf(size * l.Scale, l.Italic, l.Bold);
        var b = new MBox { Width = W(l.Text, f), Height = Ascent(f), Depth = Descent(f) };
        b.Prims.Add(new MGlyph { Text = l.Text, Font = f, X = 0, Y = 0 });
        return b;
    }

    private static MBox Frac(MFrac fr, float size, int depth)
    {
        float inner = fr.Display ? size : size * InlineFracScale;
        var num = Lay(fr.Num, inner, depth + 1);
        var den = Lay(fr.Den, inner, depth + 1);

        float axis = AxisEm * size;
        float t = MathF.Max(1f, MathF.Round(size * 0.045f));       // 分数线粗细
        float gap = MathF.Max(1.5f, size * 0.14f);                 // 线与分子 / 分母之间

        float w = MathF.Max(num.Width, den.Width) + size * 0.35f;
        float numBase = -(axis + t / 2f + gap) - num.Depth;
        float denBase = -(axis - t / 2f) + gap + den.Height;

        var b = new MBox
        {
            Width = w,
            Height = axis + t / 2f + gap + num.Height + num.Depth,
            Depth = MathF.Max(0f, -axis + t / 2f + gap + den.Height + den.Depth),
        };
        b.Prims.Add(new MRule { X = 0, Y = -(axis + t / 2f), W = w, H = t });
        Append(b, num, (w - num.Width) / 2f, numBase);
        Append(b, den, (w - den.Width) / 2f, denBase);
        return b;
    }

    private static MBox Script(MScript s, float size, int depth)
    {
        var b = Lay(s.Base, size, depth + 1);

        float ss = size * ScriptScale;
        MBox? sup = s.Sup != null ? Lay(s.Sup, ss, depth + 1) : null;
        MBox? sub = s.Sub != null ? Lay(s.Sub, ss, depth + 1) : null;

        // \sum / \int / \lim 这一类的上下限在**正上下方**，其余的角标挂在右上 / 右下。
        // 认大小靠 Base 的种类：大运算符在解析那一步就标成 Op 了。
        bool limits = s.Base.Kind == MKind.Op && !(s.Base is MLeaf { SideLimits: true });

        var box = new MBox();
        // 上下限 / 角标一律**贴墨迹**摆，不贴字格。
        //
        // 字格比墨迹高出一圈，平时看不出来（一个字母的字格和墨迹也就差一两个像素），
        // 但 Σ 和 ∫ 的字格比它自己画出来的那一坨高得多 —— 按字格算出来的上标基线
        // 会落在 Σ 的肚子里，`\sum_{i=1}^{n}` 于是变成「i=1 叠在 Σ 上」。
        // 这些偏移量全都只看「画出来多重」，所以也只能问 InkOf 要。
        var (bt, bb) = InkOf(b);
        if (limits && sup != null && sub != null)
        {
            float w = MathF.Max(b.Width, MathF.Max(sup.Width, sub.Width));
            float gap = size * 0.14f;
            var (st, sb) = InkOf(sup);
            var (tt, tb) = InkOf(sub);
            float supBase = bt - gap - sb;         // 上标的墨迹下缘落在 Σ 的墨迹上缘之上
            float subBase = bb + gap - tt;         // 下标的墨迹上缘落在 Σ 的墨迹下缘之下
            Append(box, b, (w - b.Width) / 2f, 0);
            Append(box, sup, (w - sup.Width) / 2f, supBase);
            Append(box, sub, (w - sub.Width) / 2f, subBase);
            box.Width = w;
            box.Height = MathF.Max(b.Height, -(supBase + st));
            box.Depth = MathF.Max(b.Depth, subBase + tb);
            return box;
        }

        Append(box, b, 0, 0);
        float kern = size * 0.04f;
        float colW = 0;
        float supBot = 0f;
        if (sup != null)
        {
            var (st, sb) = InkOf(sup);
            // 上标的墨迹下缘抬到基线以上约 0.42em（就是 x-height 稍高一点的位置）；
            // base 自己很高时（分数、根式、大运算符）改成骑在它的墨迹上沿再往下
            // 压 0.25em（TeX 的 sup_drop：上标嵌进高 base 的顶部一点，不是悬在它头顶上）。
            // 两条取更靠上的那个，于是「矮 base 用固定抬升、高 base 自动让位」是同一条
            // 式子的两个分支。
            //
            // 第二条的写法**必须是 `bt + 0.25em`（往下压），不能是「墨迹上沿再往上一点」
            // （bt - 0.06em 之类）：n 这种小写字母的墨迹上沿只有约 0.55em，比固定抬升
            // 的 0.42em 高不了多少，「再往上一点」会让上标整个悬到 n 的头顶上方 ——
            // `\(O(n^2)\)` 里的 ² 就是这么飘上去的（0.9.4 修）。
            float want = MathF.Min(-0.42f * size, bt + 0.25f * size);
            float supBase = want - sb;
            Append(box, sup, b.Width + kern, supBase);
            colW = MathF.Max(colW, sup.Width);
            box.Height = MathF.Max(b.Height, -(supBase + st));
            supBot = want;
        }
        else box.Height = b.Height;

        if (sub != null)
        {
            var (tt, tb) = InkOf(sub);
            float want = MathF.Max(0.10f * size, bb + size * 0.06f);
            // 上下标同时挂在一个矮 base 上时（x_i^2 那种）两者会叠在一起 —— 下标再往下让一让。
            if (s.Sup != null) want = MathF.Max(want, supBot + size * 0.10f);
            float subBase = want - tt;
            Append(box, sub, b.Width + kern, subBase);
            colW = MathF.Max(colW, sub.Width);
            box.Depth = MathF.Max(b.Depth, subBase + tb);
        }
        else box.Depth = b.Depth;

        box.Width = b.Width + kern + colW;
        return box;
    }

    /// <summary>根号。那一撇是**画出来的折线**，不是字形 —— 用它自己的 <c>√</c> 字就撑不高，
    /// 内容一高就得换字号，而字号一变粗细也跟着变，和横杠对不上。</summary>
    private static MBox Sqrt(MSqrt q, float size, int depth)
    {
        var rad = Lay(q.Rad, size, depth + 1);
        return Radical(rad, size, q.Index);
    }

    private static MBox Radical(MBox rad, float size, MNode? index)
    {
        float t = MathF.Max(1f, MathF.Round(size * 0.05f));       // 横杠粗细
        float gap = MathF.Max(1.5f, size * 0.12f);
        float w = MathF.Max(size * 0.62f, 7f);                    // 那一撇占的宽

        float top = -(rad.Height + gap) - t;                      // 横杠上缘
        float bot = rad.Depth;                                    // 撇的最低点

        var b = new MBox
        {
            Width = w + rad.Width + size * 0.12f,
            Height = -top,
            Depth = rad.Depth,
        };
        b.Prims.Add(new MRule { X = w * 0.6f, Y = top, W = b.Width - w * 0.6f, H = t });
        b.Prims.Add(new MPoly
        {
            // 那一撇是**折线**，横杠是**矩形**：矩形在 Draw 里落到整数像素、填出来是实笔，
            // 折线走 GDI+ 抗锯齿，同样的粗细看着就淡一档 —— 同一根根号上「杠黑、撇灰」。
            // 这里按比横杠（0.05）厚一点取，把抗锯齿吃掉的那点覆盖度补回来。
            Th = MathF.Max(1f, size * 0.09f),
            Pts = new[]
            {
                new PointF(0f, bot - (bot - top) * 0.45f),
                new PointF(w * 0.28f, bot - t * 0.5f),
                new PointF(w * 0.82f, top + t * 0.5f),
                new PointF(w * 1.02f, top + t * 0.5f),
            },
        });
        Append(b, rad, w + size * 0.12f, 0);

        if (index != null)
        {
            // 根指数 TeX 排在 scriptscriptstyle（更小一号），但 12.5 的正文再乘 0.5 只剩 6px，
            // 雅黑在那个字号下已经是一个墨点。这里跟上下标同一级，宁可略大也不要糊。
            var ix = Lay(index, size * ScriptScale, 0);
            var (it, ib) = InkOf(ix);
            // 排在那一撇**上方**。位置按折线自己算：原来那个「b.Height 的 0.62」是量出来的
            // 比例，只对当时那个字号成立，而折线的陡峭程度同时跟着 size 和 w 走 ——
            // 字号一大，`3` 就压到斜杠上了。直接问折线要那条斜边的方程，跟字号无关。
            float x = MathF.Min(ix.Width, w * 0.82f);
            float y0 = bot - (bot - top) * 0.45f, y1 = bot - t * 0.5f, y2 = top + t * 0.5f;
            float diag = x <= w * 0.28f
                ? y0 + (y1 - y0) * (x / MathF.Max(0.01f, w * 0.28f))
                : y1 + (y2 - y1) * ((x - w * 0.28f) / MathF.Max(0.01f, w * 0.54f));
            float baseLine = diag - size * 0.06f - ib;
            Append(b, ix, 0f, baseLine);
            b.Height = MathF.Max(b.Height, -(baseLine + it));
        }
        return b;
    }

    private static MBox Delim(MDelim d, float size, int depth)
    {
        var inner = Lay(d.Inner, size, depth + 1);
        return WithDelims(inner, size, d.Open, d.Close);
    }

    /// <summary>给内容套一对括号，括号按内容**墨迹**的高度缩放。</summary>
    private static MBox WithDelims(MBox inner, float size, string open, string close)
    {
        var b = new MBox();
        var (it, ib) = InkOf(inner);
        float need = MathF.Max(1f, ib - it);

        var (l, r) = DelimBoxes(open, close, size, need);

        if (l != null) Append(b, l, 0, DelimBase(l, it, ib));
        Append(b, inner, l?.Width ?? 0f, 0);
        if (r != null) Append(b, r, (l?.Width ?? 0f) + inner.Width + size * 0.06f, DelimBase(r, it, ib));

        // 盒子本身仍然按**字格**取：行高要跟正文对得上，而括号的墨迹比字格矮，
        // 按墨迹取会让 `\left(…\right)` 那一行的行距比旁边的正文还紧。
        b.Width = (l?.Width ?? 0f) + inner.Width + (r != null ? size * 0.06f + r.Width : 0f);
        b.Height = MathF.Max(inner.Height, MathF.Max(l?.Height ?? 0f, r?.Height ?? 0f));
        b.Depth = MathF.Max(inner.Depth, MathF.Max(l?.Depth ?? 0f, r?.Depth ?? 0f));
        return b;
    }

    /// <summary>括号的基线上移，让它的**墨迹中点**落在内容墨迹的中点上。</summary>
    private static float DelimBase(MBox d, float inTop, float inBot)
    {
        float innerMid = (inTop + inBot) / 2f;
        float dMid = (d.Depth - d.Height) / 2f;      // d 的盒子就是它自己的墨迹
        return innerMid - dMid;
    }

    /// <summary>
    /// 一个盒子里**画出来的东西**的上下缘（相对基线，上负下正）。
    ///
    /// 盒子的 <see cref="MBox.Height"/> / <see cref="MBox.Depth"/> 是**字格** ——
    /// 「这一格能装多高」，不是「里面画到哪儿」。两者差得很远（字格 1.27 em，一行小写字母
    /// 的墨迹只有 0.7 em 上下），凡是「要贴着内容配大小 / 居中」的地方都得用这个，
    /// 用 Height / Depth 配出来的括号会大出一大截、中心还会偏。
    ///
    /// 直接走已经排好的每一笔：字形按字体的墨迹比例折算，线段和折线本来就是实心的。
    /// 这是**精确**的（画什么就在 Prims 里），不用再去猜「这一坨大概多高」。
    /// </summary>
    private static (float Top, float Bot) InkOf(MBox b)
    {
        float top = 0f, bot = 0f;
        foreach (var p in b.Prims)
        {
            switch (p)
            {
                case MGlyph g when g.Text.Length > 0:
                {
                    float t = float.MaxValue, d = float.MinValue;
                    foreach (char c in g.Text)
                    {
                        var (ct, cb) = InkEm(c);
                        t = MathF.Min(t, ct);
                        d = MathF.Max(d, cb);
                    }
                    top = MathF.Min(top, g.Y + t * Em(g.Font));
                    bot = MathF.Max(bot, g.Y + d * Em(g.Font));
                    break;
                }
                case MRule r:
                    top = MathF.Min(top, r.Y);
                    bot = MathF.Max(bot, r.Y + r.H);
                    break;
                case MPoly y:
                    foreach (var pt in y.Pts)
                    {
                        top = MathF.Min(top, pt.Y);
                        bot = MathF.Max(bot, pt.Y);
                    }
                    break;
            }
        }
        return (top, bot);
    }

    /// <summary>
    /// 造一对括号。<c>"."</c> 表示那边不画（<c>\left. ... \right)</c>，这在分段函数里是必需的。
    /// </summary>
    private static (MBox?, MBox?) DelimBoxes(string open, string close, float size, float need)
    {
        MBox? M(string glyph)
        {
            if (glyph.Length == 0) return null;
            var (top, bot) = InkEm(glyph[0]);
            float ink = MathF.Max(0.2f, bot - top);
            // 字号取「墨迹刚好装下 need」那一号，多给 6% 免得括号和内容一般高、看着像没框住。
            // **下限是正文那一号**：内容比一个字还矮时（`\left(x\right)`）括号不许跟着缩，
            // 缩下去的括号比正文的 `(` 还小，看着像排错了。
            float s = Snap(MathF.Max(size, need * 1.06f / ink));
            var f = FontOf(s, false, false);
            // 盒子的上下缘取**墨迹**而不是字格：下面 `DelimBase` 是拿上下缘的中点去对内容的，
            // 而字格的上下缘并不对称（雅黑的上缘比下缘大得多），拿它居中括号会整体沉下去。
            var mb = new MBox { Width = W(glyph, f), Height = -top * s, Depth = bot * s };
            mb.Prims.Add(new MGlyph { Text = glyph, Font = f, X = 0, Y = 0 });
            return mb;
        }
        return (M(open), M(close));
    }

    private static readonly Dictionary<char, (float Top, float Bot)> InkCache = new();
    private static readonly FontFamily DelimFamily = new(SF.UiFamily);

    /// <summary>
    /// 一个字形的**墨迹**上下缘，相对基线，单位是 em（和字号无关）：上缘为负、下缘为正。
    ///
    /// **不能用 <see cref="Ascent"/> / <see cref="Descent"/> 代替** —— 那是**字格**，
    /// 是「这个字最多能占多高」，不是「它画到哪儿」。两者在这套界面的字体上差得很远：
    /// 括号的墨迹几乎占满字格，而 `a`、`b` 这些小写字母的墨迹只有字格的六成。
    /// 拿字格去配括号，等于按「装得下多高的字」而不是「内容画到哪儿」来配 ——
    /// 实测括号比内容高出 1.73 倍，而且竖直中心比内容中心低 14px。
    /// 这两种毛病在截图上是「括号大得离谱 + 掉在下面」，但把字格量一遍只会得出「一切正常」。
    ///
    /// <c>internal</c> 是为了 <c>OfflineRender</c> 的逐笔 dump 能把它打出来：
    /// 「这个字被摆在这儿」和「摆它的时候认为它的墨迹在哪儿」是两个问题，
    /// 位置不对的时候必须能分开看。
    /// </summary>
    internal static (float Top, float Bot) InkEm(char c)
    {        if (InkCache.TryGetValue(c, out var v)) return v;

        v = (-0.75f, 0.17f);                       // 量不出来时的兜底：大致就是一对圆括号
        try
        {
            const float Em = 100f;
            using var path = new GraphicsPath();
            path.AddString(c.ToString(), DelimFamily, (int)FontStyle.Regular, Em,
                           new PointF(0, 0), StringFormat.GenericTypographic);
            var r = path.GetBounds();
            // `AddString` 的原点在**行顶**不是基线（实测 `(` 的墨迹从 +0.259 em 起，不是从 0 起），
            // 所以要先减掉这个字族自己的字格上缘，才换得到「相对基线」的位置。
            float baseLine = DelimFamily.GetCellAscent(FontStyle.Regular) * Em
                             / DelimFamily.GetEmHeight(FontStyle.Regular);
            if (r.Height > 1f) v = ((r.Top - baseLine) / Em, (r.Bottom - baseLine) / Em);
        }
        catch { /* 兜底值 */ }

        InkCache[c] = v;
        return v;
    }

    private static MBox Accent(MAccent a, float size, int depth)
    {
        var b = Lay(a.Base, size, depth + 1);
        var box = new MBox { Width = b.Width, Height = b.Height, Depth = b.Depth };
        Append(box, b, 0, 0);

        float gap = MathF.Max(1f, size * 0.10f);
        if (a.Bar)
        {
            float t = MathF.Max(1f, MathF.Round(size * 0.05f));
            float y = a.Below ? b.Depth + gap : -(b.Height + gap) - t;
            box.Prims.Add(new MRule { X = 0, Y = y, W = b.Width, H = t });
            if (a.Below) box.Depth = b.Depth + gap + t;
            else box.Height = b.Height + gap + t;
            return box;
        }

        var acc = Leaf(new MLeaf { Text = a.Acc }, size * 0.9f);
        float w = MathF.Max(b.Width, acc.Width);
        // 重音字形的**字面**长在字格的上半部分，所以贴到基线上方一个 .Depth 处就够了，
        // 再往上抬会让帽子飞离被戴的那个字母。
        float baseLine = a.Below ? b.Depth + gap + acc.Height
                                 : -(b.Height + gap + acc.Depth);
        Append(box, acc, (w - acc.Width) / 2f, baseLine);
        box.Width = w;
        if (a.Below) box.Depth = MathF.Max(box.Depth, baseLine + acc.Depth);
        else box.Height = MathF.Max(box.Height, -baseLine + acc.Height);
        return box;
    }

    private static MBox Matrix(MMatrix m, float size, int depth)
    {
        float cell = size * 0.92f;
        int cols = 0;
        foreach (var r in m.Rows) cols = Math.Max(cols, r.Count);
        if (cols == 0) return new MBox();

        var boxes = new MBox[m.Rows.Count, cols];
        var colW = new float[cols];
        var rowUp = new float[m.Rows.Count];      // 该行墨迹的最高点（相对基线，负）
        var rowDn = new float[m.Rows.Count];      // 该行墨迹的最低点（正）

        for (int i = 0; i < m.Rows.Count; i++)
        {
            for (int j = 0; j < cols; j++)
            {
                var node = j < m.Rows[i].Count ? m.Rows[i][j] : new MLeaf();
                var bx = Lay(node, cell, depth + 1);
                boxes[i, j] = bx;
                colW[j] = MathF.Max(colW[j], bx.Width);
                // 行距按**墨迹**累加，不按字格：字格里有一大截是行间隙，
                // 两行小写字母之间会凭空多出一条空带，矩阵看着就散了。
                var (t, b2) = InkOf(bx);
                rowUp[i] = MathF.Min(rowUp[i], t);
                rowDn[i] = MathF.Max(rowDn[i], b2);
            }
        }

        // 列距 / 行距都取 TeX `pmatrix` 那一档（0.5 em 上下）。原来的 0.9 em 是按字格算的，
        // 字格比墨迹宽，于是列与列之间空出一大块，四个字母的矩阵看着像四列。
        float colGap = size * 0.55f, rowGap = size * 0.42f;
        float totalW = 0;
        foreach (var w in colW) totalW += w;
        totalW += colGap * (cols - 1);

        var inner = new MBox { Width = totalW };
        float y = 0;
        for (int i = 0; i < m.Rows.Count; i++)
        {
            if (i > 0) y += rowDn[i - 1] + rowGap - rowUp[i];
            inner.Height = MathF.Max(inner.Height, -y - rowUp[i]);
            inner.Depth = MathF.Max(inner.Depth, y + rowDn[i]);

            float x = 0;
            for (int j = 0; j < cols; j++)
            {
                if (boxes[i, j].Width > 0)
                    Append(inner, boxes[i, j], x + (colW[j] - boxes[i, j].Width) / 2f, y);
                x += colW[j] + colGap;
            }
        }
        return WithDelims(inner, size, m.Open, m.Close);
    }

    /// <summary>
    /// 横排一串。分两步走：先把每个孩子的盒子都排出来，**再**看有没有需要跟着内容长高的括号，
    /// 最后才摆位置。
    ///
    /// 分两步是为了 <c>(\frac{a}{b})</c> 这种写法：括号的高度取决于它右边那个分数的身高，
    /// 而分数要排完才知道多高。边排边摆的写法在这里只能拿到「上一个孩子」的高度，
    /// 括号会长得不够，然后被后面更高的内容从中间穿出去。
    /// </summary>
    private static MBox Row(MRow r, float size, int depth)
    {
        var kids = new List<MBox>(r.Items.Count);
        foreach (var it in r.Items) kids.Add(it is MSpace ? new MBox() : Lay(it, size, depth + 1));

        // 成对的可伸缩括号（( [ { | 这些）：把中间内容的最大高度 / 深度量出来，按它重排这两个叶子
        var stack = new List<int>();
        for (int i = 0; i < r.Items.Count; i++)
        {
            var it = r.Items[i];
            if (it is not MLeaf { Text.Length: > 0 } lf || !IsGrowable(lf.Text[0])) continue;

            if (lf.Kind == MKind.Open) { stack.Add(i); continue; }
            if (lf.Kind != MKind.Close || stack.Count == 0) continue;

            int o = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            // 要配的是**内容画到哪儿**（墨迹），不是内容的字格 —— 按字格配出来的括号
            // 比内容高出一大截，而 `(x)` 这种一个字母的内容最明显。
            float h = 0, d = 0;
            for (int k = o + 1; k < i; k++)
            {
                var (kt, kb) = InkOf(kids[k]);
                h = MathF.Min(h, kt);
                d = MathF.Max(d, kb);
            }

            var fo = kids[o]; var fc = kids[i];
            var (lo, _) = DelimBoxes(((MLeaf)r.Items[o]).Text, "", size, d - h);
            var (_, rc) = DelimBoxes("", ((MLeaf)r.Items[i]).Text, size, d - h);
            if (lo != null && lo.Height > fo.Height) kids[o] = lo;
            if (rc != null && rc.Height > fc.Height) kids[i] = rc;
        }

        var box = new MBox();
        float x = 0;
        var prev = MKind.Ord;
        bool first = true;
        for (int i = 0; i < r.Items.Count; i++)
        {
            if (r.Items[i] is MSpace sp) { x += sp.Em * size; continue; }
            var kind = r.Items[i].Kind;
            // 开头的二元运算符是**正负号**（-x、+1），不是减法：减号两边要留空，正负号只要右边留
            if (first && kind == MKind.Bin) kind = MKind.Ord;
            if (!first) x += GapBetween(prev, kind, size);

            var b = kids[i];
            if (kind == MKind.Op)               // 大运算符（Σ ∫）上下居中在轴线上
            {
                float axis = AxisEm * size;
                float mid = (b.Depth - b.Height) / 2f;
                Append(box, b, x, mid + axis);
                box.Height = MathF.Max(box.Height, axis + mid + b.Height);
                box.Depth = MathF.Max(box.Depth, -(axis + mid) + b.Depth);
            }
            else
            {
                Append(box, b, x, 0);
                box.Height = MathF.Max(box.Height, b.Height);
                box.Depth = MathF.Max(box.Depth, b.Depth);
            }
            x += b.Width;
            prev = kind;
            first = false;
        }
        box.Width = x;
        return box;
    }

    /// <summary>能跟着内容长高的括号。竖线 <c>|</c> 也算 —— 范数 ||x|| 里它必须比 x 高。</summary>
    private static bool IsGrowable(char c) => "([{|".IndexOf(c) >= 0;

    /// <summary>
    /// 两个原子之间留多少空。表是 TeX 那张的简化版，但**关系符和二元运算符留得比其它多**这一条
    /// 必须留着 —— 少了它 <c>a+b=c</c> 会挤成 <c>a+b=c</c> 的一团。
    /// </summary>
    private static float GapBetween(MKind a, MKind b, float size)
    {
        float em;
        if (a == MKind.Rel || b == MKind.Rel) em = 0.28f;
        else if (a == MKind.Bin || b == MKind.Bin) em = 0.22f;
        else if (a == MKind.Func || b == MKind.Func) em = 0.17f;
        else if (a == MKind.Punct || b == MKind.Punct) em = 0.17f;
        else if (a == MKind.Op || b == MKind.Op) em = 0.17f;
        else return 0f;
        return em * size;
    }
}
