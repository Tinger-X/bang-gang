namespace BangGang;

/// <summary>
/// 软件说明页：这个程序是什么、能做什么、有什么限制，以及检查更新。
///
/// 这一页没有任何设置项 —— <see cref="Rebind"/> 只负责把版本号与更新状态刷新一遍，
/// <see cref="ApplyTo"/> 是空的、<see cref="ComputeDirty"/> 恒假（底栏不会出现「保存」）。
/// 底栏那枚「检查更新」走 <see cref="SettingsPage.AddFooterAction"/>，与别的页的次级按钮同一处。
/// </summary>
internal sealed class AboutPage : SettingsPage
{
    private const string HomeUrl = "https://bang-gang.tin.edu.kg";
    private const string RepoUrl = "https://github.com/Tinger-X/bang-gang";

    /// <summary>一张卡里那几行纯说明用的占位控件（右侧本来就不该有东西）。</summary>
    private static Control Blank() => new Panel { Size = new Size(1, 1), BackColor = SC.GroupBg };

    /// <summary>行右侧那枚「打开外部链接」的圆钮。地址写死在本文件里，走 Ui.OpenLink 的白名单。</summary>
    private static IconButton Link() => new(IconButton.Kind.Link, SC.GroupBg) { Clickable = true };

    private readonly SettingRow _version = new("当前版本", "程序内显示的版本号", Blank());
    private readonly SettingRow _status = new("更新状态", "点右下角的「检查更新」问一次官网", Blank());

    private PillButton? _action;
    private UpdateInfo? _pending;      // 已经下好、等着用户点确认的那个安装包
    private string? _downloaded;
    private bool _busy;

    public AboutPage() : base("软件说明", "这个程序是什么、能做什么、有什么限制")
    {
        ResetContent();

        // ---- 关于 ----
        var about = new GroupCard("帮帮", "Windows 桌面直播助手 · MIT 许可证 · 零第三方依赖");

        var home = Link();
        home.Click += (_, _) => Open(HomeUrl);
        var repo = Link();
        repo.Click += (_, _) => Open(RepoUrl);

        about.Add(_version);
        // 地址写全（含 https://）：这两行同时也是让人**照着敲**的，
        // 省掉协议头会变成「复制下来粘到浏览器里能不能打开要看运气」。
        about.Add(new SettingRow("官方网站", HomeUrl, home));
        about.Add(new SettingRow("开源仓库", RepoUrl, repo));
        about.Height = about.MeasureHeight();
        Stack.Controls.Add(about);

        // ---- 能做什么 ----
        var can = new GroupCard("能做什么", "详细的用法见仓库首页的 README");
        // 这一行**必须短**：SettingRow 的说明是单行 Label + 省略号，写长了会被截掉尾巴，
        // 而「没有托盘图标」正是这条要说的重点之一（第一版写了 48 字，实测在「用 Alt+X…」处被切掉）。
        can.Add(new SettingRow("防录屏",
            "只有本机用户看得见；录屏、直播、屏幕共享都拍不到，也没有托盘或任务栏图标",
            Blank()));
        can.Add(new SettingRow("对话", "流式回复、可暂停；推理模型的思考过程单独折叠显示", Blank()));
        can.Add(new SettingRow("工具调用", "模型可以查时间、算数、读剪贴板与文件、搜网页、抓网页、看系统信息", Blank()));
        can.Add(new SettingRow("语音转写", "同时采集系统声音与麦克风，实时转成文字", Blank()));
        can.Height = can.MeasureHeight();
        Stack.Controls.Add(can);

        // ---- 限制 ----
        var limits = new GroupCard("使用上的限制", "这些是设计取舍，不是还没做完");
        limits.Add(new SettingRow("搜索走第三方", "网页搜索用 AnySearch 的公开额度（无需注册），搜索词会发送到该服务", Blank()));
        limits.Add(new SettingRow("读不了 PDF", "文件读取只认文本、源码与新版 Office 文档（.docx/.xlsx/.pptx）", Blank()));
        limits.Height = limits.MeasureHeight();
        Stack.Controls.Add(limits);

        // ---- 更新 ----
        // 不带副标题：这一张只有一行状态，而那句「从官网取最新版本」和下面按钮上的字
        // 说的是同一件事。
        var update = new GroupCard("更新");
        update.Add(_status);
        update.Height = update.MeasureHeight();
        Stack.Controls.Add(update);

        _action = AddFooterAction("检查更新", OnAction);
        HideSave();     // 这一页没有设置项，底栏只留「检查更新」

        FinishContent();
    }

    // ---------------- 更新流程 ----------------

    /// <summary>
    /// 底栏那枚按钮的三种身份（同一个按钮轮流当）：
    /// 「检查更新」→ 下载中（禁用、显示百分比）→「运行安装程序」。
    ///
    /// **下载完不自动运行**：本程序自己去拉起一个 exe 这件事，值得用户再点一次。
    /// 也不需要弹系统对话框 —— 按钮自己变成「运行安装程序」就是那次确认，
    /// 比在全是自绘界面的应用里插一个系统弹窗更合这套外观。
    /// </summary>
    private async void OnAction()
    {
        if (_busy) return;

        if (_downloaded != null)
        {
            if (Updater.RunInstaller(_downloaded))
                SetStatus("正在启动安装程序", "安装程序会自动关闭本程序；装完后从开始菜单重新打开即可");
            else
                SetStatus("启动安装程序失败", "安装包在 " + _downloaded + "，可以手动双击运行");
            return;
        }

        _busy = true;
        SetAction("检查中…", enabled: false);
        SetStatus("正在检查更新", "正在问官网有没有新版本");
        try
        {
            var (info, message) = await Updater.CheckAsync(MainForm.AppVersion, CancellationToken.None);
            if (info == null)
            {
                SetStatus("已是最新", message);
                SetAction("检查更新", enabled: true);
                return;
            }

            SetStatus("正在下载 " + info.Version, Describe(info) + "，0%");
            long lastTick = 0;
            string path = await Updater.DownloadAsync(info, p =>
            {
                // 节流：每 200ms 刷一次界面。不节流的话 49MB 会触发几百次重绘，
                // 而设置浮窗重绘一次要重排整个内容栈。
                long now = Environment.TickCount64;
                if (now - lastTick < 200 && p < 1.0) return;
                lastTick = now;
                SetStatus("正在下载 " + info.Version, Describe(info) + "，" + (int)Math.Round(p * 100) + "%");
            }, CancellationToken.None);

            _pending = info;
            _downloaded = path;
            string note = info.Sha256 == null
                ? "（服务端未提供校验值，本次只核对了大小）"
                : "（校验通过）";
            SetStatus("已下载 " + info.Version, Describe(info) + note + "。点右下角「运行安装程序」继续");
            SetAction("运行安装程序", enabled: true);
        }
        catch (Exception ex)
        {
            SetStatus("更新失败", ex.Message);
            SetAction("检查更新", enabled: true);
            Trace.Log("update failed: " + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private static string Describe(UpdateInfo i) =>
        (i.Variant == "without-runtime" ? "依赖已装运行时" : "自带运行时")
        + "，" + BangGang.AttachTypes.SizeText(i.Size);

    private void SetStatus(string title, string desc) => _status.SetText(title, desc);

    private void SetAction(string text, bool enabled)
    {
        if (_action == null) return;
        _action.Text = text;
        _action.On = enabled;
        _action.Invalidate();
    }

    private static void Open(string url)
    {
        bool ok = Ui.OpenLink(url);
        Trace.Log($"about: open link ok={ok} url={url}");
    }

    // ---------------- SettingsPage ----------------

    public override void Rebind(AppSettings s)
    {
        _version.SetText("当前版本", MainForm.AppVersion);
        MarkClean();
    }

    /// <summary>这一页没有任何设置项，写回时什么都不做。</summary>
    public override void ApplyTo(AppSettings target) { }

    /// <summary>永远不脏 —— 底栏不该出现「保存」。</summary>
    protected override bool ComputeDirty() => false;
}
