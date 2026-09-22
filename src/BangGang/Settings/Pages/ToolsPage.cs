namespace BangGang;

/// <summary>
/// 工具调用设置页：一个总开关 + 每个工具一个开关。
///
/// 开关直接对应 <see cref="ToolRegistry.Enabled"/> 里那张表 —— 加工具时这里也要加一行，
/// 而忘了加的表现是「设置里找不到那个工具的开关」，它却照样在被调用。
/// </summary>
internal sealed class ToolsPage : SettingsPage
{
    /// <summary>一张卡里那几行纯说明用的占位控件。SettingRow 的第三个参数是必填的，
    /// 而说明行右侧本来就不该有东西 —— 给个贴底色的空面板让它自己隐身。</summary>
    private static Control Blank() => new Panel { Size = new Size(1, 1), BackColor = SC.GroupBg };

    private readonly ToggleSwitch _all = new();
    private readonly ToggleSwitch _now = new();
    private readonly ToggleSwitch _calc = new();
    private readonly ToggleSwitch _clipboard = new();
    private readonly ToggleSwitch _file = new();
    private readonly ToggleSwitch _webFetch = new();
    private readonly ToggleSwitch _sysInfo = new();

    /// <summary>六个单工具开关，只为「恢复默认」与批量接线方便，顺序无关。</summary>
    private readonly ToggleSwitch[] _tools;

    private bool _bAll, _bNow, _bCalc, _bClip, _bFile, _bWeb, _bSys;
    public ToolsPage() : base("工具调用", "让模型在回答时使用本机的工具：查时间、算数、读文件、抓网页等")
    {
        ResetContent();

        _tools = new[] { _now, _calc, _clipboard, _file, _webFetch, _sysInfo };
        foreach (var t in _tools) t.Changed += MarkChanged;
        // 总开关一变，下面那排的可用性跟着变（见 SyncToolSwitches）。
        _all.Changed += () => { SyncToolSwitches(); MarkChanged(); };

        // ---- 总开关 ----
        var master = new GroupCard("工具调用", "关掉之后下面所有工具一律失效，请求里也不再携带工具");
        master.Add(new SettingRow("启用工具调用", "模型可以在回答前先调用工具拿到准确信息", _all));
        master.Height = master.MeasureHeight();
        Stack.Controls.Add(master);

        // ---- 每个工具 ----
        var tools = new GroupCard("可用的工具", "关掉的工具不会告诉模型，它也就不会去调用");
        tools.Add(new SettingRow("时间日期", "「今天几号」「还有几天」——模型自己不知道今天是哪天", _now));
        tools.Add(new SettingRow("计算器", "精确的算术、百分比、幂与开方；比模型自己心算可靠", _calc));
        tools.Add(new SettingRow("剪贴板", "读当前剪贴板里的文本，「帮我看看我刚复制的东西」", _clipboard));
        tools.Add(new SettingRow("文件读取", "读文本、源码与 .docx / .xlsx / .pptx；读不了 PDF", _file));
        tools.Add(new SettingRow("网页抓取", "抓取用户给出的网址并读出正文；没有搜索功能", _webFetch));
        tools.Add(new SettingRow("系统信息", "屏幕分辨率与缩放、系统版本、本程序版本", _sysInfo));
        tools.Height = tools.MeasureHeight();
        Stack.Controls.Add(tools);

        // ---- 说明 ----
        //
        // 这一块不是凑数：它把三件用户真正会困惑的事写在设置里，而不是等他问到。
        // 顺带也把页面顶过了一屏（内容区只有 484px，只超出几个像素的话
        // 滚动条会变成一根拖不动、又像多余竖线的东西，见 ChatPage 顶上的注释）。
        var about = new GroupCard("说明", "关于工具调用，你可能想知道的");
        about.Add(new SettingRow("调用过程是可见的", "气泡里会显示调用了哪些工具，点开可以看到参数与结果", Blank()));
        about.Add(new SettingRow("不会越聊越占地方", "工具结果只在当轮发给模型，之后的历史里只留一行摘要", Blank()));
        about.Add(new SettingRow("能读到什么", "文件读取只认文本、源码与新版 Office 文档；PDF 与老版 .doc/.xls/.ppt 读不了", Blank()));
        about.Height = about.MeasureHeight();
        Stack.Controls.Add(about);

        AddFooterAction("恢复默认", () =>
        {
            // 拿一份出厂设置来填，而不是写一串 true：出厂就是全开这件事将来若变了，
            // 这里跟着变，不用回来改第二处。
            var d = new AppSettings();
            _all.On = d.ToolsEnabled;
            _now.On = d.ToolNow;
            _calc.On = d.ToolCalc;
            _clipboard.On = d.ToolClipboard;
            _file.On = d.ToolFile;
            _webFetch.On = d.ToolWebFetch;
            _sysInfo.On = d.ToolSysInfo;
            SyncToolSwitches();
            MarkChanged();
        }, NonDefault);

        FinishContent();
    }

    /// <summary>
    /// 总开关关掉时，下面六个一律不可改。
    ///
    /// **灰掉而不是隐藏**：位置留着，用户看得出「这里本来有东西、只是现在不生效」；
    /// 藏起来的话，他刚关掉总开关就会以为那些开关丢了。而且这六个的开关状态本身仍然有意义
    /// —— 重新打开总开关时它们要原样还在（<see cref="ToolRegistry.Enabled"/> 也是先看总开关）。
    /// </summary>
    private void SyncToolSwitches()
    {
        foreach (var t in _tools) t.Enabled = _all.On;
    }

    /// <summary>当前值是否已偏离出厂默认（决定底栏「恢复默认」是否显示）。出厂就是全开。</summary>
    private bool NonDefault()
    {
        var d = new AppSettings();
        return _all.On != d.ToolsEnabled || _now.On != d.ToolNow || _calc.On != d.ToolCalc
            || _clipboard.On != d.ToolClipboard || _file.On != d.ToolFile
            || _webFetch.On != d.ToolWebFetch || _sysInfo.On != d.ToolSysInfo;
    }

    public override void Rebind(AppSettings s)
    {
        // 先把基线落下来再写控件值：ToggleSwitch 的程序赋值**不**触发 Changed，
        // 所以顺序在这里其实无所谓，但和别的页保持一个写法省得以后看错。
        _bAll = s.ToolsEnabled; _bNow = s.ToolNow; _bCalc = s.ToolCalc; _bClip = s.ToolClipboard;
        _bFile = s.ToolFile; _bWeb = s.ToolWebFetch; _bSys = s.ToolSysInfo;

        _all.On = _bAll;
        _now.On = _bNow;
        _calc.On = _bCalc;
        _clipboard.On = _bClip;
        _file.On = _bFile;
        _webFetch.On = _bWeb;
        _sysInfo.On = _bSys;
        SyncToolSwitches();     // 打开浮窗时总开关可能就是关着的，那六个得一开始就是灰的
        MarkClean();
    }

    public override void ApplyTo(AppSettings target)
    {
        target.ToolsEnabled = _all.On;
        target.ToolNow = _now.On;
        target.ToolCalc = _calc.On;
        target.ToolClipboard = _clipboard.On;
        target.ToolFile = _file.On;
        target.ToolWebFetch = _webFetch.On;
        target.ToolSysInfo = _sysInfo.On;
    }

    protected override bool ComputeDirty() =>
        _all.On != _bAll || _now.On != _bNow || _calc.On != _bCalc || _clipboard.On != _bClip
        || _file.On != _bFile || _webFetch.On != _bWeb || _sysInfo.On != _bSys;
}
