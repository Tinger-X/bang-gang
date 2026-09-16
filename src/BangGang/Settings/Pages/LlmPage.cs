using System.Drawing.Drawing2D;

namespace BangGang;

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
