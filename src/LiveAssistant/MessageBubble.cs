using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;

namespace LiveAssistant;

/// <summary>一条消息的气泡控件（自绘）。</summary>
internal sealed class MessageBubble : Control
{
    public ChatMessage Msg { get; }
    public bool IsUser { get; }

    private const int InnerCap = 560; // 气泡文本最大内宽
    private const int PadX = 14, PadY = 11;

    private readonly List<(Image Img, Size S)> _imgs = new();
    private readonly List<(string Name, float W)> _files = new();
    private Markdown.Layout? _md;

    public MessageBubble(ChatMessage msg, bool isUser)
    {
        Msg = msg;
        IsUser = isUser;
        BackColor = Theme.ChatBg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        Rebuild();
    }

    /// <summary>依据消息重建内部布局并计算控件尺寸。</summary>
    private void Rebuild()
    {
        DisposeImgs();
        _imgs.Clear();
        _files.Clear();

        if (Msg.Attachments != null)
            foreach (var a in Msg.Attachments)
            {
                if (a.Kind == "image")
                {
                    var img = a.LoadImage(280, 190);
                    if (img != null) _imgs.Add((img, img.Size));
                }
                else
                {
                    string n = string.IsNullOrWhiteSpace(a.Name) ? Path.GetFileName(a.Path ?? "") : a.Name;
                    if (n.Length == 0) n = "(文件)";
                    if (n.Length > 30) n = n[..30] + "…";
                    float w = TextRenderer.MeasureText(n, Theme.UI(10.5f)).Width + 44;
                    _files.Add((n, Math.Min(w, InnerCap)));
                }
            }

        _md = Markdown.Measure(Msg.Text ?? "", InnerCap);

        float attachH = 0;
        foreach (var (_, s) in _imgs) attachH += s.Height + 6;
        if (_imgs.Count > 0) attachH -= 6;
        foreach (var _ in _files) attachH += 34;
        if (_files.Count > 0 && _imgs.Count > 0) attachH += 4;

        float contentW = Math.Max(60f, _md.Width);
        foreach (var (_, s) in _imgs) contentW = Math.Max(contentW, s.Width);
        foreach (var f in _files) contentW = Math.Max(contentW, f.W);
        Width = (int)Math.Min(InnerCap + PadX * 2, contentW + PadX * 2);
        Height = (int)(PadY + attachH + _md.Height + PadY);
        if (Msg.Text.Length == 0 && _imgs.Count == 0 && _files.Count == 0) Height = 28;
    }

    private void DisposeImgs()
    {
        foreach (var (img, _) in _imgs) img.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeImgs();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        Color bg = IsUser ? Theme.UserBubble : Theme.AsstBubble;
        using (var path = RoundedRect(0, 0, Width - 1, Height - 1, 12))
        using (var b = new SolidBrush(bg))
            g.FillPath(b, path);

        float x = PadX;
        float y = PadY;
        foreach (var (img, s) in _imgs)
        {
            if (s.Width > 0 && s.Height > 0)
                g.DrawImage(img, x, y, s.Width, s.Height);
            y += s.Height + 6;
        }
        foreach (var (name, w) in _files)
        {
            float h = 24;
            using (var path = RoundedRect(x, y + 2, w, h, 6))
            using (var bb = new SolidBrush(IsUser ? Blend(bg, Color.White, .6f) : Blend(bg, Color.White, .6f)))
            {
                g.FillPath(bb, path);
            }
            using (var linePen = new Pen(Color.FromArgb(150, 150, 150), 1.4f))
            {
                float mid = y + 2 + h / 2;
                g.DrawLine(linePen, x + 7, mid - 4, x + 7, mid + 4);
                g.DrawLine(linePen, x + 4, mid, x + 10, mid);
            }
            g.DrawString(name, Theme.UI(10.5f), Brushes.Gray, x + 15, y + 5);
            y += 34;
        }
        if (_files.Count > 0) y += 4;

        if (_md != null) Markdown.Draw(g, _md, x, y, InnerCap);

        base.OnPaint(e);
    }

    private static Color Blend(Color a, Color b, float k) =>
        Color.FromArgb((int)(a.R * k + b.R * (1 - k)), (int)(a.G * k + b.G * (1 - k)), (int)(a.B * k + b.B * (1 - k)));

    internal static GraphicsPath RoundedRect(float x, float y, float w, float h, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
