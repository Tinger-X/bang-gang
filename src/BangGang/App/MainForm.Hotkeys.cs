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
                    case "hide": if (!settingsOpen) ToggleVisible(); break;
                    case "shot": if (!settingsOpen) StartScreenshot(); break;
                    case "record": if (!settingsOpen) ToggleOrHoldRecording(); break;
                }
            }
            return;
        }

        // 拖动期间临时摘掉透明（0.9.5）：Opacity < 1 会把窗口变成分层窗口
        // （WS_EX_LAYERED + LWA_ALPHA），DWM 对它走慢速合成路径，开销随窗口面积涨 ——
        // 大窗口拖着发顿的主因就是它。HTCAPTION 模态拖动由系统发出这对消息：进入时
        // 暂时回到不透明（松手后恢复），整个拖动过程就在快速路径上。
        // 边缘缩放不发这对消息（走自己的 PreFilter 循环），所以「是移动还是缩放」
        // 不用在这里分辨。
        if (m.Msg == Win32.WM_ENTERSIZEMOVE)
        {
            if (Opacity < 1.0)
            {
                _dragSavedOpacity = Opacity;
                Opacity = 1.0;
            }
        }
        else if (m.Msg == Win32.WM_EXITSIZEMOVE)
        {
            if (_dragSavedOpacity.HasValue)
            {
                Opacity = _dragSavedOpacity.Value;
                _dragSavedOpacity = null;
            }
        }

        base.WndProc(ref m);
    }

    /// <summary>拖动期间被临时摘掉的透明度；null = 不在「临时不透明」状态。</summary>
    private double? _dragSavedOpacity;

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
