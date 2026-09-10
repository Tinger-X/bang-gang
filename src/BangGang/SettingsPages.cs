using System.Drawing.Drawing2D;

namespace BangGang;

/// <summary>
/// 设置详情页基类：顶部标题区 + 可滚动内容区 + 底部操作条
/// （保存按钮仅在内容变化后可点击，可选的动作按钮排在保存按钮左侧）。
/// </summary>
internal abstract class SettingsPage : Panel, IThemed
{
    private const int PadX = 30;
    private const int HeaderH = 92;
    private const int FooterH = 58;

    public event Action<SettingsPage>? SaveRequested;
    public event Action? DirtyChanged;

    protected readonly StackPanel Stack = new();

    private readonly ScrollArea _body;
    private readonly Label _title = new();
    private readonly Label _desc = new();
    private readonly Label _state = new();
    private readonly PillButton _save;
    private readonly System.Windows.Forms.Timer _stateTimer;
    private readonly List<PillButton> _footerActions = new();
    /// <summary>是否存在未保存的修改。</summary>
    public bool IsDirty { get; private set; }

    protected SettingsPage(string title, string desc)
    {
        BackColor = SC.CardBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _title.Text = title;
        _title.AutoSize = false;
        _title.Font = Theme.UI(15.5f, FontStyle.Bold);
        _title.ForeColor = SC.Ink;
        _title.BackColor = SC.CardBg;
        _title.TextAlign = ContentAlignment.MiddleLeft;

        _desc.Text = desc;
        _desc.AutoSize = false;
        _desc.AutoEllipsis = true;
        _desc.Font = Theme.UI(9.5f);
        _desc.ForeColor = SC.InkMuted;
        _desc.BackColor = SC.CardBg;
        _desc.TextAlign = ContentAlignment.TopLeft;

        _body = new ScrollArea { BackColor = SC.CardBg };
        _body.SetContent(Stack);
        _body.Resize += (_, _) => LayoutStack();
        Stack.ControlAdded += (_, _) => LayoutStack();

        _state.AutoSize = false;
        _state.Font = Theme.UI(9.5f);
        _state.ForeColor = SC.InkMuted;
        _state.BackColor = SC.CardBg;
        _state.TextAlign = ContentAlignment.MiddleLeft;
        _state.Text = "已是最新";

        _save = new PillButton("保存", PillButton.Look.Primary, 108, 38) { On = false };
        _save.Click += (_, _) => SaveRequested?.Invoke(this);

        _stateTimer = new System.Windows.Forms.Timer { Interval = 1800 };
        _stateTimer.Tick += (_, _) => { _stateTimer.Stop(); UpdateSaveUi(); };

        Controls.Add(_title);
        Controls.Add(_desc);
        Controls.Add(_body);
        Controls.Add(_state);
        Controls.Add(_save);
    }

    /// <summary>在保存按钮左侧添加一个次级动作按钮（例如快捷键页的“恢复默认”）。</summary>
    protected PillButton AddFooterAction(string text, Action onClick)
    {
        var b = new PillButton(text, PillButton.Look.Ghost, 112, 38);
        b.Click += (_, _) => onClick();
        _footerActions.Add(b);
        Controls.Add(b);
        return b;
    }

    // ---------- 子类实现 ----------

    /// <summary>用最新设置重新载入并重置“基线”，调用后该页视为无改动。</summary>
    public abstract void Rebind(AppSettings s);

    /// <summary>把本页内容写回设置对象（其它字段保持不变）。</summary>
    public abstract void ApplyTo(AppSettings target);

    /// <summary>与基线比较，返回是否有改动。</summary>
    protected abstract bool ComputeDirty();

    // ---------- 状态 ----------

    /// <summary>任一子控件内容变化后调用。</summary>
    protected void MarkChanged()
    {
        bool d = ComputeDirty();
        if (d != IsDirty)
        {
            IsDirty = d;
            DirtyChanged?.Invoke();
        }
        UpdateSaveUi();
    }

    /// <summary>由浮窗在保存成功后调用。</summary>
    public void MarkSaved()
    {
        IsDirty = false;
        DirtyChanged?.Invoke();
        _save.On = false;
        _state.Text = "已保存 ✓";
        _state.ForeColor = SC.Accent;
        _stateTimer.Stop();
        _stateTimer.Start();
    }

    /// <summary>重置为“无改动”（Rebind 之后调用）。</summary>
    protected void MarkClean()
    {
        IsDirty = false;
        UpdateSaveUi();
        DirtyChanged?.Invoke();
    }

    private void UpdateSaveUi()
    {
        if (IsDirty)
        {
            _save.On = true;
            _state.Text = "有未保存的修改";
            _state.ForeColor = SC.Accent;
        }
        else
        {
            _save.On = false;
            _state.Text = "已是最新";
            _state.ForeColor = SC.InkMuted;
        }
        _save.Invalidate();
        _state.Invalidate();
    }

    /// <summary>放弃未保存的改动：重新按基线载入并清除状态。</summary>
    public void Discard(AppSettings applied)
    {
        Rebind(applied);
        UpdateSaveUi();
    }

    public void Restyle()
    {
        BackColor = SC.CardBg;
        _title.BackColor = SC.CardBg; _title.ForeColor = SC.Ink;
        _desc.BackColor = SC.CardBg; _desc.ForeColor = SC.InkMuted;
        _body.BackColor = SC.CardBg;
        _state.BackColor = SC.CardBg;
        UpdateSaveUi();
        Stack.Restyle();
        Invalidate(true);
    }

    // ---------- 布局 ----------

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_body == null) return;
        int w = Width - PadX * 2;
        _title.SetBounds(PadX, 26, Math.Max(40, w - 46), 28);
        _desc.SetBounds(PadX, 56, Math.Max(40, w - 46), 20);
        _body.SetBounds(PadX, HeaderH, Math.Max(40, w), Math.Max(40, Height - HeaderH - FooterH));
        LayoutFooter();
        LayoutStack();
    }

    private void LayoutFooter()
    {
        int y = Height - FooterH + (FooterH - _save.Height) / 2;
        int right = Width - PadX - _save.Width;
        _save.SetBounds(right, y, _save.Width, _save.Height);
        foreach (var b in _footerActions)
        {
            right -= b.Width + 10;
            b.SetBounds(right, y, b.Width, b.Height);
        }
        _state.SetBounds(PadX, Height - FooterH + (FooterH - 20) / 2, Math.Max(60, right - PadX - 16), 20);
    }

    private void LayoutStack()
    {
        if (_body is not ScrollArea area || Stack == null) return;
        Stack.ArrangeAndResize(Math.Max(40, area.ClientSize.Width - 18));
        area.Relayout(Math.Max(40, area.ClientSize.Width - 18), Stack.ContentHeight);
    }

    /// <summary>页面被切换显示时强制重排内容（隐藏期间的自动布局会被 WinForms 跳过）。</summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) LayoutStack();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(SC.Mix(SC.CardBg, Theme.Border, 0.9f));
        e.Graphics.DrawLine(pen, 0, Height - FooterH, Width, Height - FooterH);
    }

    /// <summary>重建内容：清空后由子类填充。</summary>
    protected void ResetContent()
    {
        Stack.Controls.Clear();
    }

    protected void FinishContent()
    {
        LayoutStack();
        UpdateSaveUi();
    }
}

/// <summary>快捷键设置页。</summary>
internal sealed class ShortcutsPage : SettingsPage
{
    private static readonly (string action, string title, string desc)[] Items =
    {
        ("hide", "显示 / 隐藏窗口", "窗口隐藏时全局生效"),
        ("shot", "选区截屏", "框选区域，截图直接加入输入框"),
        ("record", "音频录制", "录制系统声音与麦克风，松开后加入输入框"),
    };

    private static readonly string[] RecordModes = { "按住", "按下" };

    private readonly Dictionary<string, KeyCapBox> _caps = new();
    private readonly Dictionary<string, ShortcutSetting> _base = new();
    private readonly SegmentedControl _recMode = new(RecordModes, 200, 34);
    private string _baseRecMode = "hold";

    public ShortcutsPage() : base("快捷键", "全局热键，至少需要一个修饰键（Ctrl / Alt / Shift）")
    {
        ResetContent();

        var card = new GroupCard("全局热键", "逐个点击右侧输入框后按下组合键即可替换，Esc 取消录入");
        foreach (var (action, title, desc) in Items)
        {
            var cap = new KeyCapBox(236);
            cap.Set(new ShortcutSetting { Action = action });
            cap.Changed += MarkChanged;
            _caps[action] = cap;
            card.Add(new SettingRow(title, desc, cap));
        }
        card.Height = card.MeasureHeight();
        Stack.Controls.Add(card);

        // ---- 录音方式：按住录音 / 按一下开始再按一下停止 ----
        _recMode.Changed += MarkChanged;
        var rec = new GroupCard("录音方式", "录音快捷键的行为方式");
        rec.Add(new SettingRow("录音方式", "按住说话，或按一下开始、再按一下停止", _recMode));
        rec.Height = rec.MeasureHeight();
        Stack.Controls.Add(rec);

        AddFooterAction("恢复默认", () =>
        {
            var defs = AppSettings.DefaultShortcuts();
            foreach (var (action, _, _) in Items)
                _caps[action].Set(defs.First(x => x.Action == action));
            _recMode.Select(0, false);
            MarkChanged();
        });

        FinishContent();
    }

    public override void Rebind(AppSettings s)
    {
        _base.Clear();
        foreach (var (action, _, _) in Items)
        {
            var sc = s.Shortcuts.FirstOrDefault(x => x.Action == action)
                     ?? AppSettings.DefaultShortcuts().First(x => x.Action == action);
            _base[action] = Clone(sc);
            _caps[action].Set(sc);
        }
        _baseRecMode = s.RecordMode == "toggle" ? "toggle" : "hold";
        _recMode.Select(_baseRecMode == "toggle" ? 1 : 0, false);
        MarkClean();
    }

    public override void ApplyTo(AppSettings target)
    {
        target.Shortcuts = Items.Select(i =>
        {
            var v = _caps[i.action].Value;
            v.Action = i.action;
            return v;
        }).ToList();
        target.RecordMode = _recMode.SelectedIndex == 1 ? "toggle" : "hold";
    }

    protected override bool ComputeDirty() =>
        Items.Any(i => !Same(_caps[i.action].Value, _base[i.action])) ||
        (_recMode.SelectedIndex == 1 ? "toggle" : "hold") != _baseRecMode;

    private static ShortcutSetting Clone(ShortcutSetting s) =>
        new() { Action = s.Action, Ctrl = s.Ctrl, Alt = s.Alt, Shift = s.Shift, Vk = s.Vk };

    private static bool Same(ShortcutSetting a, ShortcutSetting b) =>
        a.Ctrl == b.Ctrl && a.Alt == b.Alt && a.Shift == b.Shift && a.Vk == b.Vk;
}

/// <summary>模型接入设置页：对话模型（OpenAI 兼容 / 多模态）+ 实时语音转写。</summary>
internal sealed class LlmPage : SettingsPage
{
    private static readonly (string label, string url, string model, bool vision)[] ChatPresets =
    {
        ("DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat", false),
        ("通义千问", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-vl-max", true),
        ("OpenAI", "https://api.openai.com/v1", "gpt-4o", true),
        ("Kimi", "https://api.moonshot.cn/v1", "kimi-latest", true),
        ("自定义", "", "", true),
    };

    private static readonly (string label, string url, string model)[] SttPresets =
    {
        ("火山引擎", "wss://openspeech.bytedance.com/api/v3/sauc/bigmodel", "volc.bigasr.sauc.duration"),
        ("OpenAI", "https://api.openai.com/v1", "whisper-1"),
        ("阿里云", "wss://nls-gateway-cn-shanghai.aliyuncs.com/ws/v1", "paraformer-realtime-v2"),
        ("自定义", "", ""),
    };

    private readonly DropdownSelect _chatPreset = new(ChatPresets.Select(p => p.label).ToArray(), 190, ChatPresets.Length - 1);
    private readonly DropdownSelect _sttPreset = new(SttPresets.Select(p => p.label).ToArray(), 190, SttPresets.Length - 1);

    private readonly InputField _chatUrl = new(330, false, "https://api.deepseek.com/v1");
    private readonly InputField _chatKey = new(330, true, "sk-…");
    private readonly InputField _chatModel = new(330, false, "deepseek-chat");
    private readonly ToggleSwitch _chatVision = new();

    private readonly InputField _sttUrl = new(330, false, "wss://…（实时流式接口）");
    private readonly InputField _sttAppId = new(330, false, "App ID / 账号（火山引擎等需要）");
    private readonly InputField _sttKey = new(330, true, "Access Token / API Key");
    private readonly InputField _sttModel = new(330, false, "模型 / 资源 ID");

    private string _bChatUrl = "", _bChatKey = "", _bChatModel = "";
    private bool _bChatVision = true;
    private string _bSttUrl = "", _bSttAppId = "", _bSttKey = "", _bSttModel = "";

    public LlmPage() : base("模型接入", "通用配置：任何 OpenAI 兼容接口与流式语音转写服务都可接入，密钥只保存在本机")
    {
        ResetContent();

        // ---- 对话模型 ----
        var chat = new GroupCard("对话模型", "OpenAI 兼容协议（Base URL + API Key + 模型名），支持多模态输入");
        foreach (var f in new[] { _chatUrl, _chatKey, _chatModel }) f.Changed += MarkChanged;
        _chatVision.Changed += MarkChanged;
        _chatPreset.Chosen += i => { FillChat(i); MarkChanged(); };
        chat.Add(new SettingRow("常用服务商", "选择后自动填好接口地址与模型名", _chatPreset));
        chat.Add(new SettingRow("接口地址", "Base URL，通常以 /v1 结尾", _chatUrl));
        chat.Add(new SettingRow("API Key", "仅存本机，可点右侧图标查看", _chatKey));
        chat.Add(new SettingRow("模型名", "服务商提供的模型名", _chatModel));
        chat.Add(new SettingRow("多模态", "支持图片 / 文件的模型请开启", _chatVision));
        chat.Height = chat.MeasureHeight();
        Stack.Controls.Add(chat);

        // ---- 语音转写 ----
        var stt = new GroupCard("语音转文字", "通用实时（流式）语音转写：按住说话，松开即转写");
        foreach (var f in new[] { _sttUrl, _sttAppId, _sttKey, _sttModel }) f.Changed += MarkChanged;
        _sttPreset.Chosen += i => { FillStt(i); MarkChanged(); };
        stt.Add(new SettingRow("常用服务商", "选择后自动填好实时流式接口", _sttPreset));
        stt.Add(new SettingRow("接口地址", "实时流式地址（wss://…）", _sttUrl));
        stt.Add(new SettingRow("App ID / 账号", "火山引擎等服务商需要", _sttAppId));
        stt.Add(new SettingRow("Access Token", "密钥 / Token，仅存本机", _sttKey));
        stt.Add(new SettingRow("模型 / 资源 ID", "服务商的模型或资源 ID", _sttModel));
        stt.Height = stt.MeasureHeight();
        Stack.Controls.Add(stt);

        FinishContent();
    }

    private void FillChat(int i)
    {
        var (_, url, model, vision) = ChatPresets[i];
        if (url.Length == 0) return;          // “自定义”只切换名称，不动用户填写的内容
        _chatUrl.Text = url;
        _chatModel.Text = model;
        _chatVision.On = vision;
    }

    private void FillStt(int i)
    {
        var (_, url, model) = SttPresets[i];
        if (url.Length == 0) return;
        _sttUrl.Text = url;
        _sttModel.Text = model;
    }

    public override void Rebind(AppSettings s)
    {
        // 先落基线，再写控件值，避免 Rebind 过程中被判定为“有改动”
        _bChatUrl = s.ChatApiUrl; _bChatKey = s.ChatApiKey; _bChatModel = s.ChatModel; _bChatVision = s.ChatVision;
        _bSttUrl = s.SttApiUrl; _bSttAppId = s.SttAppId; _bSttKey = s.SttApiKey; _bSttModel = s.SttModel;

        _chatUrl.Text = s.ChatApiUrl; _chatKey.Text = s.ChatApiKey; _chatModel.Text = s.ChatModel;
        _chatVision.On = s.ChatVision;
        _sttUrl.Text = s.SttApiUrl; _sttAppId.Text = s.SttAppId; _sttKey.Text = s.SttApiKey; _sttModel.Text = s.SttModel;
        MarkClean();
    }

    public override void ApplyTo(AppSettings target)
    {
        target.ChatApiUrl = _chatUrl.Text.Trim();
        target.ChatApiKey = _chatKey.Text;
        target.ChatModel = _chatModel.Text.Trim();
        target.ChatVision = _chatVision.On;
        target.SttApiUrl = _sttUrl.Text.Trim();
        target.SttAppId = _sttAppId.Text.Trim();
        target.SttApiKey = _sttKey.Text;
        target.SttModel = _sttModel.Text.Trim();
    }

    protected override bool ComputeDirty() =>
        _chatUrl.Text.Trim() != _bChatUrl || _chatKey.Text != _bChatKey || _chatModel.Text.Trim() != _bChatModel ||
        _chatVision.On != _bChatVision ||
        _sttUrl.Text.Trim() != _bSttUrl || _sttAppId.Text.Trim() != _bSttAppId ||
        _sttKey.Text != _bSttKey || _sttModel.Text.Trim() != _bSttModel;
}

/// <summary>界面外观设置页：应用主题（亮色 / 暗色 / 跟随系统）、主色、窗口选项。</summary>
internal sealed class UiPage : SettingsPage
{
    private static readonly Color[] AccentPresets =
    {
        Color.FromArgb(47, 112, 224),
        Color.FromArgb(124, 92, 226),
        Color.FromArgb(16, 152, 132),
        Color.FromArgb(232, 122, 44),
        Color.FromArgb(214, 60, 54),
    };

    private static readonly string[] Modes = { "亮色", "暗色", "跟随系统" };

    private readonly SegmentedControl _mode = new(Modes, 320, 38);
    private readonly SwatchChip _accent = new(112);
    private readonly SliderBar _opacity = new(300);
    private readonly ToggleSwitch _border = new();
    private readonly List<ColorDot> _dots = new();

    private string _bMode = "system";
    private int _bAccent;
    private int _bOpacityPct;
    private bool _bBorder;

    private static int Pct(double v) => (int)Math.Round(Math.Clamp(v, 0.5, 1.0) * 100);

    public UiPage() : base("界面外观", "主题、主色与窗口效果，保存后立即应用到整个应用")
    {
        ResetContent();

        // ---- 主题配色 ----
        var theme = new GroupCard("主题配色", "选择应用主题与主色");
        _mode.Changed += MarkChanged;
        _accent.Changed += MarkChanged;

        var accents = new Panel { Size = new Size(156 + 112, 32), BackColor = SC.GroupBg };
        int x = 0;
        foreach (var c in AccentPresets)
        {
            var dot = new ColorDot(c) { Location = new Point(x, 3) };
            dot.Changed += () => { _accent.Value = dot.Value; SyncDots(); MarkChanged(); };
            _dots.Add(dot);
            accents.Controls.Add(dot);
            x += 30;
        }
        accents.Controls.Add(_accent);
        _accent.Location = new Point(x + 6, 0);

        theme.Add(new SettingRow("应用主题", "跟随系统随 Windows 自动切换", _mode));
        theme.Add(new SettingRow("主色", "按钮、选中态与强调文字", accents));
        theme.Height = theme.MeasureHeight();
        Stack.Controls.Add(theme);

        // ---- 窗口 ----
        _opacity.Changed += MarkChanged;
        _border.Changed += MarkChanged;
        var win = new GroupCard("窗口", "透明度只影响观感，不影响防录屏与热键");
        win.Add(new SettingRow("不透明度", "50% – 100%，调整后依然对录屏不可见", _opacity));
        win.Add(new SettingRow("窗口边框", "在窗口四周显示一圈主题色边框", _border));
        win.Height = win.MeasureHeight();
        Stack.Controls.Add(win);

        FinishContent();
    }

    private void SyncDots()
    {
        foreach (var d in _dots) d.Selected = d.Value.ToArgb() == _accent.Value.ToArgb();
    }

    private static int ModeIndex(string mode) => mode switch { "light" => 0, "dark" => 1, _ => 2 };
    private static string ModeValue(int idx) => idx switch { 0 => "light", 1 => "dark", _ => "system" };

    public override void Rebind(AppSettings s)
    {
        // 先落基线，再写控件值
        _bMode = s.ThemeMode; _bAccent = s.Accent; _bOpacityPct = Pct(s.Opacity); _bBorder = s.WindowBorder;

        _mode.Select(ModeIndex(s.ThemeMode), false);
        _accent.Value = Color.FromArgb(s.Accent);
        _opacity.Value = _bOpacityPct;
        _border.On = s.WindowBorder;
        SyncDots();
        MarkClean();
    }

    public override void ApplyTo(AppSettings target)
    {
        target.ThemeMode = ModeValue(_mode.SelectedIndex);
        target.Accent = _accent.Value.ToArgb();
        target.Opacity = _opacity.Value / 100.0;
        target.WindowBorder = _border.On;
    }
    protected override bool ComputeDirty() =>
        ModeValue(_mode.SelectedIndex) != _bMode ||
        _accent.Value.ToArgb() != _bAccent ||
        _opacity.Value != _bOpacityPct ||
        _border.On != _bBorder;
}
