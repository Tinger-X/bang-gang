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

    private readonly SliderBar _temp = new(FieldW)
    {
        Min = 0, Max = 200, Step = 5, Value = 70,
        Format = v => (v / (double)TempScale).ToString("0.00", CultureInfo.InvariantCulture),
    };

    private readonly SliderBar _tokens = new(FieldW)
    {
        // 下限 256：再低就够不着一条正常回复；上限 8192 是多数 OpenAI 兼容接口的默认天花板。
        // 步长 128 是因为滑块只有两百多像素宽 —— 不吸附的话拖出来的是 2917 这种没人打算设的数。
        Min = 256, Max = 8192, Step = 128, Value = 2048,
        Format = v => v.ToString(CultureInfo.InvariantCulture),
    };

    private readonly TextArea _sys = new(FieldW, 3, "例如：你是帮帮，回答简洁准确，用中文。");
    private readonly TextArea _rein = new(FieldW, 3, "例如：只回答与本次对话相关的问题，不要跑题。");

    private int _bTemp, _bTokens;
    private string _bSys = "", _bRein = "";

    public ChatPage() : base("对话设置", "这些参数会随每次请求一起发给模型，只影响对话本身")
    {
        ResetContent();

        _temp.Changed += MarkChanged;
        _tokens.Changed += MarkChanged;
        _sys.Changed += MarkChanged;
        _rein.Changed += MarkChanged;

        var gen = new GroupCard("生成参数", "按当前模型的能力量力而行，设置过大会被接口拒绝");
        gen.Add(new SettingRow("对话温度", "越低越稳定，越高越发散", _temp));
        gen.Add(new SettingRow("最大回复长度", "单次回复的 token 上限", _tokens));
        gen.Height = gen.MeasureHeight();
        Stack.Controls.Add(gen);

        var prompts = new GroupCard("提示词", "留空表示不发送对应的那一段") { RowH = PromptRowH };
        prompts.Add(new SettingRow("系统提示词", "每次对话都放在最前面", _sys));
        prompts.Add(new SettingRow("强化信息", "附在每次提问之后，避免跑题", _rein));
        prompts.Height = prompts.MeasureHeight();
        Stack.Controls.Add(prompts);

        FinishContent();
    }

    public override void Rebind(AppSettings s)
    {
        _temp.Value = (int)Math.Round(Math.Clamp(s.ChatTemperature, 0, 2) * TempScale);
        _tokens.Value = s.ChatMaxTokens;
        _sys.Text = s.ChatSystemPrompt ?? "";
        _rein.Text = s.ChatReinforce ?? "";
        _sys.ClearUndoBuffers();
        _rein.ClearUndoBuffers();

        // 基线取**控件里实际持有的值**，不是 settings 里那个原始值。
        // 滑条会把值吸附到步长上（70 → 70 没事，但 2300 → 2304），拿原始值当基线的话
        // 页面一打开就是「有未保存的修改」，用户还没碰过任何东西。
        _bTemp = _temp.Value;
        _bTokens = _tokens.Value;
        _bSys = _sys.Text;
        _bRein = _rein.Text;
        MarkClean();
    }

    public override void ApplyTo(AppSettings target)
    {
        target.ChatTemperature = _temp.Value / (double)TempScale;
        target.ChatMaxTokens = _tokens.Value;
        target.ChatSystemPrompt = _sys.Text;
        target.ChatReinforce = _rein.Text;
    }

    protected override bool ComputeDirty() =>
        _temp.Value != _bTemp ||
        _tokens.Value != _bTokens ||
        _sys.Text != _bSys ||
        _rein.Text != _bRein;
}
