namespace BangGang;

/// <summary>
/// 计算器工具。见 <see cref="Expr"/> 里「为什么不直接让模型算」的解释。
/// </summary>
internal static class CalcTool
{
    private const string ParamsJson = """
    {
      "type": "object",
      "properties": {
        "expr": {
          "type": "string",
          "description": "要计算的数学表达式，例如 (1234*5678)/2、sqrt(2)、2^10、log(100)。支持 + - * / % ^ 与括号，常量 pi、e，函数 sqrt/abs/round/floor/ceil/trunc/sign/min/max/pow/log/ln/exp。"
        }
      },
      "required": ["expr"]
    }
    """;

    public static readonly ToolDef Def = new()
    {
        Name = "calc",
        Label = "计算器",
        Desc = "精确计算一个数学表达式。凡是需要算术、百分比、幂、开方、取整的地方都应该用这个工具，" +
               "不要自己心算 —— 你算多位数乘除会出错，而且错得很自信。",
        Params = ParamsJson,
        Primary = "expr",
        Brief = a => "计算 " + Cut(a.Str("expr")),
        Run = (a, _, _) => Task.FromResult(Run1(a)),
    };

    private static string Run1(ToolArgs args)
    {
        string expr = args.Str("expr").Trim();
        if (expr.Length == 0)
            return "没有收到表达式。请在 expr 参数里给出要计算的式子，例如 {\"expr\":\"1234*5678\"}。";

        try
        {
            double v = Expr.Eval(expr);
            // 把原式也回显一遍：模型一次会算好几步，回显能让它（和用户）对上哪一步是哪个结果。
            return expr + " = " + Expr.Num(v);
        }
        catch (FormatException ex)
        {
            // 表达式写错是**模型的**问题而不是工具坏了，所以不抛（抛出去会被包成
            // 「工具执行失败」，模型看到那个措辞会以为是环境问题而不再重试）。
            // 直接把错在哪告诉它，它改一版再调一次就好。
            return "这个表达式算不了：" + ex.Message + " 请修正表达式后重新调用。";
        }
    }

    /// <summary>摘要里别塞一整条长表达式（折叠行会撑爆）。</summary>
    private static string Cut(string s) => s.Length <= 24 ? s : s[..24] + "…";
}
