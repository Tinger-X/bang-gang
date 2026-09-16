using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>小型图标按钮（放大镜 / 齿轮 / 加号 / 发送 / 关闭 / 回形针等），不改变鼠标指针。</summary>
internal sealed class IconButton : Control, IThemed
{
    public enum Kind { Search, Gear, Plus, Send, Stop, Record, Close, Paperclip, Maximize, Restore, Collapse, Expand }

    /// <summary>
    /// 底衬的三种画法。
    ///
    /// <c>Chrome</c> 是顶栏 / 侧栏用的老样式：常驻一个浅色圆底，图标压在上面。
    /// 输入区底部工具行换成了参考产品的样子，那里两个按钮的底衬语义完全不同，于是拆出另外两种：
    /// <c>Ghost</c> 平时**不画**底衬（只有悬浮时才浮出一个圆），<c>Solid</c> 则永远是一枚实心圆。
    /// </summary>
    public enum Look { Chrome, Ghost, Solid }

    public Kind Icon { get; set; }
    public Look Skin { get; set; } = Look.Chrome;

    /// <summary>
    /// <see cref="Look.Solid"/> 圆底的「点亮」状态：真 = 主题强调色 + 白图标，假 = 中性灰 + 白图标。
    /// 发送按钮用它表达「现在能不能发」。另外两种底衬不看这个。
    /// </summary>
    public bool Active { get; set; } = true;

    private readonly Color? _backdropHint;
    private bool _hover;
    private bool _clickable = true;

    /// <summary>
    /// 按钮当前是否可点（发送键的「不可点击」状态）。
    ///
    /// 用自定义标志而不是 <see cref="Control.Enabled"/>：后者给窗口挂上 <c>WS_DISABLED</c>，
    /// 鼠标消息整个绕开按钮落到父面板上，<c>WindowFromPoint</c> 于是命中面板而不是按钮 ——
    /// tools/settings-over-chat.ps1 的 A 段（「底行每个控件在自己的中心点上都得被点中」）
    /// 会因此报假失败，而那条探针守的是真问题（按钮被 Fill 的兄弟控件盖住）。
    /// 这里只关掉两件事：悬浮高亮，以及点下去有没有反应。
    /// </summary>
    public bool Clickable
    {
        get => _clickable;
        set
        {
            if (_clickable == value) return;
            _clickable = value;
            if (!value) _hover = false;   // 变灰那一刻鼠标可能正悬在上面，留着就是一枚假的悬浮圈
            Invalidate();
        }
    }

    /// <summary>
    /// 这个按钮实际盖在谁身上 —— 取底色时用它，而不是 <c>Parent</c>。
    ///
    /// 圆钮之外的四个角是**不透明**的 <c>BackColor</c>，所以「父面板什么色」直接决定四角是否无痕。
    /// 绝大多数按钮的父面板就是它盖住的那块面，两者一致；但对话顶栏那个「收起 / 展开」按钮是
    /// <c>_chatUI</c> 的孩子，画的却落在**兄弟控件** <c>_convTitle</c>（标题条）上 ——
    /// 于是 <c>Restyle()</c> 拿 <c>Parent.BackColor</c> 会取到 ChatBg，四角在标题条上留下一圈
    /// 异色的方块。用户看到的正是这个：「切换主题后非圆角部分没跟着变」。
    /// </summary>
    public Control? BackdropOwner { get; set; }

    /// <summary>
    /// <see cref="BackdropOwner"/> 的补充：底色不是「某个控件」，而是**画出来的一块面**时用它。
    ///
    /// 输入区底部那一行按钮就压在 <c>InputPanel</c> 自己画的那张圆角卡片上，卡片的填充色
    /// （<c>Theme.InputBg</c>）不等于面板的 <c>BackColor</c>（<c>Theme.ChatBg</c>）——
    /// 没有哪个控件的 <c>BackColor</c> 等于卡片色，所以指不出 <see cref="BackdropOwner"/>。
    ///
    /// 取的是委托而不是颜色值：颜色值只在赋值那一刻求值，主题切换后会留下一圈旧色的方角，
    /// 那正是 <see cref="BackdropOwner"/> 要解决、却在这里解决不了的问题。
    /// </summary>
    public Func<Color>? BackdropSource { get; set; }

    public IconButton(Kind kind, Color? backdrop = null)
    {
        Icon = kind;
        _backdropHint = backdrop;
        Size = new Size(28, 28);
        BackColor = backdrop ?? Theme.SideBg;   // 不透明：与所在面板同色即可无痕
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
    }

    /// <summary>
    /// 主题切换后重新贴合所在面板的底色。
    /// 必须取“当前”面板底色：构造时传进来的颜色属于旧主题，继续沿用就会在按钮四周留一圈旧色。
    /// 盖在兄弟控件上的按钮要用 <see cref="BackdropOwner"/> 指出来，盖在自绘面上的用
    /// <see cref="BackdropSource"/>，见那两处的注释。
    /// </summary>
    public void Restyle()
    {
        BackColor = BackdropSource?.Invoke()
                 ?? BackdropOwner?.BackColor
                 ?? Parent?.BackColor
                 ?? _backdropHint
                 ?? Theme.SideBg;
        Invalidate();
    }

    /// <summary>
    /// 挂上父控件时立刻贴合一次底色。
    ///
    /// <see cref="Restyle"/> 原来只在换主题那条路上被调用（<c>Ui.RestyleTree</c> 由
    /// <c>MainForm.ApplyThemeUi</c> 驱动），而 <c>ApplyThemeUi</c> **启动时根本不走** ——
    /// 它只挂在「设置已保存」和「跟随系统亮暗翻转」上。于是每个按钮在第一次换主题之前，
    /// 四角一直是构造函数里的兜底色 <c>Theme.SideBg</c>：盖在卡片上的发送 / 回形针在
    /// 浅色主题下露出 246,248,251 的方角（卡片是 252,253,255），用户看到的就是
    /// 「图标底下有个比卡片暗一点的方块」。
    ///
    /// 钩在这里而不是让每个调用方自己记得调 <c>Restyle()</c>：<c>Controls.Add</c> 触发
    /// <c>OnParentChanged</c>，而对象初始化器（<c>BackdropSource</c> / <c>BackdropOwner</c>
    /// 就是在那儿赋的）**在那之前**已经跑完，所以这一刻读到的一定是最终的底色来源。
    /// </summary>
    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        Restyle();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        if (_clickable) { _hover = true; Invalidate(); }
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover) { _hover = false; Invalidate(); }
        base.OnMouseLeave(e);
    }

    /// <summary>不可点时连 <c>Click</c> 都不冒出去，调用方不必在一个个分支里自己判。</summary>
    protected override void OnClick(EventArgs e)
    {
        if (!_clickable) return;
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color ink;
        if (Skin == Look.Solid)
        {
            // 实心圆：点亮时是强调色，否则中性灰。图标一律白 —— 两种底都压得住。
            using (var sb = new SolidBrush(Active
                       ? Theme.Accent
                       : Theme.Mix(BackColor, Theme.TextMuted, _hover ? 0.55f : 0.42f)))
                g.FillEllipse(sb, 0, 0, Width, Height);
            ink = Color.White;
        }
        else
        {
            // 常驻浅底圆钮（Chrome），或只在悬浮时才浮出来的圆底（Ghost）。
            // 底色必须取**自己**的 BackColor：这枚圆盖在卡片上，取 Theme.SideBg 会在卡片上
            // 留一个异色圆斑 —— 和 BackdropSource 那段注释是同一个坑。
            if (Skin == Look.Chrome || _hover)
            {
                using var bg = new SolidBrush(Theme.Mix(BackColor, Theme.TextMuted, _hover ? 0.20f : 0.11f));
                g.FillEllipse(bg, 1, 1, Width - 2, Width - 2);
            }
            ink = _hover ? Theme.Accent : Theme.Mix(Theme.TextMuted, Theme.TextMain, 0.35f);
        }

        using var pen = new Pen(ink, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float c = Width / 2f;
        switch (Icon)
        {
            case Kind.Search:
                g.DrawEllipse(pen, c - 5, c - 5, 9, 9);
                g.DrawLine(pen, c + 3, c + 3, c + 7, c + 7);
                break;
            case Kind.Gear:
                DrawGear(g, c, c, 6f, ink);
                break;
            case Kind.Plus:
                g.DrawLine(pen, c, c - 6, c, c + 6);
                g.DrawLine(pen, c - 6, c, c + 6, c);
                break;
            case Kind.Send:
                DrawArrowUp(g, c, c, 5.6f, pen);
                break;
            case Kind.Stop:
                // 暂停生成：一枚圆角方块（各处助手通用的「停止」形状）。
                // 用填充而不是描边 —— 28px 下描边方块中间那块空腔和旁边那圈实心圆一比就显得脏。
                using (var sq = RP.Path(new Rectangle((int)c - 4, (int)c - 4, 8, 8), 2))
                using (var sb = new SolidBrush(ink))
                    g.FillPath(sb, sq);
                break;
            case Kind.Record:
                using (var rb = new SolidBrush(Color.Crimson)) g.FillEllipse(rb, c - 4, c - 4, 8, 8);
                break;
            case Kind.Close:
                using (var xb = new SolidBrush(_hover
                           ? Theme.Mix(BackColor, Theme.Danger, 0.85f)
                           : Theme.Mix(BackColor, Theme.TextMuted, 0.11f)))
                    g.FillEllipse(xb, 1, 1, Width - 2, Width - 2);
                using (var xp = new Pen(_hover ? Color.White : ink, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(xp, c - 4, c - 4, c + 4, c + 4);
                    g.DrawLine(xp, c + 4, c - 4, c - 4, c + 4);
                }
                break;
            case Kind.Maximize:
                g.DrawRectangle(pen, c - 5, c - 5, 10, 10);
                break;
            case Kind.Restore:
                // 两个错开的方框，后面那个只画露在外面的三条边
                g.DrawRectangle(pen, c - 5, c - 3, 8, 8);
                g.DrawLine(pen, c - 3, c - 3, c - 3, c - 5);
                g.DrawLine(pen, c - 3, c - 5, c + 5, c - 5);
                g.DrawLine(pen, c + 5, c - 5, c + 5, c + 3);
                break;
            case Kind.Collapse:
            case Kind.Expand:
                // 侧栏那条竖边 + 一个尖角：尖角指左 = 收起，指右 = 展开
                g.DrawLine(pen, c - 5, c - 6, c - 5, c + 6);
                float tip = Icon == Kind.Collapse ? c - 1 : c + 3;
                float back = Icon == Kind.Collapse ? c + 3 : c - 1;
                g.DrawLine(pen, back, c - 4, tip, c);
                g.DrawLine(pen, tip, c, back, c + 4);
                break;
            case Kind.Paperclip:
                DrawPaperclip(g, c, c, 20f, ink);
                break;
        }
        base.OnPaint(e);
    }

    /// <summary>
    /// 发送：一支竖直的上箭头（一条杆 + 两撇箭头）。
    ///
    /// 旧版画的是三笔拼的「纸飞机」，那三笔首尾不接，缩到 28px 就是一团看不出是什么的乱码 ——
    /// 用户报的「图标像乱码」正是它和下面那个回形针。上箭头在任何尺寸下都只有一个读法。
    /// </summary>
    private static void DrawArrowUp(Graphics g, float cx, float cy, float s, Pen pen)
    {
        g.DrawLine(pen, cx, cy + s, cx, cy - s);                         // 杆
        g.DrawLine(pen, cx - s * 0.72f, cy - s * 0.26f, cx, cy - s);     // 左撇
        g.DrawLine(pen, cx + s * 0.72f, cy - s * 0.26f, cx, cy - s);     // 右撇
    }

    /// <summary>
    /// 回形针：一根线、三个 180° 回头弯、两条长腿。
    ///
    /// 在**旋转 45° 的坐标系**里画 —— 那样每个回头弯都正好是半个圆（一行 <c>DrawArc</c>），
    /// 屏幕坐标系里则要自己算贝塞尔控制点。两条形状上的硬要求：腿长必须**明显大于**弯的直径，
    /// 且两个上弯要互相**嵌套**（内弯整个落在外弯里面）；少一条读出来就是锯齿而不是回形针。
    ///
    /// 笔宽要**除以**缩放系数：<c>ScaleTransform</c> 是连笔宽一起缩的。
    /// 设计稿与调参过程见 tools/glyph-preview.ps1。
    /// </summary>
    private static void DrawPaperclip(Graphics g, float cx, float cy, float box, Color ink)
    {
        const float rOut = 5.0f;    // 外侧上弯
        const float rBot = 3.5f;    // 底部回头弯
        const float rIn = 2.5f;     // 内侧上弯，必须整个落在外侧上弯里面
        const float yTopO = -4.2f;  // 外侧上弯的圆心
        const float yBot = 4.2f;    // 底部回头弯的圆心

        float xA = 0f;                       // 外左腿（自由端在下）
        float xB = xA + 2 * rOut;            // 外右腿
        float xC = xB - 2 * rBot;            // 内左腿
        float xD = xC + 2 * rIn;             // 内右腿（自由端在下）
        float yTopI = yTopO + rOut - rIn;    // 内上弯的圆心：贴着外上弯的内侧

        float k = box / 2f / 10.6f;          // 局部单位 -> 像素；10.6 是留出四角余量的外接半径
        var old = g.Save();
        g.TranslateTransform(cx, cy);
        g.RotateTransform(45);
        g.ScaleTransform(k, k);
        using (var pen = new Pen(ink, 1.8f / k)
               { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            g.DrawLine(pen, xA, 9f, xA, yTopO);
            g.DrawArc(pen, xA, yTopO - rOut, 2 * rOut, 2 * rOut, 180, 180);
            g.DrawLine(pen, xB, yTopO, xB, yBot);
            g.DrawArc(pen, xC, yBot, 2 * rBot, 2 * rBot, 0, 180);
            g.DrawLine(pen, xC, yBot, xC, yTopI);
            g.DrawArc(pen, xC, yTopI - rIn, 2 * rIn, 2 * rIn, 180, 180);
            g.DrawLine(pen, xD, yTopI, xD, 5f);
        }
        g.Restore(old);
    }

    private void DrawGear(Graphics g, float cx, float cy, float r, Color ink)
    {
        // 设置：三条水平调节滑杆（比齿轮更清晰，避免被误认作太阳）
        using var pen = new Pen(ink, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float[] xs = { -3f, 3f, -3f }; // 旋钮水平偏移
        for (int i = 0; i < 3; i++)
        {
            float y = cy + (i - 1) * 5;
            g.DrawLine(pen, cx - 7, y, cx + 7, y);
            using var kb = new SolidBrush(ink);
            g.FillEllipse(kb, cx + xs[i] - 2.5f, y - 2.5f, 5, 5);
            using var kw = new SolidBrush(_hover
                ? Theme.Mix(BackColor, Theme.TextMuted, 0.20f)
                : Theme.Mix(BackColor, Theme.TextMuted, 0.11f));
            g.FillEllipse(kw, cx + xs[i] - 1.1f, y - 1.1f, 2.2f, 2.2f);
        }
    }
}
