using System.Drawing.Drawing2D;

namespace LiveAssistant;

/// <summary>
/// 主窗口：半透明置顶，仅本机用户可见；对一切共享/录屏/截屏完全不可见。
/// 全局热键（RegisterHotKey，非独占）：
///   Alt+O  显示/隐藏界面与托盘图标
///   Alt+C  选区截屏 → 剪贴板（选区遮罩同样防录屏）
///   Alt+V  开始/停止录音（WASAPI 共享模式：系统声音 + 麦克风，不干扰直播/会议）
/// </summary>
public class MainForm : Form
{
    public const string WindowTitle = "直播助手";

    private const double UserOpacity = 0.75;
    private const uint Affinity = Native.WDA_EXCLUDEFROMCAPTURE;

    // 全局热键 id（高位 0x10 起，避免与 1..0x0FFF 保留冲突）
    private const int HK_SHOW = 0x101;
    private const int HK_SHOT = 0x102;
    private const int HK_REC = 0x103;

    private readonly Label _status;
    private readonly System.Windows.Forms.Timer _guardTimer;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _miShow;
    private readonly ToolStripMenuItem _miRecord;
    private readonly PillForm _pill;
    private bool _overlayActive;
    private AudioMixRecorder? _recorder;
    private bool _reallyQuit;

    public MainForm()
    {
        Text = WindowTitle;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(800, 500);
        MinimumSize = new Size(420, 260);
        Opacity = UserOpacity;
        TopMost = true;
        ShowInTaskbar = false;

        var title = new Label
        {
            Text = "直播助手",
            Dock = DockStyle.Top,
            Height = 170,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 40f, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 120, 240),
        };

        var help = new Label
        {
            Dock = DockStyle.Fill,
            Text = "本窗口仅你自己可见 —— 任何共享 / 录屏 / 截屏软件中都看不到它。\n\n"
                 + "全局快捷键（在任意程序中生效）：\n"
                 + "    Alt + O    显示 / 隐藏界面与托盘图标\n"
                 + "    Alt + C    选区截屏（拖出区域 → 已自动复制到剪贴板）\n"
                 + "    Alt + V    开始 / 停止录音（系统声音 + 麦克风，不影响正在进行的直播 / 会议）\n\n"
                 + "录音时右下角会出现可点击的悬浮提示，点击即可停止。\n"
                 + "关闭窗口 = 隐藏到托盘；退出请用托盘菜单。",
            Font = new Font("Microsoft YaHei UI", 11f),
            ForeColor = Color.FromArgb(55, 55, 55),
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(34, 8, 34, 8),
        };

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 30,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Color.FromArgb(70, 70, 70),
            Text = "● 防录屏已开启",
        };

        Controls.Add(help);
        Controls.Add(title);
        Controls.Add(_status);

        // ---- 托盘 ----
        _tray = new NotifyIcon { Icon = MakeTranslucentIcon(), Text = WindowTitle, Visible = true };
        var menu = new ContextMenuStrip();
        _miShow = new ToolStripMenuItem("显示/隐藏界面与托盘 (Alt+O)", null, (_, _) => ToggleUiAndTray());
        var miShot = new ToolStripMenuItem("选区截屏 (Alt+C)", null, (_, _) => StartScreenshot());
        _miRecord = new ToolStripMenuItem("开始录音 (Alt+V)", null, (_, _) => ToggleRecord());
        menu.Items.Add(_miShow);
        menu.Items.Add(miShot);
        menu.Items.Add(_miRecord);
        menu.Items.Add(new ToolStripSeparator());
        var miQuit = new ToolStripMenuItem("退出", null, (_, _) => Quit());
        menu.Items.Add(miQuit);
        _tray.ContextMenuStrip = menu;
        ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ToggleUiAndTray();

        // ---- 悬浮提示（同样防录屏）----
        _pill = new PillForm();

        // ---- 句柄就绪后：防录屏 + 注册热键 ----
        HandleCreated += (_, _) => ApplyAffinity();

        // ---- 守护：防录屏属性若被重置则补设 ----
        _guardTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _guardTimer.Tick += (_, _) => EnsureAffinity();
        _guardTimer.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyAffinity();
        RegisterHotkeys();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyQuit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;  // 关窗口 = 隐藏到托盘
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _guardTimer.Stop();
        UnregisterHotkeys();
        _recorder?.Stop();
        _pill.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosed(e);
    }

    private void Quit() { _reallyQuit = true; Close(); }

    // ---------------- 防录屏 ----------------

    private void ApplyAffinity()
    {
        bool ok = Native.SetWindowDisplayAffinity(Handle, Affinity);
        _status.Text = ok ? "● 防录屏已开启" : "● 防录屏设置失败（需 Windows 10 2004+）";
        if (!ok) _status.ForeColor = Color.Firebrick;
    }

    private void EnsureAffinity()
    {
        if (Native.GetWindowDisplayAffinity(Handle, out uint cur) && cur != Affinity)
            Native.SetWindowDisplayAffinity(Handle, Affinity);
        if (_pill.Visible) _pill.TryExclude();
    }

    // ---------------- 全局热键 ----------------

    private void RegisterHotkeys()
    {
        RegisterOne(HK_SHOW, Win32.VK_O);
        RegisterOne(HK_SHOT, Win32.VK_C);
        RegisterOne(HK_REC, Win32.VK_V);
    }

    private void RegisterOne(int id, int vk)
    {
        bool ok = Win32.RegisterHotKey(Handle, id, Win32.MOD_ALT | Win32.MOD_NOREPEAT, (uint)vk);
        if (!ok)
        {
            string which = id == HK_SHOW ? "Alt+O" : id == HK_SHOT ? "Alt+C" : "Alt+V";
            _status.Text = $"⚠ 热键 {which} 注册失败（可能已被其它程序占用）";
            _status.ForeColor = Color.Firebrick;
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
            int id = m.WParam.ToInt32();
            if (id == HK_SHOW) ToggleUiAndTray();
            else if (id == HK_SHOT) StartScreenshot();
            else if (id == HK_REC) ToggleRecord();
            return;
        }
        base.WndProc(ref m);
    }

    // ---------------- Alt+O：显示/隐藏 ----------------

    private void ToggleUiAndTray()
    {
        bool allVisible = Visible && _tray.Visible;
        if (allVisible)
        {
            Hide();
            _tray.Visible = false;
            _miShow.Text = "显示界面与托盘 (Alt+O)";
        }
        else
        {
            Show();
            _tray.Visible = true;
            Activate();
            _miShow.Text = "隐藏界面与托盘 (Alt+O)";
        }
        _tray.Text = WindowTitle;
    }

    // ---------------- Alt+C：选区截屏 ----------------

    private void StartScreenshot()
    {
        if (_overlayActive) return;
        _overlayActive = true;
        bool pillWasVisible = _pill.Visible;
        try
        {
            if (pillWasVisible) _pill.Hide();
            using var overlay = new ScreenshotOverlayForm();
            if (overlay.ShowDialog() == DialogResult.OK)
            {
                using var img = ScreenGrab.CaptureRegion(overlay.SelectedRectangle);
                if (img != null)
                {
                    try
                    {
                        Clipboard.SetImage(img);
                        ShowPill("✓ 截图已复制到剪贴板", 2500, null);
                    }
                    catch
                    {
                        ShowPill("✗ 复制到剪贴板失败", 3500, null);
                    }
                }
                else
                {
                    ShowPill("选区无效", 2000, null);
                }
            }
        }
        finally
        {
            _overlayActive = false;
            if (pillWasVisible) _pill.Show();
        }
    }

    // ---------------- Alt+V：录音 ----------------

    private void ToggleRecord()
    {
        if (_recorder != null && _recorder.IsRecording) StopRecording();
        else StartRecording();
    }

    private void StartRecording()
    {
        if (_recorder != null) return;
        string dir = GetRecordingsDir();
        string path = Path.Combine(dir, $"录音_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
        try
        {
            var rec = new AudioMixRecorder();
            rec.Start(path);
            _recorder = rec;
            _miRecord.Text = "停止录音 (Alt+V)";
            string tip = rec.SystemOnlyMic
                ? "● 录音中（仅系统声音，麦克风被占用）—— 点击停止"
                : "● 正在录音（系统 + 麦克风）—— 点击停止";
            ShowPill(tip, 0, StopRecording);
            _status.Text = "● 正在录音…";
            _status.ForeColor = Color.Crimson;
        }
        catch (Exception ex)
        {
            ShowPill("✗ 无法开始录音：" + ex.Message, 5000, null);
            _status.Text = "✗ 录音启动失败";
            _status.ForeColor = Color.Firebrick;
        }
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
            string msg = "✓ 已保存：" + Path.GetFileName(rec.SavePath) + " " + note;
            if (rec.MicNote != null) msg += "\n" + rec.MicNote;
            ShowPill(msg, 6000, null);
            _status.Text = "● 录音已保存：" + Path.GetFileName(rec.SavePath);
            _status.ForeColor = Color.FromArgb(70, 70, 70);
        }
        catch (Exception ex)
        {
            ShowPill("✗ 保存录音出错：" + ex.Message, 6000, null);
            _status.Text = "✗ 保存录音出错";
            _status.ForeColor = Color.Firebrick;
        }
        finally
        {
            _miRecord.Text = "开始录音 (Alt+V)";
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

    // ---------------- 悬浮提示 ----------------

    private void ShowPill(string text, int ms, Action? onClick) => _pill.ShowPill(text, ms, onClick);

    /// <summary>绘制半透明托盘图标。</summary>
    private static Icon MakeTranslucentIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var back = new SolidBrush(Color.FromArgb(90, 40, 120, 240));
            using var pen = new Pen(Color.FromArgb(150, 40, 120, 240), 2f);
            using var text = new SolidBrush(Color.FromArgb(150, 40, 120, 240));
            g.FillEllipse(back, 2, 2, 28, 28);
            g.DrawEllipse(pen, 2, 2, 28, 28);
            using var font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            var sz = g.MeasureString("直", font);
            g.DrawString("直", font, text, 16 - sz.Width / 2, 16 - sz.Height / 2);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
