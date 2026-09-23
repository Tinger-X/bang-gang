#if DEBUG
using System.Drawing;
using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>
/// 上下文仪表的离屏出图：把各种百分比下的那一行**画成 PNG**，明暗两版。
///
/// 为什么要它：圆环是手画的，而「画出来长什么样」跟窗口在不在、桌面画不画得出来
/// 一点关系都没有 —— 这条理由与 <c>OfflineRender</c> / <c>OfflineIcons</c> 完全一样。
/// 更要紧的是**明暗是两套调色板**：只对着一套调好，换主题才发现环看不见了，
/// 是这类自绘控件最典型的错法，所以这里一次出两版。
///
/// <c>BANGGANG_CTX_METER=1</c> 触发；<c>BANGGANG_CTX_METER_PNG</c> 指定输出目录。
/// </summary>
internal static class OfflineMeter
{
    /// <summary>要画的几档。<c>live=false</c> 是「上一轮留下的静态数字」那种淡色状态。</summary>
    private static readonly (int Used, int Window, double Tps, bool Live)[] Cases =
    {
        (0,       32768, 0,    true),   // 空：只有轨道
        (4200,    32768, 12.4, true),
        (20000,   32768, 46.8, true),
        (28000,   32768, 8.4,  false),  // 空闲：数字变淡
        (32000,   32768, 55.1, true),   // 接近满：环转红
        (32768,   32768, 3.2,  true),   // 满圈：走 DrawEllipse 那条路
        (999900,  999900, 999.9, true), // 最坏情况的宽度
    };

    public static bool TryRun()
    {
        if (Environment.GetEnvironmentVariable("BANGGANG_CTX_METER") != "1") return false;

        try
        {
            Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput(),
                new System.Text.UTF8Encoding(false)) { AutoFlush = true });
        }
        catch { /* 包不上就按默认走 */ }

        try { Run(); }
        catch (Exception ex) { Console.WriteLine("meter render failed: " + ex); }
        return true;
    }

    private static void Run()
    {
        string dir = Environment.GetEnvironmentVariable("BANGGANG_CTX_METER_PNG") ?? "";
        if (dir.Length == 0) dir = Directory.GetCurrentDirectory();

        const int w = 400;
        const int rowH = 38;      // 与 InputPanel.BottomRowH 一致 —— 出图要能反映真实行高
        int h = rowH * Cases.Length;

        foreach (bool dark in new[] { false, true })
        {
            var settings = new AppSettings { ThemeMode = dark ? "dark" : "light" };
            settings.ApplyTheme();

            using var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Theme.InputBg);
                g.SmoothingMode = SmoothingMode.AntiAlias;
            }

            for (int i = 0; i < Cases.Length; i++)
            {
                var (used, window, tps, live) = Cases[i];
                using var meter = new ContextMeter { Size = new Size(w, rowH) };
                // 首次 Set 会直接到位（不做缓动），所以不用等那个 15ms 的 Timer
                meter.Set(new CtxInfo(used, window, tps, live));
                using var one = new Bitmap(w, rowH);
                meter.DrawToBitmap(one, new Rectangle(0, 0, w, rowH));
                using var g = Graphics.FromImage(bmp);
                g.DrawImage(one, 0, i * rowH);
            }

            string outPath = Path.Combine(dir, dark ? "out-meter-dark.png" : "out-meter-light.png");
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"rendered {w}x{h} -> {outPath}");
        }

        // 顺带报一下预留宽度，跟 InputPanel 那边实际分到的比一比
        Console.WriteLine($"最坏情况宽度 {ContextMeter.MeasureWorstWidth()}px（InputPanel 还会再加 2×HintPadX=96）");

        RenderPanel(dir);
    }

    /// <summary>
    /// 把**整个输入面板**画出来，两种状态各一张：还没开口（快捷键提示）与开口之后（上下文仪表）。
    ///
    /// 单画那个圆环证明不了集成对不对 —— 真正有风险的是「两个控件共用 LayoutCard 算出来的
    /// 同一个矩形、同一时刻只有一个可见」，以及新的宽度预留会不会把两侧按钮挤掉。
    /// 这两件事只有把整块面板画出来才看得见。
    /// </summary>
    private static void RenderPanel(string dir)
    {
        const int w = 1200;      // 默认窗口宽度，接近用户实际看到的样子

        foreach (bool dark in new[] { false, true })
        {
            var settings = new AppSettings { ThemeMode = dark ? "dark" : "light" };
            settings.ApplyTheme();

            var states = new (string Tag, CtxInfo Info)[]
            {
                ("hint", default),                                     // 还没开口：显示快捷键提示
                ("meter", new CtxInfo(20000, 32768, 46.8, true)),      // 开口之后：显示仪表
            };

            int h = 0;
            var panels = new List<(string Tag, InputPanel Panel, Bitmap Bmp)>();
            foreach (var (tag, info) in states)
            {
                var p = new InputPanel { Width = w };
                p.SetContext(info);
                p.Height = p.PreferredHeight;
                // 量真实矩形，别靠截图目测 —— 仓库里那条「EnumChildWindows + GetWindowRect
                // 比截图可靠」的经验在这里同样成立（DrawToBitmap 画子控件有自己的怪癖）。
                foreach (Control child in p.Controls)
                    Console.WriteLine($"    {tag}  {child.GetType().Name,-14} "
                                      + $"x={child.Left,5} w={child.Width,5} right={child.Right,5} vis={child.Visible}");
                var bmp = new Bitmap(w, p.Height);
                p.DrawToBitmap(bmp, new Rectangle(0, 0, w, p.Height));
                panels.Add((tag, p, bmp));
                h += p.Height;
            }

            using var canvas = new Bitmap(w, h);
            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(Theme.ChatBg);
                int y = 0;
                foreach (var (_, panel, bmp) in panels)
                {
                    g.DrawImage(bmp, 0, y);
                    y += panel.Height;
                }
            }

            string outPath = Path.Combine(dir, dark ? "out-panel-dark.png" : "out-panel-light.png");
            canvas.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"rendered panel {w}x{h} -> {outPath}");

            foreach (var (_, panel, bmp) in panels) { bmp.Dispose(); panel.Dispose(); }
        }
    }
}
#endif
