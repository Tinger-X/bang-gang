
namespace BangGang;

internal sealed class SettingRow : Panel, IThemed, IArranged
{
    private readonly Label _title;
    private readonly Label _desc;
    private readonly Control _right;

    public SettingRow(string title, string desc, Control right)
    {
        _title = new Label
        {
            Text = title,
            AutoSize = false,
            AutoEllipsis = true,
            Font = Theme.UI(11f),
            ForeColor = SC.Ink,
            BackColor = SC.GroupBg,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _desc = new Label
        {
            Text = desc,
            AutoSize = false,
            AutoEllipsis = true,
            Font = Theme.UI(8.5f),
            ForeColor = SC.InkMuted,
            BackColor = SC.GroupBg,
            TextAlign = ContentAlignment.TopLeft,
        };
        _right = right;
        BackColor = SC.GroupBg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Controls.Add(_title);
        Controls.Add(_desc);
        Controls.Add(right);
    }

    public void Restyle()
    {
        BackColor = SC.GroupBg;
        _title.BackColor = SC.GroupBg; _title.ForeColor = SC.Ink;
        _desc.BackColor = SC.GroupBg; _desc.ForeColor = SC.InkMuted;
        _right.BackColor = SC.GroupBg;
        if (_right is IThemed t) t.Restyle();
        Invalidate(true);
    }

    public void Arrange()
    {
        int rw = _right.Width, rh = _right.Height;
        int rightTop = Math.Max(0, (Height - rh) / 2);
        _right.SetBounds(Math.Max(0, Width - rw), rightTop, rw, rh);
        if (_right is IArranged ra) ra.Arrange();
        int tw = Math.Max(20, Width - rw - 18);
        bool two = !string.IsNullOrEmpty(_desc.Text);
        // 行高按字体度量算：写死高度会在高 DPI 或换字体时把文字上下裁掉
        int th = TextHeight(_title);
        int dh = TextHeight(_desc);
        if (two)
        {
            // TopAlign：标题贴着右控件里第一行文字排（多行文本域那种高行用），
            // 不垂直居中 —— 104px 的行里居中会让标题悬在半空，和输入框对不上。
            // 9 与 TextArea.PadY 一致：EDIT 的第一行文字从那儿开始。
            int top = TopAlign ? rightTop + 9 : Math.Max(2, (Height - th - dh) / 2);
            _title.SetBounds(0, top, tw, th);
            _desc.SetBounds(0, top + th, tw, dh);
        }
        else
        {
            _title.SetBounds(0, Math.Max(0, (Height - th) / 2), tw, th);
            _desc.SetBounds(0, 0, tw, dh);
        }
        _desc.Visible = two;
    }

    /// <summary>该标签显示完整一行文字所需的高度（含一点余量，避免上下被裁）。</summary>
    private static int TextHeight(Label l)
    {
        var sz = TextRenderer.MeasureText(l.Text.Length > 0 ? l.Text : "测", l.Font);
        return Math.Max((int)Math.Ceiling(l.Font.GetHeight()) + 2, sz.Height + 2);
    }

    /// <summary>行右侧的控件（切换服务商时用来定位这一行）。</summary>
    public Control RightControl => _right;

    /// <summary>
    /// true 时标题 / 说明贴右控件的顶端排（行比右控件高很多时用，例如提示词的多行文本域）；
    /// false（默认）时整体在行内垂直居中。
    /// </summary>
    public bool TopAlign { get; set; }

    /// <summary>
    /// 是否参与排布。注意不能用 Control.Visible 判断：父级不可见时它也会返回 false。
    /// </summary>
    public bool Shown { get; set; } = true;

    /// <summary>显示/隐藏这一行（同时同步 Control.Visible）。</summary>
    public void SetShown(bool on)
    {
        Shown = on;
        Visible = on;
    }

    /// <summary>更换标题与说明（切换服务商时用）。</summary>
    public void SetText(string title, string desc)
    {
        _title.Text = title;
        _desc.Text = desc ?? "";
        Arrange();
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        Arrange();
    }
}

/// <summary>
/// 圆角文本输入框（可带占位符与密码模式 + 显示/隐藏切换）。
/// 内部文本框与外部边框同底色（看起来就是“一个框”），并按字体高度自适应，
/// 因此高 DPI 下也不会出现文字被遮挡。
/// </summary>
