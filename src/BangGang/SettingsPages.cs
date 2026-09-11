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

    /// <summary>内容高度变化后重新排布（切换服务商导致行数变化时调用）。</summary>
    protected void RelayoutContent()
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
    private readonly SegmentedControl _recMode = new(RecordModes, 34);
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

/// <summary>
/// 模型接入设置页：对话模型（OpenAI 兼容 / 多模态）+ 实时语音转写。
/// 服务商决定需要哪些参数，因此参数行是**预先创建、按服务商显示/隐藏**的，
/// 切换时只改可见性、标题与取值，不销毁重建控件 —— 切换过程不会闪烁。
/// </summary>
internal sealed class LlmPage : SettingsPage
{
    private const int FieldW = 330;          // 输入框与下拉框同宽
    private const int RowH = 56;             // 输入框加高后，行也要相应加高

    /// <summary>所有服务商可能用到的参数键，以及展示顺序。</summary>
    private static readonly string[] AllKeys = { "url", "appid", "key", "secret2", "model" };

    private sealed class Block
    {
        public required GroupCard Card;
        public required DropdownSelect Provider;
        public readonly List<SettingRow> Rows = new();
        public readonly Dictionary<string, InputField> Inputs = new();
        public SettingRow? VisionRow;
        public ToggleSwitch? Vision;
        public bool Chat;
        public ProviderPreset[] Presets = Array.Empty<ProviderPreset>();
    }

    private readonly Block _chat = new() { Card = null!, Provider = null! };
    private readonly Block _stt = new() { Card = null!, Provider = null! };

    private string _baseProviderChat = "自定义", _baseProviderStt = "自定义";
    private Dictionary<string, Dictionary<string, string>> _baseChatProfiles = new();
    private Dictionary<string, Dictionary<string, string>> _baseSttProfiles = new();
    private bool _baseVision = true;

    public LlmPage() : base("模型接入", "选择服务商后会自动带出需要的参数项，密钥只保存在本机")
    {
        ResetContent();

        _chat.Card = new GroupCard("对话模型", "任何 OpenAI 兼容接口都可接入，支持多模态输入") { RowH = RowH };
        _stt.Card = new GroupCard("语音转文字", "通用实时（流式）语音转写：按住说话，松开即转写") { RowH = RowH };
        Stack.Controls.Add(_chat.Card);
        Stack.Controls.Add(_stt.Card);

        BuildBlock(_chat, Providers.Chat, chat: true);
        BuildBlock(_stt, Providers.Stt, chat: false);

        FinishContent();
    }

    /// <summary>一次性建好某个分区的全部行（服务商行 + 所有参数行 + 多模态行）。</summary>
    private void BuildBlock(Block b, ProviderPreset[] presets, bool chat)
    {
        b.Chat = chat;
        b.Presets = presets;
        b.Provider = new DropdownSelect(presets.Select(p => p.Name).ToArray(), FieldW, 0);
        b.Provider.Chosen += i => SwitchProvider(b, i);

        AddRow(b, providerRow: true, new SettingRow("服务商", "选择后自动带出需要的参数", b.Provider));

        foreach (var key in AllKeys)
        {
            var input = new InputField(FieldW, key is "key" or "secret2", "");
            input.Changed += () =>
            {
                var prov = b.Provider.SelectedItem;
                _working.ProfileOf(chat, prov)[key] = input.Text.Trim();
                MarkChanged();
            };
            b.Inputs[key] = input;
            AddRow(b, providerRow: false, new SettingRow(key, "", input));
        }

        if (chat)
        {
            b.Vision = new ToggleSwitch();
            b.Vision.Changed += MarkChanged;
            b.VisionRow = new SettingRow("多模态", "支持图片 / 文件的模型请开启", b.Vision);
            AddRow(b, providerRow: false, b.VisionRow);
        }

        ApplyPreset(b, presets[0]);
    }

    private static void AddRow(Block b, bool providerRow, SettingRow row)
    {
        b.Rows.Add(row);
        b.Card.Add(row);
    }

    /// <summary>让某个分区按服务商显示/隐藏参数行，并更新标题、说明、示例与取值。</summary>
    private void ApplyPreset(Block b, ProviderPreset preset)
    {
        b.Card.SuspendLayout();

        var keys = preset.Fields.Select(f => f.Key).ToList();
        var stored = _working.ProfileOf(b.Chat, preset.Name);

        // 服务商行标题保持固定；参数行按需显示
        foreach (var key in AllKeys)
        {
            var spec = preset.Fields.FirstOrDefault(f => f.Key == key);
            var row = b.Rows.First(r => ReferenceEquals(r.RightControl, b.Inputs[key]));
            if (spec == null) { row.SetShown(false); continue; }

            if (!stored.TryGetValue(key, out var val) || (val.Length == 0 && preset.Defaults.TryGetValue(key, out _)))
            {
                val = preset.Defaults.TryGetValue(key, out var dv) ? dv : "";
                stored[key] = val;
            }
            row.SetText(spec.Label, spec.Desc);
            row.SetShown(true);
            var input = b.Inputs[key];
            input.SetPlaceholder(spec.Placeholder);
            if (input.Text != val) input.Text = val;      // 切回该服务商时恢复它自己的值
        }

        b.VisionRow?.SetShown(true);

        b.Card.ResumeLayout();
        b.Card.Height = b.Card.MeasureHeight();
        b.Card.Arrange();
        RelayoutContent();
    }

    private void SwitchProvider(Block b, int index)
    {
        index = Math.Clamp(index, 0, b.Presets.Length - 1);
        Trace.Log($"switch provider index={index} name={b.Presets[index].Name}");
        ApplyPreset(b, b.Presets[index]);
        MarkChanged();
    }

    private AppSettings _working = new();

    private static string Get(Block b, string key) =>
        b.Inputs.TryGetValue(key, out var f) && f.Visible ? f.Text.Trim() : "";

    public override void Rebind(AppSettings s)
    {
        _working = new AppSettings();
        _working.CopyFrom(s);
        _baseProviderChat = s.ChatProvider;
        _baseProviderStt = s.SttProvider;
        _baseChatProfiles = s.ChatProfiles.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value));
        _baseSttProfiles = s.SttProfiles.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value));
        _baseVision = s.ChatVision;

        _chat.Provider.Select(IndexOf(Providers.Chat, s.ChatProvider), false);
        _stt.Provider.Select(IndexOf(Providers.Stt, s.SttProvider), false);
        if (_chat.Vision != null) _chat.Vision.On = s.ChatVision;
        ApplyPreset(_chat, Providers.Chat[IndexOf(Providers.Chat, s.ChatProvider)]);
        ApplyPreset(_stt, Providers.Stt[IndexOf(Providers.Stt, s.SttProvider)]);
        MarkClean();
    }

    private static int IndexOf(ProviderPreset[] presets, string name)
    {
        int i = Array.FindIndex(presets, p => p.Name == name);
        return i < 0 ? 0 : i;
    }

    public override void ApplyTo(AppSettings target)
    {
        target.ChatProvider = _chat.Provider.SelectedItem;
        target.ChatProfiles = _working.ChatProfiles.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value));
        target.ChatVision = _chat.Vision?.On ?? true;
        target.SttProvider = _stt.Provider.SelectedItem;
        target.SttProfiles = _working.SttProfiles.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value));

        // 同步旧版扁平字段，保证向下兼容
        target.ChatApiUrl = Get(_chat, "url");
        target.ChatApiKey = Get(_chat, "key");
        target.ChatModel = Get(_chat, "model");
        target.SttApiUrl = Get(_stt, "url");
        target.SttAppId = Get(_stt, "appid");
        target.SttApiKey = Get(_stt, "key");
        target.SttModel = Get(_stt, "model");
    }

    protected override bool ComputeDirty()
    {
        if (_chat.Provider.SelectedItem != _baseProviderChat) return true;
        if (_stt.Provider.SelectedItem != _baseProviderStt) return true;
        if ((_chat.Vision?.On ?? true) != _baseVision) return true;
        return !SameProfiles(_working.ChatProfiles, _baseChatProfiles) ||
               !SameProfiles(_working.SttProfiles, _baseSttProfiles);
    }

    private static bool SameProfiles(
        Dictionary<string, Dictionary<string, string>> a,
        Dictionary<string, Dictionary<string, string>> b)
    {
        static string Key(Dictionary<string, Dictionary<string, string>> m) =>
            string.Join("\n", m.OrderBy(kv => kv.Key).Select(kv =>
                kv.Key + "=" + string.Join(",", kv.Value.OrderBy(x => x.Key).Select(x => x.Key + ":" + x.Value))));
        return Key(a) == Key(b);
    }
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

    private readonly SegmentedControl _mode = new(Modes, 34);
    private readonly SwatchChip _accent = new(112);
    private readonly SliderBar _opacity = new(300);
    private readonly ToggleSwitch _border = new();
    private readonly ColorDotPicker _dots = new(AccentPresets);

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

        // 预设色球：选中用一块会滑动的背景表示；自定义颜色时选中背景自动隐藏
        _dots.Changed += () =>
        {
            _accent.Value = _dots.Value;
            MarkChanged();
        };
        _accent.Changed += () =>
        {
            // 用户用取色器选了自定义颜色：只有刚好等于某个预设时才点亮对应色球
            _dots.SetValue(_accent.Value, raise: false);
            MarkChanged();
        };

        var accents = new Panel { Size = new Size(_dots.Width + 6 + 112, 32), BackColor = SC.GroupBg };
        accents.Controls.Add(_dots);
        _dots.Location = new Point(0, 0);
        accents.Controls.Add(_accent);
        _accent.Location = new Point(_dots.Width + 6, 0);

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

    private static int ModeIndex(string mode) => mode switch { "light" => 0, "dark" => 1, _ => 2 };
    private static string ModeValue(int idx) => idx switch { 0 => "light", 1 => "dark", _ => "system" };

    public override void Rebind(AppSettings s)
    {
        // 先落基线，再写控件值
        _bMode = s.ThemeMode; _bAccent = s.Accent; _bOpacityPct = Pct(s.Opacity); _bBorder = s.WindowBorder;

        _mode.Select(ModeIndex(s.ThemeMode), false);
        _accent.Value = Color.FromArgb(s.Accent);
        _dots.SetValue(Color.FromArgb(s.Accent), raise: false);
        _opacity.Value = _bOpacityPct;
        _border.On = s.WindowBorder;
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
