using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>顶部工具条：品牌 + 状态 + 最大化 + 关闭；整条可拖动。</summary>
internal sealed class ChromeBar : Panel, IThemed
{
    public event Action? DragRequested;
    public event Action? CloseRequested;
    public event Action? MaximizeRequested;
    public string StatusText { get; private set; } = "";
    private readonly Label _status;
    private readonly Label _brand;
    private readonly IconButton _max;
    private readonly IconButton _close;

    private const int BtnSize = 28;
    private const int BtnTop = 5;
    private const int CloseRight = 12;    // 关闭按钮右边缘到工具条右端的空档
    private const int BtnGap = 6;         // 关闭与最大化之间的空档

    /// <summary>
    /// 「设置打开期间也要能点」的那些按钮（最大化 / 关闭）相对本工具条的矩形。
    /// 工具条贴在客户区左上角，所以这份坐标直接就是主窗口客户区坐标 ——
    /// 设置浮窗据此在自己的 Region 上给这些按钮开洞（<c>SettingsOverlay.AppChromeHoles</c>）。
    /// </summary>
    public Rectangle[] ChromeHoleBounds => new[] { _max.Bounds, _close.Bounds };

    /// <summary>按钮被重新摆放（工具条改宽）时触发，订阅者据此同步自己缓存的矩形。</summary>
    public event Action? ChromeButtonsMoved;

    public ChromeBar()
    {
        Height = 38;
        BackColor = Theme.PanelBg;
        // 本文件里自绘的控件一律开 ResizeRedraw（见 WindowFrame / WelcomeView）。
        // 这一条本身不是必须的 —— 底边线画在 y=Height-1、横跨 0..Width，而尺寸变化时 Windows
        // 补画的恰好就是它要延伸的那条新增区域，所以它不会画旧。纯粹是补齐一致性，
        // 别给下一个往这条上画东西的人留坑（缺 CS_HREDRAW/CS_VREDRAW 时的症状见 WelcomeView）。
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
               | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);

        _brand = new Label
        {
            Text = "● 帮帮",
            AutoSize = true,
            Font = Theme.UI(10.5f, FontStyle.Bold),
            ForeColor = Theme.Accent,
            BackColor = Theme.PanelBg,
            Location = new Point(16, 9),
        };
        Controls.Add(_brand);

        _status = new Label
        {
            AutoSize = true,
            Font = Theme.UI(9.5f),
            ForeColor = Theme.TextMuted,
            BackColor = Theme.PanelBg,
            Location = new Point(180, 11),
        };
        Controls.Add(_status);

        _max = new IconButton(IconButton.Kind.Maximize, Theme.PanelBg);
        _max.Click += (_, _) => MaximizeRequested?.Invoke();
        Controls.Add(_max);

        _close = new IconButton(IconButton.Kind.Close, Theme.PanelBg);
        _close.Click += (_, _) => CloseRequested?.Invoke();
        Controls.Add(_close);

        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) DragRequested?.Invoke(); };
        _brand.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) DragRequested?.Invoke(); };
        Resize += (_, _) => PlaceButtons();
        PlaceButtons();
    }

    /// <summary>两个按钮一起靠右端排开：关闭在最右，最大化紧挨着它左边。</summary>
    private void PlaceButtons()
    {
        _close.Location = new Point(Width - CloseRight - BtnSize, BtnTop);
        _max.Location = new Point(_close.Left - BtnGap - BtnSize, BtnTop);
        ChromeButtonsMoved?.Invoke();
    }

    /// <summary>最大化状态变了：按钮在「最大化 / 还原」两个字形之间切换。</summary>
    public void SetMaximized(bool on)
    {
        var want = on ? IconButton.Kind.Restore : IconButton.Kind.Maximize;
        if (_max.Icon == want) return;
        _max.Icon = want;
        _max.Invalidate();
    }

    /// <summary>主题切换后重新着色（顶栏也要跟随暗色）。</summary>
    public void Restyle()
    {
        BackColor = Theme.PanelBg;
        _brand.BackColor = Theme.PanelBg;
        _brand.ForeColor = Theme.Accent;
        _status.BackColor = Theme.PanelBg;
        SetStatus(StatusText);
        _max.Restyle();
        _close.Restyle();
        Invalidate();
    }

    public void SetStatus(string s)
    {
        StatusText = s;
        _status.Text = s;
        _status.ForeColor = s.Contains('●') ? Color.Crimson : Theme.TextMuted;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}
