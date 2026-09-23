using System.Globalization;

namespace BangGang;

/// <summary>
/// 对话设置页：每次请求都带上的那几个参数（温度、最大回复长度）与两段提示词。
///
/// 参数存在 <see cref="AppSettings"/> 上、由发请求的那一端读取，这一页只负责编辑与保存；
/// 页面本身不认识任何服务商 —— 温度 / 长度是所有 OpenAI 兼容接口共有的字段。
/// </summary>
internal sealed class ChatPage : SettingsPage
{
    /// <summary>控件宽度。与模型接入页的输入框同宽，右缘才对得齐。</summary>
    private const int FieldW = 330;

    /// <summary>
    /// 提示词那个卡片里一行的净高度：文本域本身的高度 + 上下留白。
    ///
    /// 这个数不是随便取的：整页内容必须**矮过卡片的内容区**（880×640 的卡片扣掉顶栏 92
    /// 和底栏 58，再扣掉 3px 内缩，只剩 484）。多出哪怕 4px，<see cref="ScrollArea"/> 就会
    /// 画出一根几乎占满整条轨道的滑条 —— 拖不动、又像一道多出来的竖线。
    /// 一行 104 时整页 472，留 12px 余量。
    /// </summary>
    private const int PromptRowH = 104;

    /// <summary>温度滑条的值是**百分之一度**（0–200 → 0.00–2.00）。滑块是整数控件。</summary>
    private const int TempScale = 100;

    /// <summary>
    /// 长度滑条上的「不限」档位：最大值再往右多出一格，读数显示「不限」、存成 0
    /// （请求里不带 max_tokens，见 <see cref="LlmConfig.MaxTokens"/>）。
    /// 8320 = 8192 + 128，恰好落在步长网格上（256 + 63×128），吸附不会把它吃掉。
    /// </summary>
    private const int UnlimitedMark = 8192 + 128;

    private readonly SliderBar _temp = new(FieldW)
    {
        Min = 0, Max = 200, Step = 5, Value = 70,
        Format = v => (v / (double)TempScale).ToString("0.00", CultureInfo.InvariantCulture),
    };

    private readonly SliderBar _tokens = new(FieldW)
    {
        // 下限 256：再低就够不着一条正常回复；上限 8192 是多数 OpenAI 兼容接口的默认天花板，
        // 最右端再留一格「不限」（UnlimitedMark）。
        // 步长 128 是因为滑块只有两百多像素宽 —— 不吸附的话拖出来的是 2917 这种没人打算设的数。
        Min = 256, Max = UnlimitedMark, Step = 128, Value = 2048,
        Format = v => v >= UnlimitedMark ? "不限" : v.ToString(CultureInfo.InvariantCulture),
    };

    private readonly TextArea _sys = new(FieldW, 3, "例如：你是帮帮，回答简洁准确，用中文。");
    private readonly TextArea _rein = new(FieldW, 3, "例如：只回答与本次对话相关的问题，不要跑题。");

    /// <summary>上下文处理方式。索引 ↔ 存储值写成两个纯函数，别在六处各写一遍三元表达式。</summary>
    private static readonly string[] CtxModes = { "最新", "压缩" };

    private readonly SegmentedControl _ctxMode = new(CtxModes, 34);

    /// <summary>
    /// 上下文窗口滑条。8K 起步 —— 比这更小的模型现在基本见不到了，而这个下限也顺带
    /// 挡住了「手滑拖到最左边导致每轮都在压缩」。
    /// </summary>
    private readonly SliderBar _ctxWindow = new(FieldW)
    {
        Min = 8192, Max = 262144, Step = 8192, Value = 32768,
        Format = v => (v / 1024).ToString(CultureInfo.InvariantCulture) + "K",
    };

    private int _bTemp, _bTokens, _bCtxWindow;
    private string _bSys = "", _bRein = "", _bCtxMode = "compact";

    public ChatPage() : base("对话参数", "这些参数会随每次请求一起发给模型，只影响对话本身")
    {
        ResetContent();

        _temp.Changed += MarkChanged;
        _tokens.Changed += MarkChanged;
        _sys.Changed += MarkChanged;
        _rein.Changed += MarkChanged;

        var gen = new GroupCard("生成参数", "按当前模型的能力量力而行，设置过大会被接口拒绝");
        gen.Add(new SettingRow("对话温度", "越低越稳定，越高越发散", _temp));
        gen.Add(new SettingRow("最大回复长度", "单次上限，推理模型的思考也计入；最右为不限", _tokens));
        gen.Height = gen.MeasureHeight();
        Stack.Controls.Add(gen);

        var prompts = new GroupCard("提示词", "留空表示不发送对应的那一段") { RowH = PromptRowH };
        // 这两行高 104、右侧是多行文本域：标题贴文本域顶端排，不垂直居中（SettingRow.TopAlign）。
        prompts.Add(new SettingRow("系统提示词", "每次对话都放在最前面", _sys) { TopAlign = true });
        prompts.Add(new SettingRow("强化信息", "附在每次提问之后，避免跑题", _rein) { TopAlign = true });
        prompts.Height = prompts.MeasureHeight();
        Stack.Controls.Add(prompts);

        // ---- 上下文 ----
        //
        // 单独开一张卡（而不是往「生成参数」里塞一行）：那一页的内容高度预算是死的
        // （见 PromptRowH 的注释），塞进去会变成 524 —— 超出的那点会让滚动条占满 92% 的轨道，
        // 拖不动、又像一道多余的竖线。新开一张卡是 664，滚动条 73%，
        // 与「工具调用」页同一个量级，看着就是一根正常的滚动条。
        _ctxMode.Changed += MarkChanged;
        _ctxWindow.Changed += MarkChanged;

        var ctx = new GroupCard("上下文", "聊得久了、内容超出模型能记住的长度时怎么办");
        ctx.Add(new SettingRow("上下文处理方式", "超长时丢掉最老的消息，或把它们压成一段摘要", _ctxMode));
        ctx.Add(new SettingRow("上下文窗口", "按所用模型填；填小了只是提前压缩，填大了会被接口拒绝", _ctxWindow));
        ctx.Height = ctx.MeasureHeight();
        Stack.Controls.Add(ctx);

        AddFooterAction("恢复默认", () =>
        {
            var d = new AppSettings();
            _temp.Value = (int)Math.Round(d.ChatTemperature * TempScale);
            _tokens.Value = d.ChatMaxTokens <= 0 ? UnlimitedMark : d.ChatMaxTokens;
            _sys.Text = d.ChatSystemPrompt;
            _rein.Text = d.ChatReinforce;
            _ctxMode.Select(CtxModeIndex(d.ChatContextMode), false);
            _ctxWindow.Value = d.ChatContextWindow;
            MarkChanged();
        }, NonDefault);

        FinishContent();
    }

    /// <summary>索引 ↔ 存储值的双向映射（照 UiPage 里那对纯函数的写法）。</summary>
    private static int CtxModeIndex(string? mode) => mode == "latest" ? 0 : 1;

    private static string CtxModeValue(int idx) => idx == 0 ? "latest" : "compact";

    /// <summary>当前值是否已偏离出厂默认（决定底栏「恢复默认」是否显示）。</summary>
    private bool NonDefault()
    {
        var d = new AppSettings();
        return _temp.Value != (int)Math.Round(d.ChatTemperature * TempScale)
            || _tokens.Value != (d.ChatMaxTokens <= 0 ? UnlimitedMark : d.ChatMaxTokens)
            || _sys.Text != d.ChatSystemPrompt
            || _rein.Text != d.ChatReinforce
            || CtxModeValue(_ctxMode.SelectedIndex) != d.ChatContextMode
            || _ctxWindow.Value != d.ChatContextWindow;
    }

    public override void Rebind(AppSettings s)
    {
        _temp.Value = (int)Math.Round(Math.Clamp(s.ChatTemperature, 0, 2) * TempScale);
        // 0（不限）映射到滑条最右端那一格；正数照旧（不在网格上的会被吸附）。
        _tokens.Value = s.ChatMaxTokens <= 0 ? UnlimitedMark : s.ChatMaxTokens;
        _sys.Text = s.ChatSystemPrompt ?? "";
        _rein.Text = s.ChatReinforce ?? "";
        _ctxMode.Select(CtxModeIndex(s.ChatContextMode), false);
        _ctxWindow.Value = Math.Clamp(s.ChatContextWindow, _ctxWindow.Min, _ctxWindow.Max);
        _sys.ClearUndoBuffers();
        _rein.ClearUndoBuffers();

        // 基线取**控件里实际持有的值**，不是 settings 里那个原始值。
        // 滑条会把值吸附到步长上（70 → 70 没事，但 2300 → 2304），拿原始值当基线的话
        // 页面一打开就是「有未保存的修改」，用户还没碰过任何东西。
        _bTemp = _temp.Value;
        _bTokens = _tokens.Value;
        _bSys = _sys.Text;
        _bRein = _rein.Text;
        _bCtxMode = CtxModeValue(_ctxMode.SelectedIndex);
        _bCtxWindow = _ctxWindow.Value;
        MarkClean();
    }

    public override void ApplyTo(AppSettings target)
    {
        target.ChatTemperature = _temp.Value / (double)TempScale;
        target.ChatMaxTokens = _tokens.Value >= UnlimitedMark ? 0 : _tokens.Value;
        target.ChatSystemPrompt = _sys.Text;
        target.ChatReinforce = _rein.Text;
        target.ChatContextMode = CtxModeValue(_ctxMode.SelectedIndex);
        target.ChatContextWindow = _ctxWindow.Value;
    }

    protected override bool ComputeDirty() =>
        _temp.Value != _bTemp ||
        _tokens.Value != _bTokens ||
        _sys.Text != _bSys ||
        _rein.Text != _bRein ||
        CtxModeValue(_ctxMode.SelectedIndex) != _bCtxMode ||
        _ctxWindow.Value != _bCtxWindow;
}
