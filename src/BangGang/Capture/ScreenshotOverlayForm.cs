using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>
/// 全屏选区遮罩。本身也应用 WDA_EXCLUDEFROMCAPTURE：
/// 用户拖动选框的过程同样不会被任何录屏/截屏软件录到（录屏中看不到这个遮罩层）。
/// Esc 取消；鼠标拖出矩形后返回屏幕物理坐标区域。
/// </summary>
public class ScreenshotOverlayForm : Form
{
    private Point _start;
    private Point _cur;
    private bool _dragging;
    private bool _excluded;

    /// <summary>用户选定的屏幕区域（物理像素，主屏左上为原点）。取消时为 Empty。</summary>
    public Rectangle SelectedRectangle { get; private set; }

    public ScreenshotOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;      // 覆盖全部显示器
        BackColor = Color.Black;
        Opacity = 0.30;                                // 半暗背景，突出选区
        DoubleBuffered = true;
        KeyPreview = true;
        _ = Handle;                                     // 立即创建句柄以便设置防录屏
        TryExclude();
        Ui.EnforceArrowCursor(this);                    // 选区遮罩也保持箭头指针
    }

    private void TryExclude()
    {
        if (!_excluded)
        {
            _excluded = Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        TryExclude();
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            SelectedRectangle = Rectangle.Empty;
            DialogResult = DialogResult.Cancel;
            Close();
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _start = e.Location;
            _cur = e.Location;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            _cur = e.Location;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging && e.Button == MouseButtons.Left)
        {
            _dragging = false;
            var r = RectFromPoints(_start, e.Location);
            // 过小的拖动视为取消（可能只是误点）
            if (r.Width >= 3 && r.Height >= 3)
            {
                // 换算为屏幕物理坐标
                Point tl = PointToScreen(r.Location);
                SelectedRectangle = new Rectangle(tl, r.Size);
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            SelectedRectangle = Rectangle.Empty;
            DialogResult = DialogResult.Cancel;
            Close();
        }
        base.OnMouseUp(e);
    }

    private static Rectangle RectFromPoints(Point a, Point b) =>
        Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                           Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (_dragging)
        {
            var r = RectFromPoints(_start, _cur);
            r.Inflate(1, 1);
            using var pen = new Pen(Color.FromArgb(255, 0, 200, 255), 2f);
            using var light = new Pen(Color.White, 1f) { DashStyle = DashStyle.Dash };
            e.Graphics.DrawRectangle(pen, r);
            e.Graphics.DrawRectangle(light, Rectangle.Inflate(r, -2, -2));
        }
        base.OnPaint(e);
    }
}
