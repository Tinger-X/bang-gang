using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace LiveAssistant;

/// <summary>
/// 主窗口：无边框自绘 UI + 自定义关闭按钮。半透明置顶，仅本机用户可见，
/// 对一切共享 / 录屏 / 截屏完全不可见（SetWindowDisplayAffinity）。
///
/// 全局快捷键（RegisterHotKey，非独占）：
///   Alt+X  显示 / 隐藏窗口
///   Alt+C  选区截屏 → 剪贴板（选区遮罩同样防录屏）
///   Alt+V  按住录音（系统声音 + 麦克风，WASAPI 共享模式，不干扰直播/会议），松开保存
/// </summary>
public class MainForm : Form
{
    public const string WindowTitle = "直播助手";

    private const double UserOpacity = 0.82;
    private const uint Affinity = Native.WDA_EXCLUDEFROMCAPTURE;

    private const int HK_SHOW = 0x101; // Alt+X
    private const int HK_SHOT = 0x102; // Alt+C
    private const int HK_REC = 0x103;  // Alt+V

    // 自定义关闭按钮区域（右上角，相对窗口）
    private static readonly Rectangle CloseRect = new(0, 0, 30, 30); // 位置在 OnResizeLayout 里计算

    private bool _closeHover;
    private bool _closeDown;

    private readonly System.Windows.Forms.Timer _guardTimer;
    private readonly System.Windows.Forms.Timer _pttTimer;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private bool _overlayActive;
    private AudioMixRecorder? _recorder;

    private string _statusText = "";
    private Color _statusColor = Color.FromArgb(60, 110, 170);

    private readonly Color _titleColor = Color.FromArgb(43, 108, 214);
    private readonly Color _textColor = Color.FromArgb(70, 75, 82);
    private readonly Color _mutedColor = Color.FromArgb(130, 138, 150);

    public MainForm()
    {
        Text = WindowTitle;
        FormBorderStyle = FormBorderStyle.None;   // 无系统边框 → 自定义关闭按钮
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(800, 500);
        ShowInTaskbar = false;
        TopMost = true;
        Opacity = UserOpacity;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(244, 248, 253);
        ApplyRoundRegion();

        _guardTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _guardTimer.Tick += (_, _) => EnsureAffinity();
        _guardTimer.Start();

        // 按住录音的松键监视
        _pttTimer = new System.Windows.Forms.Timer { Interval = 80 };
        _pttTimer.Tick += (_, _) => PttTick();

        // 状态文字自动复原
        _statusTimer = new System.Windows.Forms.Timer { Interval = 2600 };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); SetDefaultStatus(); };

        SetDefaultStatus();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW：不进任务栏 / Alt+Tab
            return cp;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyAffinity();
        RegisterHotkeys();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _guardTimer.Stop();
        _pttTimer.Stop();
        _statusTimer.Stop();
        UnregisterHotkeys();
        _recorder?.Stop();
        base.OnFormClosed(e);
    }

    // ---------------- 防录屏 ----------------

    private void ApplyAffinity()
    {
        bool ok = Native.SetWindowDisplayAffinity(Handle, Affinity);
        SetStatus(ok ? "● 防录屏已开启 — 界面仅自己可见" : "✗ 防录屏设置失败（需 Windows 10 2004+）",
                  ok ? _titleColor : Color.Firebrick, -1);
    }

    private void EnsureAffinity()
    {
        if (Native.GetWindowDisplayAffinity(Handle, out uint cur) && cur != Affinity)
            Native.SetWindowDisplayAffinity(Handle, Affinity);
    }

    // ---------------- 状态文案 ----------------

    private void SetDefaultStatus() =>
        SetStatus("● 防录屏已开启 — 界面仅自己可见", _titleColor, -1);

    /// <summary>更新底部状态文字。revertMs&gt;0 表示随后自动复原默认状态。</summary>
    private void SetStatus(string text, Color color, int revertMs)
    {
        _statusText = text;
        _statusColor = color;
        if (revertMs > 0)
        {
            _statusTimer.Stop();
            _statusTimer.Interval = revertMs;
            _statusTimer.Start();
        }
        Invalidate();
    }

    // ---------------- 无边框窗口拖动 ----------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (ActualCloseRect().Contains(e.Location))
            {
                _closeDown = true;
                Invalidate();
                return; // 交给 MouseUp 决定关闭
            }
            // 其余区域拖动窗口
            Win32.ReleaseCapture();
            _ = Win32.SendMessage(Handle, Win32.WM_NCLBUTTONDOWN, (IntPtr)Win32.HTCAPTION, IntPtr.Zero);
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _closeDown)
        {
            _closeDown = false;
            if (ActualCloseRect().Contains(e.Location)) Close();
        }
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool over = ActualCloseRect().Contains(e.Location);
        if (over != _closeHover)
        {
            _closeHover = over;
            Cursor = over ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_closeHover || _closeDown)
        {
            _closeHover = false;
            _closeDown = false;
            Cursor = Cursors.Default;
            Invalidate();
        }
        base.OnMouseLeave(e);
    }

    private Rectangle ActualCloseRect()
    {
        int right = Width - 8;   // 更贴近右上角
        int top = 8;
        return new Rectangle(right - CloseRect.Width, top, CloseRect.Width, CloseRect.Height);
    }

    // ---------------- 全局热键 ----------------

    private void RegisterHotkeys()
    {
        RegisterOne(HK_SHOW, Win32.VK_X);
        RegisterOne(HK_SHOT, Win32.VK_C);
        RegisterOne(HK_REC, Win32.VK_V);
    }

    private void RegisterOne(int id, int vk)
    {
        bool ok = Win32.RegisterHotKey(Handle, id, Win32.MOD_ALT | Win32.MOD_NOREPEAT, (uint)vk);
        if (!ok)
        {
            string which = id == HK_SHOW ? "Alt+X" : id == HK_SHOT ? "Alt+C" : "Alt+V";
            SetStatus($"⚠ 热键 {which} 注册失败（可能已被占用）", Color.Firebrick, -1);
        }
    }

    private void UnregisterHotkeys()
    {
        if (IsHandleCreated)
        {
            _ = Win32.UnregisterHotKey(Handle, HK_SHOW);
            _ = Win32.UnregisterHotKey(Handle, HK_SHOT);
            _ = Win32.UnregisterHotKey(Handle, HK_REC);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_HOTKEY)
        {
            switch (m.WParam.ToInt32())
            {
                case HK_SHOW: ToggleVisible(); break;
                case HK_SHOT: StartScreenshot(); break;
                case HK_REC: BeginPttRecording(); break;
            }
            return;
        }
        base.WndProc(ref m);
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

    // ---------------- Alt+C 选区截屏 ----------------

    private void StartScreenshot()
    {
        if (_overlayActive) return;
        _overlayActive = true;
        try
        {
            using var overlay = new ScreenshotOverlayForm();
            if (overlay.ShowDialog() == DialogResult.OK)
            {
                using var img = ScreenGrab.CaptureRegion(overlay.SelectedRectangle);
                if (img != null)
                {
                    try
                    {
                        Clipboard.SetImage(img);
                        SetStatus("✓ 截图已复制到剪贴板", Color.FromArgb(50, 150, 80), 2200);
                    }
                    catch
                    {
                        SetStatus("✗ 复制到剪贴板失败", Color.Firebrick, 3000);
                    }
                }
                else
                {
                    SetStatus("选区无效，未截屏", _mutedColor, 2000);
                }
            }
        }
        finally
        {
            _overlayActive = false;
        }
    }

    // ---------------- Alt+V 按住录音 ----------------

    private void BeginPttRecording()
    {
        if (_recorder?.IsRecording == true) return;
        // 若消息处理时按键已抬起（极短点按），不启动
        if ((Win32.GetAsyncKeyState(Win32.VK_V) & Win32.KEY_DOWN) == 0) return;

        try
        {
            string dir = GetRecordingsDir();
            string path = Path.Combine(dir, $"录音_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
            var rec = new AudioMixRecorder();
            rec.Start(path);
            _recorder = rec;
            SetStatus(rec.SystemOnlyMic
                          ? "● 录音中（仅系统声音）… 松开 Alt+V 保存"
                          : "● 正在录音（系统 + 麦克风）… 松开 Alt+V 保存",
                      Color.Crimson, -1);
            _pttTimer.Start();
        }
        catch (Exception ex)
        {
            SetStatus("✗ 无法开始录音：" + ex.Message, Color.Firebrick, 5000);
        }
    }

    private void PttTick()
    {
        bool vDown = (Win32.GetAsyncKeyState(Win32.VK_V) & Win32.KEY_DOWN) != 0;
        bool altDown = (Win32.GetAsyncKeyState(Win32.VK_MENU) & Win32.KEY_DOWN) != 0;
        if (vDown && altDown) return; // 仍按住 → 继续录

        _pttTimer.Stop();
        StopRecording();
    }

    private void StopRecording()
    {
        var rec = _recorder;
        if (rec == null) return;
        _recorder = null;
        try
        {
            rec.Stop();
            string note = rec.SystemOnlyMic ? "（仅系统声音）" : "";
            string msg = "✓ 已保存 " + Path.GetFileName(rec.SavePath) + " " + note;
            if (rec.MicNote != null) msg = "✓ 已保存 " + Path.GetFileName(rec.SavePath) + " · " + rec.MicNote;
            SetStatus(msg, Color.FromArgb(50, 150, 80), 4000);
        }
        catch (Exception ex)
        {
            SetStatus("✗ 保存录音出错：" + ex.Message, Color.Firebrick, 5000);
        }
    }

    private static string GetRecordingsDir()
    {
        foreach (string baseDir in new[] { AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
        {
            try
            {
                string d = Path.Combine(baseDir, "recordings");
                Directory.CreateDirectory(d);
                return d;
            }
            catch { /* 尝试下一个位置 */ }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "recordings");
    }

    // ---------------- 自绘 UI ----------------

    private void ApplyRoundRegion()
    {
        const int r = 8;   // 圆角减小
        using var path = new GraphicsPath();
        path.AddArc(0, 0, r * 2, r * 2, 180, 90);
        path.AddArc(Width - r * 2, 0, r * 2, r * 2, 270, 90);
        path.AddArc(Width - r * 2, Height - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(0, Height - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        // 背景渐变 + 细描边
        using (var bg = new LinearGradientBrush(ClientRectangle, Color.FromArgb(250, 252, 255), Color.FromArgb(235, 243, 251), LinearGradientMode.Vertical))
            g.FillRectangle(bg, ClientRectangle);
        using (var pen = new Pen(Color.FromArgb(70, 203, 223, 244), 1f))
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        int cx = Width / 2;

        // 顶部：标题（主视觉）
        using (var titleFont = new Font("Microsoft YaHei UI", 42f, FontStyle.Bold))
        using (var titleBrush = new SolidBrush(_titleColor))
        {
            var sz = g.MeasureString(WindowTitle, titleFont);
            g.DrawString(WindowTitle, titleFont, titleBrush, cx - sz.Width / 2, 78);
        }

        // 分隔线
        using (var line = new Pen(Color.FromArgb(60, 208, 224, 240), 1f))
            g.DrawLine(line, cx - 260, 196, cx + 260, 196);

        // 状态行
        using (var stFont = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold))
        {
            var sz = g.MeasureString(_statusText, stFont);
            g.DrawString(_statusText, stFont, new SolidBrush(_statusColor), cx - sz.Width / 2, 226);
        }

        // 快捷键帮助（居中成块）
        const int topY = 288;
        const int lh = 27;
        using (var small = new Font("Microsoft YaHei UI", 11f))
        using (var key = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold))
        using (var keyBrush = new SolidBrush(_titleColor))
        using (var textBrush = new SolidBrush(_textColor))
        {
            DrawKeyLine(g, small, key, keyBrush, textBrush, cx, topY + 0, "Alt+X", "显示 / 隐藏窗口");
            DrawKeyLine(g, small, key, keyBrush, textBrush, cx, topY + lh, "Alt+C", "选区截屏 → 已自动复制到剪贴板");
            DrawKeyLine(g, small, key, keyBrush, textBrush, cx, topY + lh * 2, "Alt+V", "按住录音（系统 + 麦克风），松开即保存");
            DrawKeyLine(g, small, key, keyBrush, textBrush, cx, topY + lh * 3, "备注", "录制内容不干扰直播 / 会议；一切录屏截不到本窗");
        }

        // 右上角自定义关闭按钮
        DrawCloseButton(g);

        base.OnPaint(e);
    }

    private void DrawKeyLine(Graphics g, Font small, Font key, Brush keyBrush, Brush textBrush,
        int cx, int y, string k, string desc)
    {
        var ksz = g.MeasureString(k, key);
        var wsz = g.MeasureString(desc, small);
        float gap = 16;
        float left = cx - (ksz.Width + gap + wsz.Width) / 2;
        g.DrawString(k, key, keyBrush, left, y);
        g.DrawString(desc, small, textBrush, left + ksz.Width + gap, y + 1);
    }

    private void DrawCloseButton(Graphics g)
    {
        var r = ActualCloseRect();
        if (_closeHover || _closeDown)
        {
            using var bg = new SolidBrush(_closeDown ? Color.FromArgb(200, 214, 60, 54) : Color.FromArgb(150, 230, 74, 66));
            g.FillEllipse(bg, r);
        }
        using (var pen = new Pen(_closeHover ? Color.White : Color.FromArgb(140, 150, 160), _closeHover ? 2.2f : 1.8f))
        {
            float m = 9.5f;
            g.DrawLine(pen, r.X + m, r.Y + m, r.Right - m, r.Bottom - m);
            g.DrawLine(pen, r.Right - m, r.Y + m, r.X + m, r.Bottom - m);
        }
    }
}
