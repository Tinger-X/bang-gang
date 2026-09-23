using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>
/// 设置详情页基类：顶部标题区 + 可滚动内容区 + 底部操作条
/// （保存按钮仅在内容变化后可点击，可选的动作按钮排在保存按钮左侧）。
/// </summary>
internal abstract class SettingsPage : Panel, IThemed
{
    private const int PadX = 30;
    private const int HeaderH = 92;
    private const int FooterH = 58;

    public event Action<SettingsPage>? SaveRequested;
    public event Action? DirtyChanged;

    protected readonly StackPanel Stack = new();

    private readonly ScrollArea _body;

    /// <summary>页面的滚动区（设置浮窗的滚轮路由按光标位置直接喂它，见 SettingsOverlay.PreFilterMessage）。</summary>
    internal ScrollArea Body => _body;
    private readonly Label _title = new();
    private readonly Label _desc = new();
    private readonly Label _state = new();
    private readonly PillButton _save;
    private readonly System.Windows.Forms.Timer _stateTimer;
    private readonly List<(PillButton Btn, Func<bool>? ShowWhen)> _footerActions = new();
    /// <summary>是否存在未保存的修改。</summary>
    public bool IsDirty { get; private set; }

    protected SettingsPage(string title, string desc)
    {
        BackColor = SC.CardBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _title.Text = title;
        _title.AutoSize = false;
        _title.Font = Theme.UI(15.5f, FontStyle.Bold);
        _title.ForeColor = SC.Ink;
        _title.BackColor = SC.CardBg;
        _title.TextAlign = ContentAlignment.MiddleLeft;

        _desc.Text = desc;
        _desc.AutoSize = false;
        _desc.AutoEllipsis = true;
        _desc.Font = Theme.UI(9.5f);
        _desc.ForeColor = SC.InkMuted;
        _desc.BackColor = SC.CardBg;
        _desc.TextAlign = ContentAlignment.TopLeft;

        _body = new ScrollArea { BackColor = SC.CardBg };
        _body.SetContent(Stack);
        _body.Resize += (_, _) => LayoutStack();
        Stack.ControlAdded += (_, _) => LayoutStack();

        _state.AutoSize = false;
        _state.Font = Theme.UI(9.5f);
        _state.ForeColor = SC.InkMuted;
        _state.BackColor = SC.CardBg;
        _state.TextAlign = ContentAlignment.MiddleLeft;
        _state.Text = "已是最新";

        _save = new PillButton("保存", PillButton.Look.Primary, 108, 38) { On = false };
        _save.Click += (_, _) => SaveRequested?.Invoke(this);

        _stateTimer = new System.Windows.Forms.Timer { Interval = 1800 };
        _stateTimer.Tick += (_, _) => { _stateTimer.Stop(); UpdateSaveUi(); };

        Controls.Add(_title);
        Controls.Add(_desc);
        Controls.Add(_body);
        Controls.Add(_state);
        Controls.Add(_save);
    }

    /// <summary>
    /// 在保存按钮左侧添加一个次级动作按钮（例如「恢复默认」）。
    /// <paramref name="showWhen"/> 非空时，每次状态刷新都会重新求值，按钮仅在它为 true 时显示
    /// （「恢复默认」靠这个做到「只在设置项不为默认值时出现」）。
    /// </summary>
    protected PillButton AddFooterAction(string text, Action onClick, Func<bool>? showWhen = null)
    {
        var b = new PillButton(text, PillButton.Look.Ghost, 112, 38);
        b.Click += (_, _) => onClick();
        _footerActions.Add((b, showWhen));
        Controls.Add(b);
        return b;
    }

    // ---------- 子类实现 ----------

    /// <summary>用最新设置重新载入并重置“基线”，调用后该页视为无改动。</summary>
    public abstract void Rebind(AppSettings s);

    /// <summary>把本页内容写回设置对象（其它字段保持不变）。</summary>
    public abstract void ApplyTo(AppSettings target);

    /// <summary>与基线比较，返回是否有改动。</summary>
    protected abstract bool ComputeDirty();

    // ---------- 状态 ----------

    /// <summary>任一子控件内容变化后调用。</summary>
    protected void MarkChanged()
    {
        bool d = ComputeDirty();
        if (d != IsDirty)
        {
            IsDirty = d;
            DirtyChanged?.Invoke();
        }
        UpdateSaveUi();
    }

    /// <summary>由浮窗在保存成功后调用。</summary>
    public void MarkSaved()
    {
        IsDirty = false;
        DirtyChanged?.Invoke();
        _save.On = false;
        _state.Text = "已保存 ✓";
        _state.ForeColor = SC.Accent;
        _stateTimer.Stop();
        _stateTimer.Start();
    }

    /// <summary>重置为“无改动”（Rebind 之后调用）。</summary>
    protected void MarkClean()
    {
        IsDirty = false;
        UpdateSaveUi();
        DirtyChanged?.Invoke();
    }

    private void UpdateSaveUi()
    {
        if (IsDirty)
        {
            _save.On = true;
            _state.Text = "有未保存的修改";
            _state.ForeColor = SC.Accent;
        }
        else
        {
            _save.On = false;
            _state.Text = "已是最新";
            _state.ForeColor = SC.InkMuted;
        }
        _save.Invalidate();
        _state.Invalidate();

        // 条件显示的底栏按钮（「恢复默认」只在值偏离默认时出现）。
        foreach (var (b, pred) in _footerActions)
        {
            if (pred != null) b.Visible = pred();
        }
        if (Width > 0) LayoutFooter();
    }

    /// <summary>放弃未保存的改动：重新按基线载入并清除状态。</summary>
    public void Discard(AppSettings applied)
    {
        Rebind(applied);
        UpdateSaveUi();
    }

    public void Restyle()
    {
        BackColor = SC.CardBg;
        _title.BackColor = SC.CardBg; _title.ForeColor = SC.Ink;
        _desc.BackColor = SC.CardBg; _desc.ForeColor = SC.InkMuted;
        _body.BackColor = SC.CardBg;
        _state.BackColor = SC.CardBg;
        UpdateSaveUi();
        Stack.Restyle();
        Invalidate(true);
    }

    // ---------- 布局 ----------

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_body == null) return;
        int w = Width - PadX * 2;
        _title.SetBounds(PadX, 26, Math.Max(40, w - 46), 28);
        _desc.SetBounds(PadX, 56, Math.Max(40, w - 46), 20);
        _body.SetBounds(PadX, HeaderH, Math.Max(40, w), Math.Max(40, Height - HeaderH - FooterH));
        LayoutFooter();
        LayoutStack();
    }

    private void LayoutFooter()
    {
        int y = Height - FooterH + (FooterH - _save.Height) / 2;
        // 保存键收起来时不占位：不收的话「检查更新」会被推到离右缘 108+10 px 的地方，
        // 而那片空白本来是该由保存键填的，看着像少画了一个按钮。
        int right = Width - PadX - (_saveHidden ? 0 : _save.Width);
        if (!_saveHidden) _save.SetBounds(right, y, _save.Width, _save.Height);
        foreach (var (b, pred) in _footerActions)
        {
            // 不能读 b.Visible 判断显隐：Visible 的 getter 会沿父级链向上算，
            // 页面本身还没显示时读回来恒为 false，按钮于是被跳过、停在旧位置上。
            bool show = pred == null || pred();
            if (!show) continue;
            right -= b.Width + 10;
            b.SetBounds(right, y, b.Width, b.Height);
        }
        if (!_saveHidden)
            _state.SetBounds(PadX, Height - FooterH + (FooterH - 20) / 2, Math.Max(60, right - PadX - 16), 20);
    }

    private bool _saveHidden;

    /// <summary>
    /// 把底栏那颗「保存」连它左边的状态字一起收起来。
    ///
    /// 「软件说明」页用它 —— 那一页**没有任何设置项**（<see cref="ApplyTo"/> 是空的、
    /// <see cref="ComputeDirty"/> 恒假），摆一颗永远灰着的「保存」，用户会以为自己漏了什么没存；
    /// 旁边那行「已是最新」也会和页面里真正的更新状态混成两句话在说同一件事。
    /// 底栏于是只剩那枚「检查更新」。
    /// </summary>
    protected void HideSave()
    {
        _saveHidden = true;
        _save.Visible = false;
        _state.Visible = false;
        LayoutFooter();
    }

    private void LayoutStack()
    {
        if (_body is not ScrollArea area || Stack == null) return;
        Stack.ArrangeAndResize(Math.Max(40, area.ClientSize.Width - 18));
        area.Relayout(Math.Max(40, area.ClientSize.Width - 18), Stack.ContentHeight);
    }

    /// <summary>页面被切换显示时强制重排内容（隐藏期间的自动布局会被 WinForms 跳过）。</summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) LayoutStack();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(SC.Mix(SC.CardBg, Theme.Border, 0.9f));
        e.Graphics.DrawLine(pen, 0, Height - FooterH, Width, Height - FooterH);
    }

    /// <summary>重建内容：清空后由子类填充。</summary>
    protected void ResetContent()
    {
        Stack.Controls.Clear();
    }

    protected void FinishContent()
    {
        LayoutStack();
        UpdateSaveUi();
    }

    /// <summary>内容高度变化后重新排布（切换服务商导致行数变化时调用）。</summary>
    protected void RelayoutContent()
    {
        LayoutStack();
        UpdateSaveUi();
    }
}
