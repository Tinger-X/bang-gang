using System.Drawing.Drawing2D;

namespace BangGang;

partial class MainForm
{
    /// <summary>热键触发录音：按住模式需检测按键是否仍按下；按下模式只切换开始/停止。</summary>
    private void ToggleOrHoldRecording()
    {
        if (_settings.RecordMode == "toggle")
        {
            if (_recorder?.IsRecording == true) StopRecording();
            else StartRecording();
            return;
        }

        var sc = _settings.Shortcuts.FirstOrDefault(s => s.Action == "record");
        if (sc == null || !ComboDown(sc)) return;
        StartRecording();
    }

    private void StartRecording()
    {
        if (_recorder?.IsRecording == true) return;
        var sc = _settings.Shortcuts.FirstOrDefault(s => s.Action == "record");
        if (sc == null) return;

        try
        {
            string path = Path.Combine(GetRecordingsDir(), $"录音_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
            var rec = new AudioMixRecorder();
            rec.Start(path);
            _recorder = rec;
            string tail = _settings.RecordMode == "toggle" ? "再按一次结束" : "松开结束";
            _chrome.SetStatus((rec.SystemOnlyMic ? "● 录音中（仅系统声音）…" : "● 正在录音（系统+麦克风）…") + tail);
            if (_settings.RecordMode != "toggle") _pttTimer.Start();
        }
        catch (Exception ex)
        {
            _chrome.SetStatus("✗ 无法开始录音：" + ex.Message);
        }
    }

    private void PttTick()
    {
        var sc = _settings.Shortcuts.FirstOrDefault(s => s.Action == "record");
        if (sc != null && ComboDown(sc)) return;
        _pttTimer.Stop();
        StopRecording();
    }

    private static bool ComboDown(ShortcutSetting sc)
    {
        return (Win32.GetAsyncKeyState(sc.Vk) & Win32.KEY_DOWN) != 0
            && (!sc.Ctrl || (Win32.GetAsyncKeyState(0x11) & Win32.KEY_DOWN) != 0)
            && (!sc.Alt || (Win32.GetAsyncKeyState(0x12) & Win32.KEY_DOWN) != 0)
            && (!sc.Shift || (Win32.GetAsyncKeyState(0x10) & Win32.KEY_DOWN) != 0);
    }

    private void StopRecording()
    {
        var rec = _recorder;
        if (rec == null) return;
        _recorder = null;
        try
        {
            rec.Stop();
            EnsureActive();
            _input.AddFile(rec.SavePath);
            string note = rec.SystemOnlyMic ? "（仅系统声音）" : "";
            _chrome.SetStatus("✓ 录音已保存并加入输入框 " + note);
        }
        catch (Exception ex)
        {
            _chrome.SetStatus("✗ 保存录音出错：" + ex.Message);
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
            catch { }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "recordings");
    }
}
