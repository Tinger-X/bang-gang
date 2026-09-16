using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>
/// 多行文本域（设置界面用）：圆角外框 + 内部原生多行 EDIT + 占位文字层，
/// 与 <see cref="InputField"/> 是同一套画法，只是行数由行数而不是单行决定。
///
/// 高度按 <see cref="Ui.EditLinePitch"/> 算，也就是 **EDIT 自己排版用的那个行距**
/// （<c>tmHeight + tmExternalLeading</c>，不是 <c>Font.Height</c>），并且取成行距的
/// 整数倍 —— 多行 EDIT 只画**完整装得下**的行，盒子矮 1px 就整整少画一行，
/// 底下空着一片却看不见最后那行。这条在输入卡片那边已经踩过一次，见 InputPanel.LayoutCard。
/// </summary>
internal sealed class TextArea : Control, IThemed, IMessageFilter
{
    /// <summary>内部 EDIT 的字体。与 <see cref="InputField"/> / 下拉框同一个字号。</summary>
    private static readonly Font TextFont = Theme.UI(10.5f);

    private const int PadX = 12;
    private const int PadY = 9;
    private const int Radius = 9;

    private readonly TextBox _tb;
    private readonly HintText _ph;
    private bool _focus, _hover;

    public event Action? Changed;

    public TextArea(int width, int lines, string placeholder)
    {
        int pitch = Ui.EditLinePitch(TextFont);
        Size = new Size(width, Math.Max(2, lines) * pitch + PadY * 2);
        BackColor = SC.GroupBg;        // 与所在设置行同色：圆角外不会出现色块
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _tb = new TextBox
        {
            Multiline = true,
            BorderStyle = BorderStyle.None,
            AutoSize = false,              // 否则高度被锁回 PreferredHeight
            Font = TextFont,
            BackColor = SC.FieldBg,
            ForeColor = SC.Ink,
            WordWrap = true,
            AcceptsReturn = true,
            AcceptsTab = false,
            // 不挂系统滚动条：EDIT 一旦有 WS_VSCROLL 就**永远**画着那条灰槽，
            // 哪怕内容才两行，和这套配色也不是一路（同 InputPanel 里那条注释）。
            // 滚动能力照旧，滚轮自己在 PreFilterMessage 里折成 EM_LINESCROLL。
            ScrollBars = ScrollBars.None,
        };
        _tb.TextChanged += (_, _) => { UpdatePh(); Changed?.Invoke(); Invalidate(); };
        _tb.GotFocus += (_, _) => { _focus = true; UpdatePh(); Invalidate(); };
        _tb.LostFocus += (_, _) => { _focus = false; UpdatePh(); Invalidate(); };
        Controls.Add(_tb);

        _ph = new HintText
        {
            Font = TextFont,               // 与 EDIT 同字体，两者文字才可能像素对齐
            ForeColor = SC.InkFaint,
            BackColor = SC.FieldBg,
            Hint = placeholder ?? "",
        };
        _ph.MouseDown += (_, _) => _tb.Focus();
        Controls.Add(_ph);
        _ph.BringToFront();

        Arrange();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Application.AddMessageFilter(this);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Application.RemoveMessageFilter(this);
        base.OnHandleDestroyed(e);
    }

    /// <summary>内部 EDIT 的矩形：左右留 <see cref="PadX"/>，高度正好是行距的整数倍。</summary>
    private Rectangle TextBounds()
        => new(PadX, PadY, Math.Max(10, Width - PadX * 2), Math.Max(1, Height - PadY * 2));

    /// <summary>把内部文本框与占位层按当前尺寸摆好（构造与尺寸变化时都要调用）。</summary>
    private void Arrange()
    {
        if (_tb == null || _ph == null) return;
        var r = TextBounds();
        _tb.Bounds = r;
        _ph.Bounds = r;
        UpdatePh();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Arrange();
    }

    [AllowNull]
    public override string Text
    {
        get => _tb.Text;
        set { _tb.Text = value ?? ""; UpdatePh(); Invalidate(); }
    }

    /// <summary>
    /// 丢掉撤销缓冲。程序化写入（Rebind 按设置回填）之后按 Ctrl+Z 会把**上一个档位的文字**
    /// 捞回来，那既不是用户输入的、也不在基线里，页面于是莫名其妙变成「有未保存的修改」。
    /// </summary>
    public void ClearUndoBuffers()
    {
        if (!_tb.IsHandleCreated) return;      // 句柄没建时 ClearUndo 发不出消息
        _tb.ClearUndo();
    }

    private void UpdatePh()
    {
        if (_ph == null || _tb == null) return;
        // 位置由 Arrange 统一摆；这里只管显示/隐藏
        _ph.Visible = _tb.Text.Length == 0 && !_focus;
    }

    /// <summary>
    /// 编辑中的 Esc 只退出输入，不冒泡去关闭整个设置浮窗（与 <see cref="InputField"/> 一致）。
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _tb.Focused)
        {
            Parent?.SelectNextControl(this, true, true, true, true);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private const int WM_MOUSEWHEEL = 0x020A;
    private const int EM_LINESCROLL = 0x00B6;

    /// <summary>
    /// EDIT 没有滚动条，滚轮就没人管了。压在文本区上时把滚轮折成 <c>EM_LINESCROLL</c>
    /// 直接送给它，否则滚轮会一路冒泡上去滚整个设置页面。
    ///
    /// 用消息里的屏幕坐标而不是 <c>Cursor.Position</c>：后者要等这条消息被处理时才读，
    /// UI 线程一忙就读到已经走掉的鼠标位置（同 InputPanel 里那条注释）。
    /// </summary>
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_MOUSEWHEEL) return false;
        if (!_tb.IsHandleCreated || !_tb.Visible) return false;

        long lp = m.LParam.ToInt64();
        var screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
        if (!_tb.ClientRectangle.Contains(_tb.PointToClient(screen))) return false;

        int notches = (short)((long)m.WParam >> 16) / 120;
        if (notches == 0) return false;
        int step = SystemInformation.MouseWheelScrollLines;
        if (step <= 0) step = 3;
        Win32.SendMessage(_tb.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)(-notches * step));
        return true;
    }

    public void Restyle()
    {
        BackColor = SC.GroupBg;
        _tb.BackColor = SC.FieldBg; _tb.ForeColor = SC.Ink;
        _ph.BackColor = SC.FieldBg; _ph.ForeColor = SC.InkFaint;
        Invalidate(true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rc = new Rectangle(0, 0, Width - 1, Height - 1);
        RP.Fill(g, rc, Radius, SC.FieldBg);
        Color border = _focus ? SC.Accent : (_hover ? SC.Mix(SC.FieldBorder, SC.Accent, 0.35f) : SC.FieldBorder);
        RP.Stroke(g, rc, Radius, border, _focus ? 1.4f : 1f);
        base.OnPaint(e);
    }
}
