using System.Drawing.Drawing2D;

namespace BangGang;

partial class MainForm
{
    private LiveDictation? _dictation;

    // 转写文字 = 用户自己敲的前缀 + 已定稿 + 中间结果，每次刷新整框重写（见 InputPanel.SetDictation）
    private string _dictPrefix = "";
    private string _dictFinal = "";
    private string _dictPartial = "";

    /// <summary>热键触发录音：按住模式需检测按键是否仍按下；按下模式只切换开始/停止。</summary>
    private void ToggleOrHoldRecording()
    {
        if (_settings.RecordMode == "toggle")
        {
            if (_dictation != null) StopRecording();
            else StartRecording();
            return;
        }

        var sc = _settings.Shortcuts.FirstOrDefault(s => s.Action == "record");
        if (sc == null || !ComboDown(sc)) return;
        StartRecording();
    }

    private async void StartRecording()
    {
        if (_dictation != null) return;
        var sc = _settings.Shortcuts.FirstOrDefault(s => s.Action == "record");
        if (sc == null) return;
        Trace.Log("record: hotkey accepted");

        // 没配好转写接口就不录音 —— 录音的全部意义就是转写，录下来没处去只会误导用户
        var cfg = SttConfig.From(_settings);
        if (cfg.Problem is { } problem)
        {
            Trace.Log("record: gate -> " + problem);
            _chrome.SetStatus(problem);
            return;
        }

        LiveDictation? d = null;
        try
        {
            d = new LiveDictation(cfg.CreateSession());
            WireDictationEvents(d);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await d.StartAsync(cts.Token);

            // 真的开始录了才建会话：「不在对话中就即刻新建」以录音开始为准，
            // 接口没连上时不该留下一条空会话。
            EnsureActive();
            _dictation = d;
            _dictPrefix = _input.Text;
            _dictFinal = "";
            _dictPartial = "";

            string tail = _settings.RecordMode == "toggle" ? "再按一次结束" : "松开结束";
            _chrome.SetStatus((d.SystemOnlyMic ? "● 正在转写（仅系统声音）…" : "● 正在录音转写…") + tail);
            if (_settings.RecordMode != "toggle") _pttTimer.Start();
        }
        catch (Exception ex)
        {
            Trace.Log($"record: start failed {ex.GetType().Name}: {ex.Message}");
            d?.Dispose();
            _chrome.SetStatus("✗ 无法开始转写：" + ex.Message);
        }
    }

    /// <summary>
    /// 转写事件都在会话的接收线程上抛，先封送回 UI 线程再碰输入框 / 状态栏。
    /// 收尾（StopAsync）期间事件照样要来，所以不按 _dictation 是否为 null 拦截。
    /// </summary>
    private void WireDictationEvents(LiveDictation d)
    {
        d.Session.Partial += t => PostToUi(() =>
        {
            _dictPartial = t;
            SyncDictationText();
        });
        d.Session.Sentence += t => PostToUi(() =>
        {
            _dictFinal += t;
            _dictPartial = "";
            SyncDictationText();
        });
        d.Session.Failed += m => PostToUi(() => _chrome.SetStatus("✗ 转写出错：" + m));
    }

    private void PostToUi(Action a)
    {
        try { if (!IsDisposed) BeginInvoke(a); }
        catch { /* 窗口正在关闭，迟到的一句话直接丢 */ }
    }

    private void SyncDictationText() => _input.SetDictation(_dictPrefix + _dictFinal + _dictPartial);

    /// <summary>
    /// 新建对话时给正在进行的转写重新起一个头：已经落进输入框的那部分属于上一条对话，
    /// 不该跟过来。
    ///
    /// 非要单独有这一步，是因为 <see cref="SyncDictationText"/> 每次中间结果都**整框重写**：
    /// 光清输入框的话，下一个 Partial 到达时（几十到几百毫秒）那段字会原样装回去，
    /// 看上去就像「清空没生效」。录着音照录不误，只是从这句起头 —— 已经定稿的几句跟着
    /// 上一条对话一起丢掉了，那是对的：它们本来就不属于新对话。
    /// </summary>
    private void RebaseDictationDraft()
    {
        if (_dictation == null) return;   // 没在录音：这三个字段本来就是空的，也没有谁会重写输入框
        _dictPrefix = "";
        _dictFinal = "";
        _dictPartial = "";
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

    private async void StopRecording()
    {
        var d = _dictation;
        if (d == null) return;
        _dictation = null;
        _pttTimer.Stop();
        Trace.Log("record: stop requested");
        try
        {
            await d.StopAsync();
            SyncDictationText();     // 收尾期间定稿的最后一句也要落上
            string note = d.SystemOnlyMic ? "（仅系统声音）" : "";
            _chrome.SetStatus(_dictFinal.Length + _dictPartial.Length > 0
                ? "✓ 转写完成 " + note
                : "✓ 转写结束，没有识别到文字 " + note);
        }
        catch (Exception ex)
        {
            _chrome.SetStatus("✗ 转写出错：" + ex.Message);
        }
        finally
        {
            d.Dispose();
        }
    }
}
