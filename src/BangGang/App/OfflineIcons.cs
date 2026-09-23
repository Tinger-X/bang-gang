#if DEBUG
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text;

namespace BangGang;

/// <summary>
/// 把全部线框图标与开关的各种状态离屏画成两张 PNG，用来**看**这两样东西。
///
/// 为什么值得单独有个入口：图标是手画的（一堆相对 <c>s</c> 的系数），开关的禁用态是新加的，
/// 两者都「不看就等于没验」—— 而要看它们，常规办法是起程序 → 点开设置 → 点进那一页 → 截屏，
/// 中间任何一步（窗口半透明、桌面挂了、点歪了）都会让「看不到」和「画错了」长得一模一样。
/// 它们又都是纯粹的绘制函数/控件，离屏画一遍就够，不必扯上窗口和桌面。
///
/// 触发方式（只认 Debug 构建）：
/// <list type="bullet">
///   <item><c>BANGGANG_ICONS</c>：输出目录。设了才生效，两张 PNG 落在它下面。</item>
/// </list>
/// </summary>
internal static class OfflineIcons
{
    public static bool TryRun()
    {
        string? dir = Environment.GetEnvironmentVariable("BANGGANG_ICONS");
        if (string.IsNullOrWhiteSpace(dir)) return false;

        try
        {
            Directory.CreateDirectory(dir);
            var s = new AppSettings();
            DrawIcons(Path.Combine(dir, "out-icons.png"), s);
            DrawRealSize(Path.Combine(dir, "out-icons-18.png"), s);
            DrawSwitches(Path.Combine(dir, "out-switches.png"), s);
            DrawNavIcons(Path.Combine(dir, "out-navicons.png"), s);
        }
        catch (Exception ex) { Console.WriteLine("icon sheet failed: " + ex); }
        return true;
    }

    /// <summary>每个图标画两遍：浅色主题一遍、深色一遍 —— 线框图标在两种底色上都得看得清。</summary>
    private static void DrawIcons(string path, AppSettings settings)
    {
        var names = Enum.GetValues<Glyph>();
        const int cell = 96, cols = 7, pad = 12;
        int rows = (names.Length + cols - 1) / cols;
        int w = cols * cell + pad * 2;
        int h = rows * (cell + 26) + pad * 2;

        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            // 上半张浅色、下半张深色：同一套图标要在两种底上都立得住。
            int halfY = pad + rows / 2 * (cell + 26);
            using (var light = new SolidBrush(Color.White)) g.FillRectangle(light, 0, 0, w, halfY);
            using (var dark = new SolidBrush(Color.FromArgb(23, 25, 29))) g.FillRectangle(dark, 0, halfY, w, h - halfY);

            for (int i = 0; i < names.Length; i++)
            {
                int col = i % cols, row = i / cols;
                float x = pad + col * cell, y = pad + row * (cell + 26);
                bool onDark = y >= halfY;
                settings.ThemeMode = onDark ? "dark" : "light";
                settings.ApplyTheme();

                Color ink = onDark ? Color.FromArgb(232, 235, 239) : Color.FromArgb(30, 34, 40);
                var box = new RectangleF(x + (cell - 56) / 2f, y + (cell - 56) / 2f, 56, 56);
                Gfx.DrawGlyph(g, names[i], box, ink, 1.6f, onDark ? Theme.ChatBg : Theme.ChatBg);
                using var f = new Font("Consolas", 8.5f);
                using var tb = new SolidBrush(ink);
                g.DrawString(names[i].ToString(), f, tb, x + 4, y + cell);
            }
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"icons ({names.Length}) -> {path}");
    }

    /// <summary>
    /// 每个图标按**实际使用尺寸**画一遍：导航里就是 18px、笔宽 1.5（见 NavItem）。
    ///
    /// 上面那张大表是 56px 画的，只能说明形状本身对不对 —— 而一个形状在 56px 下成立、
    /// 在 18px 下糊成一团是常事（细节挤没了、笔画粘在一起）。这张图就是用来卡这一关的，
    /// 看的时候要按原尺寸看，或者整数倍最近邻放大，别用平滑缩放（那会把糊的地方抹平）。
    /// </summary>
    private static void DrawRealSize(string path, AppSettings settings)
    {
        var names = Enum.GetValues<Glyph>();
        const int box = 18, step = 34, pad = 10;

        settings.ThemeMode = "light";
        settings.ApplyTheme();

        using var bmp = new Bitmap(pad * 2 + step * names.Length, box + pad * 2 + 14);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.White);
            for (int i = 0; i < names.Length; i++)
            {
                float x = pad + i * step;
                Gfx.DrawGlyph(g, names[i], new RectangleF(x, pad, box, box), Color.FromArgb(30, 34, 40), 1.5f);
                using var f = new Font("Consolas", 6.5f);
                g.DrawString(names[i].ToString()[..Math.Min(6, names[i].ToString().Length)], f,
                             Brushes.Gray, x - 2, pad + box + 2);
            }
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"icons @18px -> {path}");
    }

    /// <summary>
    /// 设置页左栏那套图标，两档尺寸画一遍。
    ///
    /// 大图看形状**像不像原图**（弧解错了、路径没闭合，大图上一眼看得出来）；
    /// 18px 那一行按真实尺寸看它们彼此匀不匀 —— 这几张图各自的留白不一样，
    /// 归一化按路径外框铺满（见 NavIcons.Draw），匀不匀只有并排看才知道。
    /// </summary>
    private static void DrawNavIcons(string path, AppSettings settings)
    {
        var icons = Enum.GetValues<NavIcon>();
        const int big = 96, small = NavItem.NavIconBox, pad = 18, colW = 120;

        settings.ThemeMode = "light";
        settings.ApplyTheme();

        int w = pad * 2 + colW * icons.Length;
        int h = pad * 2 + big + 26 + small + 26;

        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.White);

            // 18px 那一行画在一条浅色带上，模拟左栏的底色
            int bandY = pad + big + 26;
            using (var band = new SolidBrush(SC.RailBg)) g.FillRectangle(band, 0, bandY, w, small + 26);

            foreach (var ic in icons)
                Console.WriteLine($"  {ic,-6} 外框 {NavIcons.Bounds(ic)}");

            for (int i = 0; i < icons.Length; i++)
            {
                float x = pad + i * colW;
                NavIcons.Draw(g, icons[i], new RectangleF(x, pad, big, big), Color.FromArgb(30, 34, 40));
                NavIcons.Draw(g, icons[i], new RectangleF(x, bandY + 10, small, small), SC.InkMuted);

                using var f = new Font("Consolas", 8f);
                g.DrawString(icons[i].ToString(), f, Brushes.Gray, x, pad + big + 4);
                g.DrawString(small + "px", f, Brushes.Gray, x, bandY + small + 12);
            }
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"nav icons -> {path}");
    }

    /// <summary>
    /// 开关的四种样子。禁用态是这一版新加的（总开关关掉时下面那排要灰掉），
    /// 关键是「灰了但仍看得出开着还是关着」——全部涂成一色就分不出「关掉了」和「不许动」了。
    /// </summary>
    private static void DrawSwitches(string path, AppSettings settings)
    {
        settings.ThemeMode = "light";
        settings.ApplyTheme();

        var cases = new (string Label, bool On, bool Enabled)[]
        {
            ("开·可改", true, true),
            ("关·可改", false, true),
            ("开·禁用", true, false),
            ("关·禁用", false, false),
        };

        const int rowH = 54, pad = 16, labelW = 150;
        int w = pad * 2 + labelW + 260;
        int h = pad * 2 + rowH * cases.Length * 2;

        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (var bg = new SolidBrush(SC.GroupBg)) g.FillRectangle(bg, 0, 0, w, h);

            int y = pad;
            for (int pass = 0; pass < 2; pass++)
            {
                settings.ThemeMode = pass == 0 ? "light" : "dark";
                settings.ApplyTheme();
                using (var bg = new SolidBrush(SC.GroupBg)) g.FillRectangle(bg, 0, y - 4, w, rowH * cases.Length + 8);

                foreach (var (label, on, enabled) in cases)
                {
                    using (var f = new Font("Consolas", 9.5f))
                    using (var tb = new SolidBrush(SC.Ink))
                        g.DrawString((pass == 0 ? "浅 " : "深 ") + label, f, tb, pad, y + 16);

                    using var sw = new ToggleSwitch { On = on, Enabled = enabled };
                    using var sb = new Bitmap(sw.Width, sw.Height);
                    sw.DrawToBitmap(sb, new Rectangle(0, 0, sw.Width, sw.Height));
                    g.DrawImage(sb, pad + labelW, y + 14);
                    y += rowH;
                }
                y += 10;
            }
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"switches -> {path}");
    }
}
#endif
