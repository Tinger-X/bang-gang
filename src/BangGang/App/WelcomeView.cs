using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>无对话时的欢迎页（参考主流 LLM 聊天工具的空态设计）。</summary>
internal sealed class WelcomeView : Panel
{
    public event Action? StartRequested;
    public event Action<string>? SuggestionRequested;

    private static readonly (string label, string prompt)[] Chips =
    {
        ("写周报", "帮我写一份本周工作周报，请先列出要点："),
        ("翻译润色", "请把下面这段话翻译成英文，再润色得地道一些："),
        ("代码审查", "请审查下面这段代码，指出问题并给出改进："),
    };

    private readonly Rectangle[] _chipRects = new Rectangle[Chips.Length];
    private Rectangle _startBtn;
    private int _hover = -1;

    public WelcomeView()
    {
        BackColor = Theme.ChatBg;
        // ResizeRedraw 必须开：整块内容是照着 Width / Height 现场摆的（居中、上下留白），
        // 而不带 CS_HREDRAW/CS_VREDRAW 的窗口在尺寸变化时只有 Windows 补画的那一条新增区域
        // 会重画，中间的原像素原样留着 —— 表现成「窗口拉大了，欢迎页还停在旧宽度居中」。
        // 这是实测到的那个 bug 本身，不是预防性写法。
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
               | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = -1;
        for (int i = 0; i < _chipRects.Length; i++)
            if (_chipRects[i].Contains(e.Location)) { h = i; break; }
        if (h != _hover) { _hover = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover != -1) { _hover = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            // 只有真正点了预设入口才开新对话。以前这里最后还有一句无条件的
            // StartRequested?.Invoke()，于是「点空白处」也等于「新建对话」——
            // 用户想先把窗口挪一挪、或者只是随手点一下，就凭空多出一个会话。
            // 开新对话的入口现在只有：这几张预设卡片、下面的新建按钮、侧栏的 +，
            // 以及截图 / 录音快捷键与拖放文件（那几条走 MainForm 的 EnsureActive）。
            for (int i = 0; i < _chipRects.Length; i++)
                if (_chipRects[i].Contains(e.Location)) { SuggestionRequested?.Invoke(Chips[i].prompt); return; }
            if (_startBtn.Contains(e.Location)) { StartRequested?.Invoke(); return; }
        }
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        float cx = Width / 2f;
        float top = Math.Max(60f, Height / 2f - 200);

        var logo = new Rectangle((int)cx - 46, (int)top, 92, 92);
        using (var bg = new SolidBrush(Theme.Accent))
            g.FillEllipse(bg, logo);
        using (var lf = new Font("Microsoft YaHei UI", 38f, FontStyle.Bold))
        {
            string ch = "帮";
            var sz = g.MeasureString(ch, lf);
            g.DrawString(ch, lf, Brushes.White, logo.X + (logo.Width - sz.Width) / 2, logo.Y + (logo.Height - sz.Height) / 2 - 3);
        }

        float y = top + logo.Height + 26;
        using (var t1 = Theme.UI(22f, FontStyle.Bold))
        using (var tb = new SolidBrush(Theme.TextMain))
        {
            string s = "你好，我是帮帮";
            var sz = g.MeasureString(s, t1);
            g.DrawString(s, t1, tb, cx - sz.Width / 2, y);
            y += sz.Height + 8;
        }
        using (var t2 = Theme.UI(12.5f))
        using (var tb2 = new SolidBrush(Theme.TextMuted))
        {
            string s = "有什么可以帮你？支持文字、文件与图片，回复自动排版 Markdown。";
            var sz = g.MeasureString(s, t2);
            g.DrawString(s, t2, tb2, cx - sz.Width / 2, y);
            y += sz.Height + 34;
        }

        // 建议快捷方式（类似参考工具的引导入口）
        float chipY = y;
        var sizes = Chips.Select(c => g.MeasureString(c.label, Theme.UI(11.5f))).ToArray();
        float gap = 12;
        float totalW = sizes.Sum(s => s.Width) + Chips.Length * 46 + gap * (Chips.Length - 1);
        float x = cx - totalW / 2;
        for (int i = 0; i < Chips.Length; i++)
        {
            float cw = sizes[i].Width + 46;
            var rect = new Rectangle((int)x, (int)chipY, (int)cw, 34);
            _chipRects[i] = rect;
            bool over = i == _hover;
            using (var path = Rounded(rect, 17))
            using (var bb = new SolidBrush(over ? Theme.UserBubble : Theme.AsstBubble))
            {
                g.FillPath(bb, path);
                if (over)
                {
                    using var bp = new Pen(Theme.Accent, 1.2f);
                    g.DrawPath(bp, path);
                }
            }
            using (var cf = Theme.UI(11.5f))
            {
                var sz = g.MeasureString(Chips[i].label, cf);
                g.DrawString(Chips[i].label, cf, new SolidBrush(Theme.TextMain), rect.X + (rect.Width - sz.Width) / 2, rect.Y + (rect.Height - sz.Height) / 2);
            }
            x += cw + gap;
        }
        y = chipY + 34 + 26;

        // 新建对话按钮
        var btn = new Rectangle((int)(cx - 90), (int)y, 180, 40);
        _startBtn = btn;
        using (var p = Rounded(btn, 20))
        using (var bb = new SolidBrush(Theme.Accent))
            g.FillPath(bb, p);
        using (var bf = Theme.UI(12.5f, FontStyle.Bold))
        {
            string t = "＋  新建对话";
            var sz = g.MeasureString(t, bf);
            g.DrawString(t, bf, Brushes.White, btn.X + (btn.Width - sz.Width) / 2, btn.Y + 9);
        }
        base.OnPaint(e);
    }

    private static GraphicsPath Rounded(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
