using System.Drawing.Drawing2D;

namespace BangGang;

partial class SettingsOverlay
{
    /// <summary>
    /// 抓一张主界面快照：卡片四角是圆角，圆角以外的像素必须显示“真正的底层界面”，
    /// 用快照填充就能得到抗锯齿的圆角（Region 硬裁剪会留下锯齿）。
    ///
    /// 两个坑：
    /// 1) 不能用屏幕抓图 —— 应用本身对截屏不可见（WDA_EXCLUDEFROMCAPTURE），
    ///    屏幕抓图在 Release 下只会拿到桌面；
    /// 2) 不能直接对主窗口 DrawToBitmap，也不能临时隐藏卡片 —— 前者对带 Region 的
    ///    顶层窗口会得到黑图，后者会在浮窗刚可见时把卡片留在“未绘制”状态（整块变黑）。
    /// 因此这里逐个把主窗口里除浮窗以外的兄弟控件画进位图再拼起来。
    /// </summary>
    private void CaptureBackdrop()
    {
        var parent = Parent;
        if (parent == null || Width <= 0 || Height <= 0) return;
        try
        {
            var bmp = new Bitmap(Width, Height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(parent.BackColor);
                // Controls[0] 在最上层，所以从后往前拼
                for (int i = parent.Controls.Count - 1; i >= 0; i--)
                {
                    var c = parent.Controls[i];
                    if (ReferenceEquals(c, this) || !c.Visible || c.Width <= 0 || c.Height <= 0) continue;
                    using var cb = new Bitmap(c.Width, c.Height);
                    c.DrawToBitmap(cb, new Rectangle(0, 0, c.Width, c.Height));
                    // DrawToBitmap 会忽略控件的 Region（例如只在窗口最外圈画描边的 WindowFrame），
                    // 因此这里按 Region 裁一下，否则整张快照会被那圈颜色盖住。
                    var old = g.Clip;
                    if (c.Region != null)
                    {
                        using var rr = c.Region.Clone();
                        rr.Translate(c.Left, c.Top);
                        g.SetClip(rr, CombineMode.Intersect);
                    }
                    g.DrawImage(cb, c.Left, c.Top);
                    g.Clip = old;
                }
            }
            _backdrop?.Dispose();
            _backdrop = bmp;
            _backdropFor = _card.Size;
            _backdropWin = new Size(Width, Height);
        }
        catch
        {
            _backdrop?.Dispose();
            _backdrop = null;
        }
        _card.BackdropBitmap = _backdrop;
        _card.BackdropOffset = _cardPos;
    }

    private Bitmap? _backdrop;
    private Size _backdropFor;
    private Size _backdropWin;

    /// <summary>
    /// 重新抓一次底层界面快照。主窗口状态栏这类内容在浮窗打开期间被快照盖住，
    /// 保存设置后状态栏会变成“设置已保存”，此时刷新一次底图，用户就能看到提示。
    /// </summary>
    public void RefreshBackdrop()
    {
        if (!Visible) return;
        CaptureBackdrop();
        _backdropStale = false;     // 刚抓过，别让待办的那次再抓一遍
        Invalidate();
    }


    /// <summary>挖洞后让下面的兄弟控件把那一块重画一遍，别留下浮窗的旧像素。</summary>
    private void RepaintUnder(Rectangle hole)
    {
        if (!Visible) return;
        Parent?.Invalidate(hole, true);
    }

    /// <summary>顶部条（ChromeBar 区域）按下时请求主窗口拖动：设置打开期间仍可拖窗。</summary>
    public event Action? TopDragRequested;

    /// <summary>可拖动顶条的高度（= 主窗口 ChromeBar 高度），由主窗口注入。</summary>
    public int DragStripHeight { get; set; } = 38;


    /// <summary>
    /// 浮窗本体只画卡片；卡片以外的区域直接用底层界面快照铺上，
    /// 这样不会留下“陈旧像素”。圆角不做 Region 裁剪（会裁出锯齿），而是靠快照本身
    /// 就是抗锯齿画出来的；Region 只用来整块覆盖 / 挖掉关闭按钮，见 <see cref="ApplyRegion"/>。
    /// </summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 底图的（重）抓放在这里，而不是缩放回调里 —— 只有到绘制这一刻，整窗布局才一定落定。
        // 见 LayoutOverlay 里的注释。
        if (_backdropStale)
        {
            _backdropStale = false;
            CaptureBackdrop();
        }

        var rc = new Rectangle(0, 0, Width, Height);
        var snap = _backdrop;
        if (snap != null)
        {
            e.Graphics.DrawImage(snap, rc, rc, GraphicsUnit.Pixel);
            return;
        }
        // 底图没抓到也必须画点东西：浮窗覆盖整窗，什么都不画就会露
        // “从未绘制”的黑色区域。
        using var b = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(b, rc);
    }


    /// <summary>抓一张设置卡片快照给确认浮层当圆角底图（不含确认条自己）。</summary>
    private void CaptureConfirmBackdrop()
    {
        if (_card.Width <= 0 || _card.Height <= 0) return;
        bool shown = _confirm.Visible;
        _confirm.Visible = false;
        try
        {
            var bmp = new Bitmap(_card.Width, _card.Height);
            _card.DrawToBitmap(bmp, new Rectangle(0, 0, _card.Width, _card.Height));
            _confirmBackdrop?.Dispose();
            _confirmBackdrop = bmp;
        }
        catch
        {
            _confirmBackdrop?.Dispose();
            _confirmBackdrop = null;
        }
        finally
        {
            _confirm.Visible = shown;
        }
        _confirm.BackdropBitmap = _confirmBackdrop;
        _confirm.BackdropOffset = _confirm.Location;
    }

    private Bitmap? _confirmBackdrop;

    private void HideConfirm()
    {
        if (_confirm.Visible) _confirm.Visible = false;
    }

}
