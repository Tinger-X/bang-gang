using System.Drawing.Drawing2D;

namespace BangGang;

internal sealed class KeyCapBox : Control, IThemed
{
    private bool _focus;
    private ShortcutSetting _sc = new();
    private readonly ShortcutSetting _pending = new();   // 录入中显示的组合
    private readonly ShortcutSetting _commit = new();    // 全部松开后真正提交的组合
    private readonly HashSet<Keys> _down = new();
    private bool _usable;

    public event Action? Changed;

    public KeyCapBox(int width = 240)
    {
        Size = new Size(width, 34);
        BackColor = SC.GroupBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
    }

    public ShortcutSetting Value => new() { Action = _sc.Action, Ctrl = _sc.Ctrl, Alt = _sc.Alt, Shift = _sc.Shift, Vk = _sc.Vk };

    public void Set(ShortcutSetting s)
    {
        _sc = new ShortcutSetting { Action = s.Action, Ctrl = s.Ctrl, Alt = s.Alt, Shift = s.Shift, Vk = s.Vk };
        Invalidate();
    }

    public void Restyle() { BackColor = SC.GroupBg; Invalidate(); }

    private void BeginCapture()
    {
        _focus = true;
        _down.Clear();
        _usable = false;
        _pending.Action = _sc.Action;
        _pending.Ctrl = _pending.Alt = _pending.Shift = false;
        _pending.Vk = 0;
        _commit.Ctrl = _commit.Alt = _commit.Shift = false;
        _commit.Vk = 0;
        Invalidate();
    }

    private void EndCapture(bool commit)
    {
        _focus = false;
        _down.Clear();
        Invalidate();
    }

    /// <summary>当前按住的修饰键 → 录入中显示的组合；提交值来自按下可用键的那一刻。</summary>
    private void RefreshLive()
    {
        _pending.Ctrl = _down.Contains(Keys.ControlKey);
        _pending.Alt = _down.Contains(Keys.Menu);
        _pending.Shift = _down.Contains(Keys.ShiftKey);
        _pending.Vk = _usable ? _commit.Vk : 0;
    }

    protected override void OnEnter(EventArgs e) { if (!_focus) BeginCapture(); base.OnEnter(e); }

    protected override void OnLeave(EventArgs e)
    {
        // 录入途中失去焦点：放弃本次录入，沿用原来的组合键
        if (_focus) EndCapture(false);
        base.OnLeave(e);
    }

    /// <summary>点击即进入录入状态（录入完成后仍保持焦点，再次点击可重新录入）。</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        BeginCapture();
        Focus();
        base.OnMouseDown(e);
    }

    protected override bool IsInputKey(Keys keyData) => true;

    /// <summary>录入中的 Esc 只取消本次录入，不会关闭设置浮窗。</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _focus)
        {
            EndCapture(false);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_focus) return;
        if (e.KeyCode == Keys.Tab) return;      // 交给系统做焦点切换

        _down.Add(e.KeyCode);

        bool mod = e.Control || e.Alt || e.Shift;
        if (ShortcutSetting.IsUsable((int)e.KeyCode, mod))
        {
            // 记下这一刻的修饰键：松开全部按键后用它提交
            _commit.Ctrl = _down.Contains(Keys.ControlKey);
            _commit.Alt = _down.Contains(Keys.Menu);
            _commit.Shift = _down.Contains(Keys.ShiftKey);
            _commit.Vk = (int)e.KeyCode;
            _usable = true;
        }
        RefreshLive();
        e.SuppressKeyPress = true;
        Invalidate();                            // 实时显示按下的键
        Trace.Log($"key down {e.KeyCode} down={_down.Count} live={Parts(_pending)}");
    }

    private static string Parts(ShortcutSetting s) =>
        $"{(s.Ctrl ? "Ctrl+" : "")}{(s.Alt ? "Alt+" : "")}{(s.Shift ? "Shift+" : "")}{(s.Vk != 0 ? ShortcutSetting.VkToName(s.Vk) : "-")}";

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (!_focus) return;
        _down.Remove(e.KeyCode);
        RefreshLive();
        e.SuppressKeyPress = true;

        // 所有按键都抬起 -> 录入完成
        if (_down.Count == 0 && _usable && _commit.Vk != 0)
        {
            _sc.Ctrl = _commit.Ctrl;
            _sc.Alt = _commit.Alt;
            _sc.Shift = _commit.Shift;
            _sc.Vk = _commit.Vk;
            EndCapture(true);
            Changed?.Invoke();
            Trace.Log($"key commit {Parts(_sc)}");
            return;
        }
        Trace.Log($"key up {e.KeyCode} down={_down.Count} live={Parts(_pending)}");
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rc = new Rectangle(0, 0, Width - 1, Height - 1);
        RP.Fill(g, rc, 9, SC.FieldBg);
        RP.Stroke(g, rc, 9, _focus ? SC.Accent : SC.FieldBorder, _focus ? 1.4f : 1f);

        // 录入中：实时显示当前按下的组合键；一个键都没按就显示提示
        var shown = _focus ? _pending : _sc;
        bool hasAny = _focus && (shown.Ctrl || shown.Alt || shown.Shift || shown.Vk != 0);
        if (_focus && !hasAny)
        {
            TextRenderer.DrawText(g, "请按下新的组合键…", SF.Get(10f), rc, SC.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            base.OnPaint(e);
            return;
        }

        var parts = new List<string>();
        if (shown.Ctrl) parts.Add("Ctrl");
        if (shown.Alt) parts.Add("Alt");
        if (shown.Shift) parts.Add("Shift");
        if (shown.Vk != 0) parts.Add(ShortcutSetting.VkToName(shown.Vk));

        // 量算每个键帽的宽度（缓存字体只读，切勿 Dispose）
        var keyFont = SF.Get(10f, FontStyle.Bold);
        var sizes = new List<float>();
        float total = 0;
        foreach (var p in parts) { float w = g.MeasureString(p, keyFont).Width; sizes.Add(w); total += (int)Math.Round(w) + 16; }
        total += (parts.Count - 1) * 12;

        int x = (int)Math.Round((Width - total) / 2f);   // 键帽整体居中
        int y = (Height - 24) / 2;
        using (var kb = new SolidBrush(SC.Mix(SC.FieldBg, Theme.Border, 0.8f)))
            for (int i = 0; i < parts.Count; i++)
            {
                int cw = (int)Math.Round(sizes[i]) + 16;
                using (var p = RP.Path(new Rectangle(x, y, cw, 24), 6)) g.FillPath(kb, p);
                TextRenderer.DrawText(g, parts[i], SF.Get(10f, FontStyle.Bold), new Rectangle(x, y, cw, 24), SC.Ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                x += cw;
                if (i < parts.Count - 1)
                {
                    TextRenderer.DrawText(g, "+", SF.Get(9.5f), new Rectangle(x, y, 12, 24), SC.InkFaint,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    x += 12;
                }
            }
        base.OnPaint(e);
    }
}

/// <summary>不透明度滑杆：左侧轨道 + 右侧百分比。</summary>
