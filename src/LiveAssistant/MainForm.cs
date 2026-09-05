using System.Drawing.Drawing2D;

namespace LiveAssistant;

/// <summary>
/// 主窗口：半透明置顶，仅本机用户可见；对一切共享/录屏/截屏完全不可见
/// （SetWindowDisplayAffinity + WDA_EXCLUDEFROMCAPTURE，Windows 10 2004+）。
/// </summary>
public class MainForm : Form
{
    public const string WindowTitle = "直播助手";

    private const double UserOpacity = 0.75;          // 用户看到的半透明度
    private const uint Affinity = Native.WDA_EXCLUDEFROMCAPTURE;

    private readonly Label _title;
    private readonly Label _status;
    private readonly System.Windows.Forms.Timer _guardTimer;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _miShow;
    private readonly ToolStripMenuItem _miHideTray;

    public MainForm()
    {
        Text = WindowTitle;
        FormBorderStyle = FormBorderStyle.SizableToolWindow; // ToolWindow：不出现在任务栏和 Alt+Tab
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(360, 200);
        Opacity = UserOpacity;
        TopMost = true;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;

        _title = new Label
        {
            Text = WindowTitle,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 28f, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 120, 240),
        };

        _status = new Label
        {
            Text = "● 防录屏已开启 —— 共享/录屏中完全不可见",
            Dock = DockStyle.Bottom,
            Height = 32,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Color.FromArgb(60, 60, 60),
        };

        Controls.Add(_title);
        Controls.Add(_status);

        // 托盘图标：半透明绘制（explorer 渲染的托盘无法逐图标排除录屏，故用透明度淡显 + 可选隐藏）
        _tray = new NotifyIcon
        {
            Icon = MakeTranslucentIcon(),
            Text = WindowTitle,
            Visible = true,
        };
        var menu = new ContextMenuStrip();
        _miShow = new ToolStripMenuItem("显示 / 隐藏窗口", null, (_, _) => ToggleVisible());
        _miHideTray = new ToolStripMenuItem("隐藏托盘图标（录屏中不出现）", null, (_, _) => ToggleTray());
        menu.Items.Add(_miShow);
        menu.Items.Add(_miHideTray);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ToggleVisible();
        ContextMenuStrip = menu; // 右键主窗口也可用同一菜单

        // 窗口句柄重建（如修改样式）时重新应用防录屏
        HandleCreated += (_, _) => ApplyAffinity();

        // 守护定时器：系统或某些软件可能重置窗口属性，周期性校验并补设
        _guardTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _guardTimer.Tick += (_, _) => EnsureAffinity();
        _guardTimer.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW：彻底不出现在任务栏
            return cp;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyAffinity();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 点右上角 X = 隐藏到托盘，真正退出走菜单"退出"
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _guardTimer.Stop();
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    private void ApplyAffinity()
    {
        if (!Native.SetWindowDisplayAffinity(Handle, Affinity))
        {
            _status.Text = "● 防录屏设置失败（需 Windows 10 2004+）";
            _status.ForeColor = Color.Firebrick;
        }
    }

    private void EnsureAffinity()
    {
        if (Native.GetWindowDisplayAffinity(Handle, out uint current) && current != Affinity)
        {
            Native.SetWindowDisplayAffinity(Handle, Affinity);
        }
    }

    private void ToggleVisible()
    {
        if (Visible) Hide();
        else
        {
            Show();
            Activate();
        }
    }

    private void ToggleTray()
    {
        bool hideTray = !_miHideTray.Checked;
        _miHideTray.Checked = hideTray;
        _tray.Visible = !hideTray;
        // 托盘隐藏时窗口若也隐藏会失联，确保窗口可见
        if (hideTray && !Visible) ToggleVisible();
    }

    /// <summary>绘制半透明托盘图标（用户看到淡显效果）。</summary>
    private static Icon MakeTranslucentIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var back = new SolidBrush(Color.FromArgb(90, 40, 120, 240));
            using var pen = new Pen(Color.FromArgb(150, 40, 120, 240), 2f);
            using var text = new SolidBrush(Color.FromArgb(140, 40, 120, 240));
            g.FillEllipse(back, 2, 2, 28, 28);
            g.DrawEllipse(pen, 2, 2, 28, 28);
            using var font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            var size = g.MeasureString("直", font);
            g.DrawString("直", font, text, 16 - size.Width / 2, 16 - size.Height / 2);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
