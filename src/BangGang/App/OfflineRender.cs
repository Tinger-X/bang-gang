#if DEBUG
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text;

namespace BangGang;

/// <summary>
/// 把一段 markdown 离屏渲染成一张 PNG，**不建窗口、不碰桌面**。
///
/// 为什么要有这个：原来的 markdown 只有一条验证路 —— 起程序、开一条会话、截屏、人眼看。
/// 那条路依赖「这台机器的桌面还能不能画」和「窗口有没有被挡住」，而它真正要回答的问题
/// （公式排得对不对、行高对不对、颜色对不对）跟窗口一点关系都没有。桌面出问题的时候
/// （显卡驱动挂了、机器停在蓝屏画面上、远程会话断开），整条验证链会一起断掉，
/// 而断掉的样子和「渲染器坏了」一模一样 —— 探针报「拿不到画面」，看上去就是程序没画。
///
/// 这里走的是**同一条** <see cref="Markdown.Measure"/> / <see cref="Markdown.Draw"/> 路径，
/// 只是把 Graphics 从窗口换成了内存位图。它证明不了「窗口把它摆在了正确的坐标上」，
/// 但那本来就是 <c>ui-rows.json</c> 的事；它证明的是「这一段排出来是什么样」，
/// 而那正是公式这块唯一说不清的部分。
///
/// 触发方式（都是环境变量，只认 Debug 构建 —— 与 <c>BANGGANG_SHOW_IN_CAPTURE</c> 同一套）：
/// <list type="bullet">
///   <item><c>BANGGANG_RENDER_MD</c>：输入 markdown 文件（UTF-8）。设了才生效。</item>
///   <item><c>BANGGANG_RENDER_PNG</c>：输出 PNG 路径。不设就写到同目录的 <c>out.png</c>。</item>
///   <item><c>BANGGANG_RENDER_W</c>：正文可用宽度，默认 620。</item>
///   <item><c>BANGGANG_RENDER_DARK</c>：<c>1</c> = 暗色主题。</item>
/// </list>
/// </summary>
internal static class OfflineRender
{
    public static bool TryRun()
    {
        string? src = Environment.GetEnvironmentVariable("BANGGANG_RENDER_MD");
        if (string.IsNullOrWhiteSpace(src)) return false;

        try
        {
            string text = File.ReadAllText(src, Encoding.UTF8);
            int cap = Env("BANGGANG_RENDER_W", 620);
            bool dark = Environment.GetEnvironmentVariable("BANGGANG_RENDER_DARK") == "1";

            // 走设置那一份真正的调色板，别在这儿另抄一套颜色 —— 抄的那套一旦和界面
            // 分家，这里看着没问题的那张图就再也证明不了界面上是什么样。
            var settings = new AppSettings();
            settings.ThemeMode = dark ? "dark" : "light";
            settings.ApplyTheme();

            string outPath = Environment.GetEnvironmentVariable("BANGGANG_RENDER_PNG")
                             ?? Path.Combine(Path.GetDirectoryName(src) ?? ".", "out.png");

            var lay = Markdown.Measure(text, cap);

            int pad = 14;
            // 1 = 只打每行；2 = 连公式里每一笔也打（见 Dump / Prims）。
            if (Env("BANGGANG_RENDER_DUMP", 0) > 0) Dump(lay, pad);
            int w = (int)Math.Ceiling(lay.Width) + pad * 2;
            int h = (int)Math.Ceiling(lay.Height) + pad * 2;
            w = Math.Max(40, Math.Min(w, 4000));
            h = Math.Max(20, Math.Min(h, 20000));

            using var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(Theme.ChatBg);
                Markdown.Draw(g, lay, pad, pad, Theme.ChatBg);
            }
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"rendered {w}x{h} -> {outPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("render failed: " + ex);
            return true;                 // 已经是渲染模式了，别再把窗口开起来
        }
        return true;
    }

    private static int Env(string name, int dflt) =>        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : dflt;

    /// <summary>
    /// 把排版结果打出来：每行的高度 / 基线，以及行里每一段是文字还是公式、多宽。
    /// 出问题的时候「屏幕上那一团到底是什么」靠截图看不出来 —— 截图只能说「画错了」，
    /// 这个能说「这一行里有 20001 个同名叶子」。
    ///
    /// 坐标一律给**位图里的绝对像素**（<c>+pad</c> 之后的），并且跟 <see cref="Markdown.Draw"/>
    /// 一样先累加 <c>SpaceBefore</c> 再取 <c>Pitch</c> —— 少加这一项的话每行的 y 都会少一截，
    /// 于是照着这个数裁出来的那块总是偏上，量半天量的是上一行。
    ///
    /// <c>shift</c> 是 <see cref="PhysLine.TextShift"/>：行内公式比正文高时整行往下推的量。
    /// 它和 <c>base</c> 是一对 —— 公式坐在 <c>base</c> 上、文字坐在 <c>base - shift</c> 上，
    /// 两者**必须**落在同一条基线上。屏幕上「公式比周围的字矮一截」就是这一条断了。
    /// </summary>
    private static void Dump(Markdown.Layout lay, int pad)
    {
        // 2 = 连公式里每一笔都打出来（见 Prims）。行级 dump 只有一行字，
        // 一屏几十行公式的时候刷得看不清哪儿是哪儿。
        bool verbose = Environment.GetEnvironmentVariable("BANGGANG_RENDER_DUMP") == "2";
        Console.WriteLine($"layout {lay.Width}x{lay.Height} wrapped={lay.Wrapped}  lines={lay.Lines.Count}");
        float cursor = pad;
        for (int i = 0; i < lay.Lines.Count; i++)
        {
            var ln = lay.Lines[i];
            cursor += ln.SpaceBefore;
            Console.WriteLine($"  line {i}: y={cursor} pitch={ln.Pitch} lineH={ln.LineHeight} base={ln.Base} shift={ln.TextShift} indent={ln.Indent} runs={ln.Runs.Count}");
            float runX = pad + ln.Indent;
            foreach (var r in ln.Runs)
            {
                float at = r.X >= 0 ? pad + r.X : runX;
                string what = r.Math != null
                    ? $"MATH {r.Math.Width}x{r.Math.Height}+{r.Math.Depth} prims={r.Math.Prims.Count}"
                    : "\"" + (r.Text.Length > 60 ? r.Text[..60] + "..." : r.Text) + "\"";
                // 线状装饰（删除线 / 下划线）从截图上只能看出「有一笔」，看不出是哪个 run 的
                // 哪个标记 —— 线画错了位置时先来这里对旗标。
                string fl = (r.Strike ? " STRIKE" : "") + (r.Underline ? " UNDER" : "")
                          + (r.Link != null ? " LINK" : "")
                          + (r.PadL > 0 ? $" PADL{r.PadL}" : "") + (r.PadR > 0 ? $" PADR{r.PadR}" : "");
                Console.WriteLine($"      x={at} w={r.Width} adv={r.Advance} {what}{fl}");
                if (r.Math != null && verbose) Prims(r.Math, at, cursor + ln.Base);
                if (r.X < 0) runX += r.Advance;
            }
            cursor += ln.Pitch;
        }
    }

    /// <summary>
    /// 把一条公式里**每一笔**按位图坐标打出来（<c>BANGGANG_RENDER_DUMP=2</c>）。
    ///
    /// 这一层是给「公式看着不对」用的：行级 dump 只能说「这一块 180x33 的公式画在 x=289」，
    /// 而真正的毛病几乎总是**里面某一笔** —— 括号的字号取大了、分数线落在了基线上、
    /// 上下限压在符号上。字号只有 <see cref="MGlyph.Font"/> 上才有，从截图上是量不出来的
    /// （量出来的永远是「墨迹 60px」，而 60px 是字号乘多少，全看那套字体的比例）。
    /// y 是**基线**，和 <c>MathLayout</c> 的盒子模型一致。
    /// </summary>
    private static void Prims(MBox box, float ox, float oy)
    {
        foreach (var p in box.Prims)
        {
            switch (p)
            {
                case MGlyph g:
                    // asc / desc 一起打：公式「看着不对」几乎总是字号取错了，而字号对不对
                    // 只能拿它自己的**字格**去比 —— 光看点数看不出 52pt 的括号比 14.5pt 的
                    // 内容高出多少（两者的 em 折算系数一样，但字族的上下缘比例并不是 1）。
                    //
                    // ink 那一项是**排的时候认为它的墨迹在哪儿**（相对基线的 em）。
                    // 「字摆在这里」和「凭什么摆在这里」是两个问题，位置不对时必须能分开看。
                    var (it, ib) = MathLayout.InkEm(g.Text.Length > 0 ? g.Text[0] : ' ');
                    Console.WriteLine($"        glyph \"{g.Text}\" size={g.Font.Size}"
                                    + $" asc={MathLayout.Ascent(g.Font):F2} desc={MathLayout.Descent(g.Font):F2}"
                                    + $" ink={it:F3}..{ib:F3}"
                                    + $" px={g.Font.Height} x={ox + g.X} y={oy + g.Y}");
                    break;
                case MRule r:
                    Console.WriteLine($"        rule x={ox + r.X} y={oy + r.Y} w={r.W} h={r.H}");
                    break;
                case MPoly y:
                    var s = new StringBuilder();
                    foreach (var pt in y.Pts) s.Append($"({ox + pt.X},{oy + pt.Y})");
                    Console.WriteLine($"        poly th={y.Th} {s}");
                    break;
            }
        }
    }
}
#endif
