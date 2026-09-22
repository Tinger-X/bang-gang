using System.Globalization;

namespace BangGang;

/// <summary>
/// 时间工具。存在的理由很直接：模型不知道今天是几号 —— 它的知识停在训练数据截止那天，
/// 问它「今天」「还有几天」要么拒答要么编一个。
/// </summary>
internal static class NowTool
{
    public static readonly ToolDef Def = new()
    {
        Name = "now",
        Label = "时间日期",
        Desc = "获取当前的本地日期和时间。凡是涉及「今天」「现在」「还有几天」「今年第几周」" +
               "这类问题时，一律先用这个工具拿准确时间，不要凭记忆回答日期 —— 你不知道今天是几号。",
        Run = (_, _, _) => Task.FromResult(Describe()),
    };

    private static string Describe()
    {
        var t = DateTimeOffset.Now;
        var d = t.DateTime;
        var sb = new System.Text.StringBuilder();

        sb.Append("当前本地时间：").Append(d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
          .Append('（').Append(Weekday(d.DayOfWeek)).Append("）");

        // 时区名（如「中国标准时间」）+ 偏移。带上名字是因为用户问「我在哪个时区」时
        // 光一个 +08:00 说不清，而名字是系统按当前区域给的，本来就是用户机器上的说法。
        sb.Append("\n时区：");
        try { sb.Append(TimeZoneInfo.Local.DisplayName).Append(' '); }
        catch { /* 取不到名字就只报偏移 */ }
        sb.Append(Offset(t.Offset));

        sb.Append("\nUTC 时间：").Append(t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        sb.Append("\nUnix 时间戳（秒）：").Append(t.ToUnixTimeSeconds());
        sb.Append("\n今年第 ").Append(ISOWeek.GetWeekOfYear(d)).Append(" 周，今天是这一年的第 ").Append(d.DayOfYear).Append(" 天");
        return sb.ToString();
    }

    private static string Offset(TimeSpan o) =>
        (o < TimeSpan.Zero ? "-" : "+") + o.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    private static string Weekday(DayOfWeek w) => w switch
    {
        DayOfWeek.Monday => "星期一",
        DayOfWeek.Tuesday => "星期二",
        DayOfWeek.Wednesday => "星期三",
        DayOfWeek.Thursday => "星期四",
        DayOfWeek.Friday => "星期五",
        DayOfWeek.Saturday => "星期六",
        _ => "星期日",
    };
}
