using System.Drawing.Drawing2D;

namespace BangGang;

partial class SettingsOverlay
{
    // ---------------- 布局 ----------------

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutOverlay();
    }

    /// <summary>
    /// 浮窗固定尺寸、始终在主窗口内上下左右居中（不可拖动）。
    /// 浮窗铺满整个窗口：卡片以外画底层界面快照，并吃掉所有鼠标消息
    /// （设置打开时主界面只“看得见”，不能再被点击操作）。
    /// </summary>
    /// <summary>底图需要在整窗布局落定之后重抓，见 <see cref="LayoutOverlay"/>。</summary>
    private bool _backdropStale;

    private void LayoutOverlay()
    {
        if (Width <= 0 || Height <= 0) return;

        int cw = Math.Min(CardW, Math.Max(520, Width - CardMargin));
        int ch = Math.Min(CardH, Math.Max(380, Height - CardMargin));
        if (_card.Width != cw || _card.Height != ch) _card.Size = new Size(cw, ch);

        _cardPos = new Point((Width - cw) / 2, (Height - ch) / 2);
        _card.Location = _cardPos;

        // 只有在窗口尺寸真的变了（卡片尺寸变了）时才重新抓底图：
        // 打开设置时已经抓过一次，重复抓会白等一次整窗渲染，正是“打开时先闪一下”的原因之一。
        //
        // 底图是按整窗矩形铺上去的（见 OnPaintBackground），所以窗口本身被拖动缩放时
        // 也必须重抓，否则旧快照就对不上新窗口了。卡片尺寸和窗口尺寸任一变化都要重抓 ——
        // 窗口放大到卡片顶到 880×640 上限之后，就只有后者还在变。
        if (Visible && (_card.Size != _backdropFor || new Size(Width, Height) != _backdropWin))
            _backdropStale = true;
        // 这里**不能**同步抓图：本次布局还没走完。
        //
        // MainForm.ApplyLayout 先把浮窗改成新尺寸（这一下就回调到这里），之后才逐个摆放底图
        // 要画的那几个控件（欢迎页 / 标题条 / 输入区 / 消息区）。在这一刻抓，抓到的是**上一个
        // 尺寸**的主界面，而且此后不会再有第二次重抓 —— 用户看到的就一直是那张旧图，也就是
        // “设置界面打开时缩放窗口，界面不更新”。推迟到 WM_PAINT 是因为绘制消息排在队列里，
        // 一定在本次 WM_SIZE 处理完之后，那一刻布局必然已经落定。
        ApplyRegion();
        _card.Invalidate(true);             // 卡片是子窗口，父级重画不会带着它刷新
        Invalidate();
        CardBoundsChanged?.Invoke();
    }

    /// <summary>
    /// 浮窗覆盖整个窗口：Region 置空，卡片以外的鼠标消息落在浮窗上被忽略，
    /// 因此设置打开期间主界面不再可点（但外观仍与原界面一致，靠底层快照绘制）。
    /// 万一底图没抓到（DrawToBitmap 失败），退回到“只占卡片区域”的圆角 Region，
    /// 让主界面自己绘制，避免出现一片未绘制的黑区。
    ///
    /// 唯一的例外是主窗口自己的那几个顶栏按钮（<see cref="AppChromeHoles"/>：最大化 / 关闭）：
    /// 那几小块被从 Region 里挖掉，鼠标消息于是直接落到下面那些**真按钮**上 —— 设置打开期间
    /// 也能一键最大化 / 退出，不必先关掉设置。挖掉的那块改由底层界面自己绘制（快照在同一个
    /// 位置被让了出来），所以既不会重影，也不会留陈旧像素。
    /// </summary>
    private void ApplyRegion()
    {
        Region? region;
        if (_backdrop == null)
        {
            var rect = new Rectangle(_cardPos.X - 1, _cardPos.Y - 1,
                                     Math.Max(1, _card.Width) + 2, Math.Max(1, _card.Height) + 2);
            using var path = RP.Path(rect, _card.Radius + 1);
            region = new Region(path);
        }
        else
        {
            region = new Region(new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)));

            // 卡片压到按钮上时（窗口极小）不能挖洞，否则会啃掉卡片的一角
            var cardRect = new Rectangle(_cardPos.X - 1, _cardPos.Y - 1,
                                         Math.Max(1, _card.Width) + 2, Math.Max(1, _card.Height) + 2);
            foreach (var hole in _appChromeHoles)
            {
                if (hole.Width <= 0 || hole.Height <= 0) continue;
                if (hole.IntersectsWith(cardRect)) continue;
                region.Exclude(hole);
                RepaintUnder(hole);
            }
        }

        var old = Region;
        Region = region;
        old?.Dispose();
    }

    private Rectangle[] _appChromeHoles = Array.Empty<Rectangle>();

    /// <summary>
    /// 主窗口顶栏上「设置打开期间也要能点」的那些按钮（最大化 / 关闭）在浮窗坐标系里的矩形
    /// （浮窗铺满整个客户区，两者同一坐标系），由主窗口在布局时注入。见 <see cref="ApplyRegion"/>。
    /// </summary>
    public Rectangle[] AppChromeHoles
    {
        get => _appChromeHoles;
        set
        {
            _appChromeHoles = value ?? Array.Empty<Rectangle>();
            if (IsHandleCreated) ApplyRegion();
        }
    }


    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && e.Y < DragStripHeight) TopDragRequested?.Invoke();
    }

    /// <summary>卡片位置或尺寸变化（窗口缩放）时通知主窗口。</summary>
    public event Action? CardBoundsChanged;

    private void LayoutCard()
    {
        int w = _card.Width, h = _card.Height;
        if (w <= 0 || h <= 0) return;

        // 子控件从 3px 处开始：给浮窗自己的 1px 边框留出位置，否则边框会被盖住
        const int inset = 3;
        _rail.SetBounds(inset, inset, RailW - 1, h - inset * 2);
        _divider.SetBounds(RailW + 1, inset, 1, h - inset * 2);
        _host.SetBounds(RailW + 2, inset, Math.Max(10, w - RailW - inset - 2), h - inset * 2);
        _close.SetBounds(w - 44, 12, 28, 28);     // 尽量贴近右上角

        _brandMark.SetBounds(18, 24, 28, 28);
        _railTitle.SetBounds(54, 24, RailW - 70, 28);

        int y = 96;
        foreach (var n in _navs)
        {
            n.SetBounds(14, y, RailW - 28, 42);
            y += 48;
        }
        // 底部版本信息整行居中
        _version.SetBounds(14, Math.Max(y + 10, h - 38), RailW - 28, 20);
        _version.TextAlign = ContentAlignment.MiddleCenter;

        foreach (var p in _pages) p.SetBounds(0, 0, _host.ClientSize.Width, _host.ClientSize.Height);

        // 确认浮层：400x180 居中；不画阴影，边框直接贴在外缘（不留投影边距）
        const int cw2 = 400, ch2 = 180;
        _confirm.Shadow = 0;   // 不画阴影，仅保留边框
        _confirm.SetBounds((w - cw2) / 2, (h - ch2) / 2, cw2, ch2);
        _confirmTitle.SetBounds(24, 30, cw2 - 48, 26);
        _confirmDesc.SetBounds(24, 62, cw2 - 48, 20);
        int ix = (cw2 - 232) / 2;   // 两个按钮整体居中
        _confirmStay.SetBounds(ix, 118, 104, 34);
        _confirmQuit.SetBounds(ix + 104 + 12, 118, 116, 34);
    }

}
