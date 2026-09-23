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
                    string path = SaveShot(img);
                    EnsureActive();
                    try { Clipboard.SetImage(img); } catch { }
                    _input.Add(new Attachment { Kind = "image", Name = $"截图_{DateTime.Now:HHmmss}.png", Path = path });
                    _chrome.SetStatus("✓ 截图已加入输入框");
                }
            }
        }
        finally { _overlayActive = false; }
    }

    /// <summary>
    /// 把截图落盘成附件。走 <see cref="AttachmentStore.Store"/>（内容寻址），于是：
    /// 同一张图截两次只占一份文件，且删会话时能按哈希数引用、没人用了才回收。
    ///
    /// 目录仍是 <see cref="ChatStore.ImageDir"/> 而不是 <c>%TEMP%</c>：这张截图会作为附件
    /// 留在一条消息里，而 %TEMP% 的文件随时可能被系统或清理工具删掉 ——
    /// 重启后历史还在、图却没了。
    /// </summary>
    private static string SaveShot(Image img) => AttachmentStore.Store(img);

}
