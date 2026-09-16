using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;

namespace BangGang;

internal sealed class InputField : Control, IThemed, IArranged
{
    private readonly TextBox _tb;
    private readonly HintText _ph;
    private bool _focus, _hover, _revealed, _hoverEye;

    /// <summary>文字距输入框左边缘的距离：占位文字与实际输入共用这一个起点。</summary>
    private const int PadX = 12;

    /// <summary>
    /// 输入文字的字体：与同一张卡片里的下拉框（<see cref="DropdownSelect"/> 用 10.5f）一致。
    /// 之前这里是 22f —— 是所有设置控件里唯一一个这么大的字号，显得整页只有输入框是「大字」。
    /// </summary>
    private static readonly Font TextFont = Theme.UI(10.5f);

    public bool Secret { get; set; }
    public string Placeholder { get; private set; } = "";
    public event Action? Changed;

    /// <summary>输入框的最小高度（随字体自动加高，保证文字不被裁切）。</summary>
    public const int MinHeight = 42;

    public InputField(int width = 300, bool secret = false, string placeholder = "")
    {
        Secret = secret;
        Placeholder = placeholder;
        Size = new Size(width, MinHeight);
        BackColor = SC.GroupBg;        // 与所在设置行同色：圆角外不会出现色块
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _tb = new TextBox
        {
            BorderStyle = BorderStyle.None,
            AutoSize = false,             // 否则高度被锁回 PreferredHeight，见 Ui.EditBoxHeight
            Font = TextFont,
            BackColor = SC.FieldBg,       // 与外部边框同色，视觉上只有一个框
            ForeColor = SC.Ink,
            UseSystemPasswordChar = secret,
        };
        _tb.TextChanged += (_, _) =>
        {
            // 眼睛只在「有内容可看」时才出现；内容被清空就顺手把明文状态收回去，
            // 否则下次再输入会直接以明文示人。
            if (Secret && _tb.Text.Length == 0 && _revealed)
            {
                _revealed = false;
                _tb.UseSystemPasswordChar = true;
            }
            Arrange();                    // ShowEye 变了，右侧预留跟着变
            Changed?.Invoke();
            Invalidate();
        };
        _tb.GotFocus += (_, _) => { _focus = true; UpdatePlaceholder(); Invalidate(); };
        _tb.LostFocus += (_, _) => { _focus = false; UpdatePlaceholder(); Invalidate(); };
        _tb.HandleCreated += (_, _) => Ui.PinEditTextLeft(_tb);
        Controls.Add(_tb);
        Ui.PinEditTextLeft(_tb);

        _ph = new HintText
        {
            Font = TextFont,              // 与 TextBox 同字体，两者文字才可能像素对齐
            ForeColor = SC.InkFaint,
            BackColor = SC.FieldBg,
            Hint = placeholder,
        };
        _ph.MouseDown += (_, _) => _tb.Focus();
        Controls.Add(_ph);
        _ph.BringToFront();
        Arrange();
        Height = NeededHeight;
    }

    /// <summary>
    /// 按字体需要的行高自动加高（高 DPI 下也不会遮挡文字）。
    /// 用 <see cref="Ui.EditBoxPadY"/>：行高之外还留了这段余量，否则雅黑的下缘
    /// 实笔（g / j / y / p / q 的尾巴）会被 EDIT 的客户区切掉。
    /// </summary>
    private int NeededHeight => Math.Max(MinHeight, _tb.PreferredHeight + Ui.EditBoxPadY);

    /// <summary>更换占位示例（切换服务商时用）。</summary>
    public void SetPlaceholder(string text)
    {
        Placeholder = text ?? "";
        _ph.Hint = Placeholder;
        UpdatePlaceholder();
        Invalidate();
    }

    /// <summary>
    /// 文字所占的矩形：内部 TextBox 与占位文字层共用同一个盒子，
    /// 左边缘与竖直位置因此严格对齐。
    ///
    /// 不能把 TextBox 拉满整高 —— 单行 EDIT 会在自己的客户区里**顶对齐**文字，
    /// 高度一多余文字就贴上边框（之前字体大到几乎填满整高，才看着像居中）。
    ///
    /// 盒子由 <see cref="Ui.EditBox"/> 给出：上边缘取行高盒子的居中位置
    /// （文字位置不变），只在下方多留 <see cref="Ui.EditBoxPadY"/> 的余量，
    /// 免得 CJK 的下缘实笔（g / j / y / p / q 的尾巴）被客户区切掉。
    /// </summary>
    private Rectangle TextBounds()
    {
        int reserve = PadX + (ShowEye ? 28 : 0);      // 右侧给眼睛图标留位
        return Ui.EditBox(PadX, Math.Max(10, Width - PadX - reserve), Height, _tb);
    }

    /// <summary>把内部文本框与占位文字按当前尺寸摆好（构造与尺寸变化时都要调用）。</summary>
    public void Arrange()
    {
        if (_tb == null || _ph == null) return;
        var r = TextBounds();
        _tb.Bounds = r;
        _ph.Bounds = r;
        UpdatePlaceholder();
    }


    [AllowNull]
    public override string Text
    {
        get => _tb.Text;
        set { _tb.Text = value ?? ""; UpdatePlaceholder(); Invalidate(); }
    }

    /// <summary>编辑中的 Esc 只退出输入，不再冒泡去关闭整个设置浮窗。</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _tb.Focused)
        {
            Parent?.SelectNextControl(this, true, true, true, true);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// 眼睛图标只在「密码框 + 里面真的有内容」时出现。
    /// 空框上挂一只眼睛，点了没有任何东西可看，属于纯噪声。
    /// </summary>
    private bool ShowEye => Secret && _tb.Text.Length > 0;

    private Rectangle EyeRect() => new(Width - 32, (Height - 20) / 2, 20, 20);

    private void UpdatePlaceholder()
    {
        // 位置在 Arrange() 里统一摆放，这里只管显示/隐藏
        _ph.Visible = _tb.Text.Length == 0 && !_focus;
    }

    public void Restyle()
    {
        BackColor = SC.GroupBg;        // 与所在设置行同色：圆角外不会出现色块
        _tb.BackColor = SC.FieldBg; _tb.ForeColor = SC.Ink;
        _ph.BackColor = SC.FieldBg; _ph.ForeColor = SC.InkFaint;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Arrange();
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        if (_hoverEye) { _hoverEye = false; Invalidate(EyeRect()); }
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool hot = ShowEye && EyeRect().Contains(e.Location);
        if (hot != _hoverEye)
        {
            _hoverEye = hot;
            Invalidate(EyeRect());
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (ShowEye && EyeRect().Contains(e.Location))
        {
            _revealed = !_revealed;
            _tb.UseSystemPasswordChar = !_revealed;
            Invalidate();
        }
        else
        {
            _tb.Focus();
        }
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rc = new Rectangle(0, 0, Width - 1, Height - 1);
        RP.Fill(g, rc, 9, SC.FieldBg);
        Color border = _focus ? SC.Accent : (_hover ? SC.Mix(SC.FieldBorder, SC.Accent, 0.35f) : SC.FieldBorder);
        RP.Stroke(g, rc, 9, border, _focus ? 1.4f : 1f);
        if (ShowEye)
        {
            var er = EyeRect();
            if (_hoverEye)
            {
                using var hb = new SolidBrush(SC.Mix(SC.FieldBg, SC.Accent, 0.16f));
                g.FillEllipse(hb, er);
            }
            // 底色交给 DrawGlyph：关闭态那道斜杠要在眼眶上抠出缺口才看得清。
            Gfx.DrawGlyph(g, _revealed ? Glyph.Eye : Glyph.EyeOff, er,
                _revealed ? SC.Accent : (_hoverEye ? SC.Ink : SC.InkMuted), 1.4f, SC.FieldBg);
        }
        base.OnPaint(e);
    }
}

/// <summary>
/// 快捷键录入框：点击进入录入后，**实时显示当前按下的按键**，
/// 等所有按键都抬起时才算录入完成。
/// </summary>
