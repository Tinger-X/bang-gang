using System.Drawing.Drawing2D;

namespace LiveAssistant;

/// <summary>
/// 屏幕角落的悬浮提示（右下角）。同样被排除出录屏/截屏，仅本机用户可见。
/// 可用于：录音中指示（可点击停止）、或短暂操作反馈（自动消失）。
/// </summary>
public class PillForm : Form
{
    private readonly System.Windows.Forms.Timer _autoClose;
    private Action? _onClick;
    private bool _excluded;

    public PillForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 32, 32);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _autoClose = new System.Windows.Forms.Timer();
        _autoClose.Tick += (_, _) => Hide();
        _ = Handle;
        TryExclude();
    }

    public void TryExclude() =>
        _excluded = Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE) || _excluded;

    /// <summary>显示提示。autoHideMs&lt;=0 表示常驻；点击内容触发 onClick（常驻时点击可执行如"停止录音"）。</summary>
    public void ShowPill(string text, int autoHideMs, Action? onClick)
    {
        _onClick = onClick;
        Controls.Clear();
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(14, 9, 14, 9),
            Font = new Font("Microsoft YaHei UI", 9.5f),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
        };
        label.MouseDown += Label_MouseDown;
        Controls.Add(label);
        // 文字下面放一个高亮底
        AutoSize = false;
        Size = label.PreferredSize;
        Paint += Pill_Paint;

        var wa = Screen.PrimaryScreen?.WorkingArea ?? Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 800);
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);

        _autoClose.Stop();
        if (autoHideMs > 0)
        {
            _autoClose.Interval = autoHideMs;
            _autoClose.Start();
        }
        Show();
        TryExclude();
    }

    private void Label_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _onClick != null)
        {
            _autoClose.Stop();
            Hide();
            _onClick();
        }
    }

    private void Pill_Paint(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 14);
        using var fill = new SolidBrush(Color.FromArgb(225, 28, 28, 28));
        e.Graphics.FillPath(fill, path);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
