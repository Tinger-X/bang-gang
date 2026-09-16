
namespace BangGang;

/// <summary>
/// 「服务商下拉 + 官网接入指引」的组合控件：下拉框在左、一枚「打开外部链接」的图标按钮在右。
///
/// 为什么是一个组合控件而不是两件并排塞进 <see cref="SettingRow"/>：行右侧的控件是**按自身宽度
/// 右对齐**摆的（<c>SettingRow.Arrange</c>），两个控件的总宽要恰好等于 <see cref="LlmPage"/>
/// 里那个 <c>FieldW</c>，才和同一张卡片里的输入框左右对齐。分别摆就得在页里再算一遍位置，
/// 而输入框宽度一改，那笔账就漏掉了 —— 合成一个「宽度 = 一个输入框」的控件，对齐由它自己保证。
///
/// 按钮的可用性跟着服务商走：<see cref="ProviderPreset.Guide"/> 为空（「自定义」，没有官方可指）
/// 时按钮是灰的且点不动。这正是需求里那条「选了非自定义模型才给导航」。
/// </summary>
internal sealed class ProviderPicker : Panel, IThemed
{
    /// <summary>右侧给图标按钮让出的宽度 = 按钮 28 + 与下拉框之间 12 的间隔。</summary>
    private const int LinkBoxW = 40;
    private const int BtnSize = 28;

    private readonly ProviderPreset[] _presets;
    private readonly DropdownSelect _dd;
    private readonly IconButton _link;

    /// <summary>用户换了一家服务商（参数行要不要跟着换由页面决定）。</summary>
    public event Action<int>? Chosen;

    public ProviderPicker(ProviderPreset[] presets, int width)
    {
        _presets = presets;
        Size = new Size(width, InputField.MinHeight);
        BackColor = SC.GroupBg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        // 下拉框**左缘不动、只收窄右边** —— 需求里说的「向左缩小一点，留出图标按钮的位置」。
        // 收窄而不是整体左移：整页的输入框都从同一条左缘起排，移动它会看着像没对齐。
        _dd = new DropdownSelect(presets.Select(p => p.Name).ToArray(), Math.Max(80, width - LinkBoxW), 0);
        _dd.Location = new Point(0, 0);
        _dd.Chosen += i => { SyncGuide(); Chosen?.Invoke(i); };
        Controls.Add(_dd);

        _link = new IconButton(IconButton.Kind.Link, SC.GroupBg)
        {
            Location = new Point(width - BtnSize, (InputField.MinHeight - BtnSize) / 2),
        };
        _link.Click += (_, _) => OpenGuide();
        Controls.Add(_link);

        SyncGuide();
    }

    public string SelectedItem => _dd.SelectedItem;
    public int SelectedIndex => _dd.SelectedIndex;

    /// <summary>当前选中项对应的预设。</summary>
    public ProviderPreset Current => _presets[Math.Clamp(_dd.SelectedIndex, 0, _presets.Length - 1)];

    public void Select(int idx, bool raise)
    {
        _dd.Select(idx, raise);
        SyncGuide();          // Select 不raise时不发 Chosen，这里得自己补一次
    }

    /// <summary>选中的服务商有没有官方指引。没有就把按钮变成「看得见但点不动」的灰态。</summary>
    private void SyncGuide()
    {
        _link.Clickable = Current.Guide.Length > 0;
        _link.Invalidate();
    }

    /// <summary>
    /// 打开当前服务商的官方接入指引。
    ///
    /// 地址一律走 <see cref="Ui.OpenLink"/>（那里只放行 http / https）——
    /// 这个入参最终来自一张常量表，但「把字符串交给 ShellExecute」这件事不该散落在各处。
    ///
    /// 打不开时只记一条 Trace、不弹提示：地址是本程序自己写死的常量，唯一现实的失败原因是
    /// 「这台机器没有默认浏览器」，而设置浮窗里目前没有一条通用的「临时说一句」通道
    /// （<c>SettingsOverlay.Status</c> 至今没有订阅者）。为这一种情况新开一条通道不划算。
    /// </summary>
    private void OpenGuide()
    {
        string url = Current.Guide;
        if (url.Length == 0) return;
        bool ok = Ui.OpenLink(url);
        Trace.Log($"open provider guide ok={ok} url={url}");
    }

    public void Restyle()
    {
        BackColor = SC.GroupBg;
        _dd.Restyle();
        _link.Restyle();
        Invalidate(true);
    }
}
