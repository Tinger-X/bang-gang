namespace BangGang;

/// <summary>
/// 剪贴板工具。本应用常驻置顶、又对录屏不可见，「帮我看看我刚复制的那段」是它的天然场景，
/// 而这个动作在模型侧是做不到的 —— 它看不见用户的剪贴板。
/// </summary>
internal static class ClipboardTool
{
    private const string ParamsJson = """
    {
      "type": "object",
      "properties": {
        "max_chars": {
          "type": "integer",
          "description": "最多返回多少个字符，默认 8000。内容更长时会被截断。"
        }
      }
    }
    """;

    public static readonly ToolDef Def = new()
    {
        Name = "clipboard",
        Label = "剪贴板",
        Desc = "读取用户当前剪贴板里的文本。当用户说「我复制的这段」「帮我看看我复制的东西」" +
               "「翻译一下下面这段」但**没有把内容贴出来**时，用这个工具去取。",
        Params = ParamsJson,
        NeedsUi = true,          // Clipboard.GetText() 要求 STA 线程，见 ToolRunner
        Run = (a, _, _) => Task.FromResult(Run1(a)),
    };

    private static string Run1(ToolArgs args)
    {
        int cap = args.Int("max_chars", 8000, 100, ToolRunner.MaxResultChars);

        try
        {
            // 剪贴板里可能是图片、文件列表而不是文字。先问一句再取：
            // 直接 GetText() 在只有图片时会返回空串，那样报「剪贴板是空的」是错的。
            if (!Clipboard.ContainsText())
                return "剪贴板里现在不是文本内容（可能是图片或文件），读不出文字。";

            string text = Clipboard.GetText() ?? "";
            if (text.Trim().Length == 0) return "剪贴板是空的。";

            string head = "剪贴板里的内容（" + text.Length + " 个字符）：\n";
            if (text.Length > cap)
                return head + text[..cap] + "\n\n…（已截断，原文共 " + text.Length + " 个字符）";
            return head + text;
        }
        catch (Exception ex)
        {
            // 剪贴板是**全系统共享**的：别的程序正开着它（某些输入法、远程桌面、
            // 剪贴板历史工具）时取不到。这不是错误，只是这一刻没拿到，说清楚就好。
            return "暂时读不到剪贴板（可能正被其他程序占用）：" + ex.Message;
        }
    }
}
