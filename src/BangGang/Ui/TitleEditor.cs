using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>
/// 对话标题的**就地编辑框**：一枚自绘的圆角输入条，里面套一个真正吃键盘的 TextBox。
///
/// 为什么不是弹一个对话框：标题条上那枚铅笔点下去，用户改的就是屏幕上那一行字，
/// 改完它还在原地。弹窗要么带一层蒙版（本程序只有设置浮窗用蒙版，那是全屏的事），
/// 要么在标题条下面浮一张卡片 —— 两种都在「改一个词」这件事上加了一道无谓的确认。
///
/// 三条交互约定：
///
/// 1. <b>回车提交</b>、<b>Esc 取消</b>、<b>点到别处提交</b>。<see cref="Control.ProcessCmdKey"/>
///    那一条不能省：Esc 在 WinForms 里是「对话框键」，单行 EDIT 根本不会把它交出来当 KeyDown
///    （同一个坑见 <see cref="InputField"/>、<see cref="TextArea"/>），只挂 KeyDown 的话
///    按 Esc 会一路冒泡到主窗口的快捷键处理里去。
/// 2. <b>点走等于提交</b>，不是取消 —— 用户点到别处通常是「改完了」。取消有明确的出口（Esc），
///    而「点空白处丢掉刚敲的字」没有第二次机会。这一条**不能只靠 <c>Leave</c>**：
///    点在一个不可聚焦的控件上（对话区、标题条自己、侧栏的空白）时，焦点根本没被挪走，
///    那个事件不会来，看上去就像输入条卡住了。所以还挂了一道消息过滤器，
///    见 <see cref="PreFilterMessage"/>。
/// 3. 提交 / 取消都只是把 <c>Visible</c> 置假，**不销毁、不重建**。这个控件在主窗口构造时
///    就位，之后一辈子都在树上。
///
/// 空标题的处置不在这里：本控件只管把用户敲的那串字原样交出去
/// （见 <see cref="Committed"/>），「空串算不算一次修改」由主窗口决定。
/// </summary>
internal sealed class TitleEditor : Control, IThemed, IMessageFilter
{
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    /// <summary>文字距输入条左右边缘的距离。「居中」说的是这一段里面居中。</summary>
    private const int PadX = 14;

    /// <summary>输入条的高度。标题条 48px，上下各留 8px，看着不顶。</summary>
    public const int BarH = 32;

    private readonly TextBox _tb;

    /// <summary>用户按下回车 / 点到别处：把他敲的那串字交出去（可能是空串）。</summary>
    public event Action<string>? Committed;

    /// <summary>用户按了 Esc：这次修改作废，标题保持原样。</summary>
    public event Action? Cancelled;

    public TitleEditor()
    {
        BackColor = Theme.PanelBg;      // 四角露的就是标题条的底色
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
               | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        Visible = false;

        _tb = new TextBox
        {
            BorderStyle = BorderStyle.None,
            AutoSize = false,           // 否则高度被锁回 PreferredHeight，见 Ui.EditBox
            TextAlign = HorizontalAlignment.Center,
            // 与标题条上那行标题同一个字体：点下去那一下，字的位置和大小都不该动。
            Font = Theme.UI(13f, FontStyle.Bold),
            BackColor = Theme.InputBg,
            ForeColor = Theme.TextMain,
        };
        _tb.KeyDown += OnKeyDown;
        _tb.Leave += (_, _) => { if (Visible) Commit(); };
        Controls.Add(_tb);
    }

    /// <summary>开始编辑：把当前标题装进来、全选、拿焦点。</summary>
    /// <remarks>
    /// 全选是给「改一个词」准备的：点开就能直接重打。光标停在末尾也行，
    /// 但那时用户要先按一串退格 —— 而这条标题多半是模型刚生成的，本来就该整个换掉。
    /// </remarks>
    public void BeginEdit(string title)
    {
        _tb.Text = title ?? "";
        Visible = true;
        BringToFront();
        Arrange();
        _tb.Focus();
        _tb.SelectAll();
    }

    /// <summary>
    /// 收起编辑框（不提交、不取消 —— 由调用方自己决定要不要把 <see cref="Committed"/> 补上）。
    ///
    /// <c>Visible = false</c> 会让系统把焦点从里面那个 TextBox 挪走，于是 <c>Leave</c>
    /// 那一手会同步跑起来；<b>先把 Visible 置假再让它跑</b>，那一手才认得出「这不是用户点走的」。
    /// </summary>
    public void Close() => Visible = false;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            // 不压这两下的话，单行 EDIT 收到回车会「叮」一声（那是系统给「这里不能换行」的提示音）。
            e.Handled = true;
            e.SuppressKeyPress = true;
            Commit();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            Cancel();
        }
    }

    /// <summary>Esc 的兜底入口，理由见类注释第 1 条。</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (Visible && keyData == Keys.Escape)
        {
            Cancel();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void Commit()
    {
        string t = _tb.Text;
        Visible = false;
        Committed?.Invoke(t);
    }

    private void Cancel()
    {
        Visible = false;
        Cancelled?.Invoke();
    }

    /// <summary>把内部 TextBox 摆到输入条里（构造与尺寸变化都要走）。</summary>
    private void Arrange()
    {
        if (_tb == null) return;
        // 内部 EDIT 铺满两侧留白之间的整条：文字是 Center 对齐的，它的宽度就是
        // 「居中」的基准 —— 让盒子只裹住文字，居中就无从谈起了。
        _tb.Bounds = Ui.EditBox(PadX, Math.Max(20, Width - PadX * 2), Height, _tb);
    }

    // ---------------- 点到别处也算改完 ----------------

    /// <summary>
    /// 编辑期间把整个应用里的鼠标按下都看一遍：落在这条输入条之外的，一律**提交**。
    ///
    /// 为什么不靠 <c>Leave</c> 就够（那是原来唯一的出口）：焦点只有在点到**可聚焦**的控件上
    /// 才会挪走。用户改完标题多半是回头去点对话区、或者干脆点标题条上的空白 —— 那些是不可聚焦
    /// 的面板，焦点原封不动，<c>Leave</c> 一次都不会来，屏幕上就是「点了别处，输入条还在」。
    ///
    /// 消息过滤器而不是给每个控件挂 <c>MouseDown</c>：窗口里能点的东西遍布好几层，
    /// 逐个挂既漏得掉后加的，也会被截图浮窗之类另外的顶层窗口绕过。
    /// 同一套做法见 <c>MainForm.PreFilterMessage</c>（窗口缩放）与 <c>ChatView</c>（滚轮）。
    /// </summary>
    public bool PreFilterMessage(ref Message m)
    {
        if (!Visible) return false;
        if (m.Msg != WM_LBUTTONDOWN && m.Msg != WM_RBUTTONDOWN) return false;
        if (IsMine(m.HWnd)) return false;      // 点在自己身上：照旧，光标挪到哪儿是哪儿
        Commit();
        // 不吃掉这一下：用户点的那个控件该收到它。这里只借一步「有人点到别处了」这个事实。
        return false;
    }

    /// <summary>这个 HWND 是输入条自己还是它的后代（内部那个 EDIT 就是后代）。</summary>
    private bool IsMine(IntPtr h)
    {
        for (var c = Control.FromHandle(h); c != null; c = c.Parent)
            if (ReferenceEquals(c, this)) return true;
        return false;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Application.AddMessageFilter(this);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Application.RemoveMessageFilter(this);   // 别让过滤器比控件活得久
        base.OnHandleDestroyed(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Arrange();
    }

    public void Restyle()
    {
        BackColor = Theme.PanelBg;
        _tb.BackColor = Theme.InputBg;
        _tb.ForeColor = Theme.TextMain;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rc = new Rectangle(0, 0, Width - 1, Height - 1);
        RP.Fill(g, rc, 8, Theme.InputBg);
        // 边框用强调色而不是 Border：这一刻它就是全窗口唯一能输入的地方，
        // 而这条细边框是「正在编辑」唯一说得出口的记号（标题条上没有别的位置放提示）。
        RP.Stroke(g, rc, 8, Theme.Accent, 1.4f);
        base.OnPaint(e);
    }
}
