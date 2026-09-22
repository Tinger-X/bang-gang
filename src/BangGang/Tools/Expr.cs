using System.Globalization;

namespace BangGang;

/// <summary>
/// 计算器工具用的表达式求值：手写词法 + 递归下降，纯 BCL。
///
/// 为什么不走「让模型自己算」：大模型的算术是逐 token 猜出来的，
/// 三四位数以上的乘除就开始错，而且**错得很自信**。这是最典型的
/// 「不该问模型」的事，所以专门给一个工具。
///
/// 不引第三方表达式库（见 CLAUDE.md 的零依赖约束），也不做成
/// 「算术 + 日期 + 单位换算」的四不像 —— 只做数值。日期差让模型配合
/// 时间工具自己推。
///
/// 所有报错都是中文短句：它会原样进模型的上下文，再被转述给用户，
/// 一句 "Unexpected token at 7" 对两头都没用。
/// </summary>
internal static class Expr
{
    /// <summary>求值。表达式有问题时抛 <see cref="FormatException"/>，消息是给人看的中文。</summary>
    public static double Eval(string src) => new Parser(src ?? "").Parse();

    /// <summary>
    /// 把结果转成人看的样子：整数不带小数点，其余按需保留小数位。
    ///
    /// 用 InvariantCulture 而不是当前区域：中文区域的小数点也是「.」，看着一样，
    /// 但 `ToString()` 不指定区域时**会跟着系统设置变**，换个区域就得到「1,5」这种
    /// 模型会读成两个数（或千分位）的东西。
    /// </summary>
    public static string Num(double d)
    {
        if (double.IsNaN(d)) return "不是数（NaN）";
        if (double.IsInfinity(d)) return d > 0 ? "溢出（无穷大）" : "溢出（负无穷大）";

        // 15 位以内且是整数：按整数打印。1e15 以上 double 已经存不下整数精度了，
        // 这时候硬转 long 会给出一个看着精确、其实是错的值。
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
            return ((long)d).ToString(CultureInfo.InvariantCulture);

        double a = Math.Abs(d);
        if (a >= 1e15 || (a > 0 && a < 1e-6))
            return d.ToString("0.######e+0", CultureInfo.InvariantCulture);
        return d.ToString("0.############", CultureInfo.InvariantCulture);
    }

    // ---------------- 解析 ----------------

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s) { _s = s.Replace("**", "^"); }   // 有人习惯写 Python 的 ** 表示幂

        public double Parse()
        {
            Skip();
            if (_i >= _s.Length) throw new FormatException("表达式是空的。");
            double v = Additive();
            Skip();
            if (_i < _s.Length) throw Err("多出来的内容");
            return v;
        }

        // 加减（最低优先级）
        private double Additive()
        {
            double v = Multiplicative();
            while (true)
            {
                Skip();
                if (Take('+')) v += Multiplicative();
                else if (Take('-')) v -= Multiplicative();
                else return v;
            }
        }

        private double Multiplicative()
        {
            double v = Unary();
            while (true)
            {
                Skip();
                if (Take('*')) v *= Unary();
                else if (Take('/'))
                {
                    double d = Unary();
                    // 明确挡一下：不挡的话 1/0 会安静地变成 Infinity 或 NaN，
                    // 一路飘到结果里，用户只看到「∞」而不知道为什么。
                    if (d == 0) throw new FormatException("除以零。");
                    v /= d;
                }
                else if (Take('%'))
                {
                    double d = Unary();
                    if (d == 0) throw new FormatException("对零取余。");
                    v %= d;
                }
                else return v;
            }
        }

        // 正负号：比乘除松、比幂紧，所以 -2^2 = -(2^2) = -4，和数学上的约定一致
        private double Unary()
        {
            Skip();
            if (Take('-')) return -Unary();
            if (Take('+')) return Unary();
            return Power();
        }

        // 幂：右结合（2^3^2 = 2^(3^2)），且指数可以是负数
        private double Power()
        {
            double b = Primary();
            Skip();
            if (Take('^')) return Math.Pow(b, Unary());
            return b;
        }

        private double Primary()
        {
            Skip();
            if (_i >= _s.Length) throw Err("表达式在这里就断了");

            if (Take('('))
            {
                double v = Additive();
                Skip();
                if (!Take(')')) throw Err("缺一个右括号");
                return v;
            }

            char c = _s[_i];
            if (char.IsDigit(c) || c == '.') return Number();

            if (char.IsLetter(c) || c == '_') return Name();

            throw Err("这里有个不认识的符号 '" + c + "'");
        }

        private double Number()
        {
            int start = _i;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
            // 科学计数法：1e5 / 2.5e-3。只在 e/E 后面确实跟着数字时才吃掉它，
            // 否则 `3e` 会被读成一个坏数字，而报错该说的是「这里缺数字」。
            if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
            {
                int save = _i;
                _i++;
                if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                if (_i < _s.Length && char.IsDigit(_s[_i]))
                {
                    while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                }
                else _i = save;
            }
            string t = _s[start.._i];
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                throw new FormatException("「" + t + "」不是一个合法的数字。");
            return v;
        }

        private double Name()
        {
            int start = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
            string name = _s[start.._i].ToLowerInvariant();

            Skip();
            if (!Take('('))
            {
                // 不是函数调用，那就是个常量
                return name switch
                {
                    "pi" => Math.PI,
                    "e" => Math.E,
                    _ => throw new FormatException("不认识的名字「" + name + "」。可用的是常量 pi / e，以及 sqrt、abs、round、floor、ceil、min、max、pow、log、ln、exp。"),
                };
            }

            var a = new List<double>();
            Skip();
            if (!Take(')'))
            {
                while (true)
                {
                    a.Add(Additive());
                    Skip();
                    if (Take(',')) continue;
                    if (Take(')')) break;
                    throw Err("函数参数里缺一个逗号或右括号");
                }
            }
            return Call(name, a);
        }

        private static double Call(string f, List<double> a)
        {
            double One()
            {
                if (a.Count != 1) throw new FormatException(f + "() 要 1 个参数，给了 " + a.Count + " 个。");
                return a[0];
            }
            double Two()
            {
                if (a.Count != 2) throw new FormatException(f + "() 要 2 个参数，给了 " + a.Count + " 个。");
                return a[0];
            }

            switch (f)
            {
                case "sqrt":
                    {
                        double x = One();
                        if (x < 0) throw new FormatException("sqrt() 里是负数，实数范围内开不了方。");
                        return Math.Sqrt(x);
                    }
                case "abs": return Math.Abs(One());
                case "round": return Math.Round(One(), MidpointRounding.AwayFromZero);
                case "floor": return Math.Floor(One());
                case "ceil": return Math.Ceiling(One());
                case "trunc": return Math.Truncate(One());
                case "sign": return Math.Sign(One());
                case "exp": return Math.Exp(One());
                case "ln":
                    {
                        double x = One();
                        if (x <= 0) throw new FormatException("ln() 里必须大于 0。");
                        return Math.Log(x);
                    }
                case "log":
                    {
                        // 一个参数按常用对数（底 10）算，两个参数按 log(x, base) ——
                        // 和计算器上的 log 键一致，也和大多数语言的 log(x, base) 对得上。
                        if (a.Count == 1)
                        {
                            if (a[0] <= 0) throw new FormatException("log() 里必须大于 0。");
                            return Math.Log10(a[0]);
                        }
                        if (a.Count == 2) return Math.Log(a[0], a[1]);
                        throw new FormatException("log() 要 1 或 2 个参数，给了 " + a.Count + " 个。");
                    }
                case "min": return a.Count == 0 ? throw new FormatException("min() 至少要 1 个参数。") : a.Min();
                case "max": return a.Count == 0 ? throw new FormatException("max() 至少要 1 个参数。") : a.Max();
                case "pow": return Math.Pow(Two(), a[1]);
                default:
                    throw new FormatException("没有名为 " + f + "() 的函数。可用的是 sqrt、abs、round、floor、ceil、trunc、sign、min、max、pow、log、ln、exp。");
            }
        }

        // ---------------- 扫描 ----------------

        private void Skip()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
        }

        private bool Take(char c)
        {
            if (_i < _s.Length && _s[_i] == c) { _i++; return true; }
            return false;
        }

        /// <summary>报错时带上「到第几个字符」，长表达式里光看消息找不到位置。</summary>
        private FormatException Err(string what) =>
            new(what + "（第 " + (_i + 1) + " 个字符处）。");
    }
}
