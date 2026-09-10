using System.Runtime.InteropServices;

namespace BangGang;

/// <summary>
/// 设置浮窗背后的浅色遮罩：一个铺满主窗口客户区的分层窗口（SetLayeredWindowAttributes 统一透明度），
/// 在设置浮窗所在位置“挖洞”，因此：
///   · 主界面仍然可见，只是被轻微压暗；
///   · 遮罩挡住了鼠标，主界面不可操作；浮窗区域不受影响；
///   · 自己同样应用 WDA_EXCLUDEFROMCAPTURE，对录屏 / 截图完全不可见（不会露出一片暗色）。
/// </summary>
internal sealed class ScrimForm : Form
{
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint LWA_ALPHA = 0x00000002;

    private Rectangle _hole = Rectangle.Empty;   // 客户区坐标下的“洞”

    /// <summary>遮罩透明度（0-255），越小越淡。</summary>
    public byte Alpha { get; set; } = 42;

    /// <summary>点击遮罩（即浮窗以外的区域）时触发。</summary>
    public event Action? ScrimClicked;

    public ScrimForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Black;
        Cursor = Cursors.Default;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>显示时不抢占焦点（键盘仍然留给设置浮窗）。</summary>
    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyAlpha();
        if (!CaptureGuard.Disabled)
            Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>WinForms 在某些时机（显示 / 句柄重建）会重置分层属性，这里统一重设一次。</summary>
    private void ApplyAlpha()
    {
        if (IsHandleCreated) SetLayeredWindowAttributes(Handle, 0, Alpha, LWA_ALPHA);
    }

    /// <summary>把遮罩提到最上层（主窗口也是 TopMost，必须显式抬到它上面，且不抢焦点）。</summary>
    public void Raise()
    {
        if (!IsHandleCreated) return;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        ApplyAlpha();
    }

    /// <summary>铺满 owner 的客户区，并在 hole（屏幕坐标）处挖洞。</summary>
    public void FitTo(Form owner, Rectangle holeInScreen)
    {
        if (!owner.IsHandleCreated) return;
        var origin = owner.PointToScreen(Point.Empty);
        var size = owner.ClientSize;
        Bounds = new Rectangle(origin.X, origin.Y, Math.Max(1, size.Width), Math.Max(1, size.Height));
        _hole = holeInScreen.IsEmpty
            ? Rectangle.Empty
            : new Rectangle(holeInScreen.X - origin.X, holeInScreen.Y - origin.Y, holeInScreen.Width, holeInScreen.Height);
        ApplyRegion();
        Raise();
    }

    private void ApplyRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        using var r = new Region(new Rectangle(0, 0, Width, Height));
        if (!_hole.IsEmpty)
        {
            var hole = _hole;
            hole.Inflate(2, 2);
            r.Exclude(hole);
        }
        var old = Region;
        Region = r.Clone();
        old?.Dispose();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyRegion();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) ScrimClicked?.Invoke();
        base.OnMouseDown(e);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
}
