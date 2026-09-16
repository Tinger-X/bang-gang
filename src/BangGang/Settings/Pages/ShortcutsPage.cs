using System.Drawing.Drawing2D;

namespace BangGang;

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
