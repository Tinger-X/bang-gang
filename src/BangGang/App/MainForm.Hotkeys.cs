using System.Drawing.Drawing2D;

namespace BangGang;

partial class MainForm
{
    // ---------------- 全局快捷键 ----------------

    private void ReapplyHotkeys()
    {
        UnregisterHotkeys();
        for (int i = 0; i < _settings.Shortcuts.Count && i < 3; i++)
        {
            var sc = _settings.Shortcuts[i];
            if (!ShortcutSetting.IsUsable(sc.Vk, sc.Ctrl || sc.Alt || sc.Shift)) continue;
            int id = 0x201 + i;
            bool ok = Win32.RegisterHotKey(Handle, id, sc.Modifiers() | Win32.MOD_NOREPEAT, (uint)sc.Vk);
            if (!ok) _chrome.SetStatus($"热键 {sc.Label} 注册失败（可能被占用）");
        }
    }

    private void UnregisterHotkeys()
    {
        if (!IsHandleCreated) return;
        for (int i = 0; i < 3; i++) _ = Win32.UnregisterHotKey(Handle, 0x201 + i);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_HOTKEY)
        {
            int id = m.WParam.ToInt32() - 0x201;
            if (id >= 0 && id < _settings.Shortcuts.Count)
            {
                bool settingsOpen = _settingsOverlay.Visible;
                switch (_settings.Shortcuts[id].Action)
                {
                    // 显隐热键在设置打开时照样可用（开了设置想临时藏起窗口看桌面）；
                    // 截图 / 录音在设置打开时仍然禁用。
                    case "hide": ToggleVisible(); break;
                    case "shot": if (!settingsOpen) StartScreenshot(); break;
                    case "record": if (!settingsOpen) ToggleOrHoldRecording(); break;
                }
            }
            return;
        }

        base.WndProc(ref m);
    }

    private void ToggleVisible()
    {
        if (Visible) Hide();
        else { Show(); Activate(); }
    }

    /// <summary>设置浮窗 / 图片放大浮层打开时，Esc 在任何位置都能收起它（两者都非模态，焦点可能不在里面）。</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _viewer.IsOpen)
        {
            _viewer.Close();
            return true;
        }
        if (keyData == Keys.Escape && _settingsOverlay.Visible)
        {
            _settingsOverlay.CloseByEscape();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

}
