using System.Drawing.Drawing2D;

namespace BangGang;

partial class MainForm
{
    private void ApplyAffinity()
    {
        if (CaptureGuard.Disabled) return;   // 本地界面调试：允许被截图
        if (!Native.SetWindowDisplayAffinity(Handle, Affinity))
            _chrome.SetStatus("防录屏设置失败（需 Win10 2004+）");
    }

    private void EnsureAffinity()
    {
        if (CaptureGuard.Disabled) return;
        if (Native.GetWindowDisplayAffinity(Handle, out uint cur) && cur != Affinity)
            Native.SetWindowDisplayAffinity(Handle, Affinity);
    }


    // ---------------- Alt+截图 / 录音（联动输入框） ----------------

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
                    string path = SaveTempPng(img);
                    EnsureActive();
                    try { Clipboard.SetImage(img); } catch { }
                    _input.Add(new Attachment { Kind = "image", Name = $"截图_{DateTime.Now:HHmmss}.png", Path = path });
                    _chrome.SetStatus("✓ 截图已加入输入框");
                }
            }
        }
        finally { _overlayActive = false; }
    }

    private static string SaveTempPng(Image img)
    {
        // 落盘目录从 %TEMP% 挪到了 <see cref="ChatStore.ImageDir"/>：这张截图会作为附件
        // 留在一条消息里，而 %TEMP% 的文件随时可能被清掉 —— 重启后历史还在、图没了。
        string p = ChatStore.NewImagePath("shot");
        img.Save(p, System.Drawing.Imaging.ImageFormat.Png);
        return p;
    }

}
