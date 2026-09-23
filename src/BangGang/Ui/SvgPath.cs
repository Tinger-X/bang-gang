using System.Drawing.Drawing2D;
using System.Globalization;

namespace BangGang;

/// <summary>
/// 把 SVG 的 path 数据（<c>d</c> 属性）转成 GDI+ 的 <see cref="GraphicsPath"/>。
///
/// 为什么自己写而不是引一个 SVG 库：本仓库零第三方依赖（见 CLAUDE.md）。这批图标用到的命令
/// 只有 <c>M L H V C A Z</c> 六种（大小写即绝对/相对），其中五种是直线与三次贝塞尔，直接对应
/// <see cref="GraphicsPath"/> 的 API。唯一要换算的是 <c>A</c>（椭圆弧）—— 它给的是**端点**和两个
/// 标志位，而 GDI+ 要圆心与角度，中间那步按 SVG 规范算（见 <see cref="ArcTo"/>）。
///
/// 弧一律切成 ≤90° 的小段、每段用 <see cref="GraphicsPath.AddBezier(PointF, PointF, PointF, PointF)"/>
/// 逼近，而不是用 <c>AddArc</c>：SVG 的弧可以是**椭圆且带旋转**的，GDI+ 的 AddArc 只画正圆弧，
/// 表达不了那颗 <c>a</c>（很多图标里的圆角就是它）。
/// </summary>
internal static class SvgPath
{
    public static GraphicsPath Parse(string d)
    {
        var path = new GraphicsPath { FillMode = FillMode.Winding };
        var c = new Scan(d);

        float cx = 0, cy = 0;          // 当前点
        float sx = 0, sy = 0;          // 当前子路径的起点（Z 回到这里）
        char cmd = '\0';

        while (true)
        {
            c.SkipSeparators();
            if (!c.More) break;

            // 一个数字紧跟在命令后面、没写新命令，就是同一命令的参数再来一组
            //（`M0 0 10 10 20 20` 等于 `M0 0 L10 10 L20 20`）。
            if (char.IsLetter(c.Peek)) cmd = c.Take();
            else if (cmd == '\0') break;      // 开头就不是命令，整段作废
            else if (cmd == 'M') cmd = 'L';   // 首字母之后的多余坐标对是隐式的 L
            else if (cmd == 'm') cmd = 'l';

            bool rel = char.IsLower(cmd);
            float x, y, x1, y1, x2, y2;

            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                    x = c.Number(); y = c.Number();
                    if (rel) { x += cx; y += cy; }
                    path.StartFigure();
                    cx = sx = x; cy = sy = y;
                    break;

                case 'L':
                    x = c.Number(); y = c.Number();
                    if (rel) { x += cx; y += cy; }
                    path.AddLine(cx, cy, x, y);
                    cx = x; cy = y;
                    break;

                case 'H':
                    x = c.Number();
                    if (rel) x += cx;
                    path.AddLine(cx, cy, x, cy);
                    cx = x;
                    break;

                case 'V':
                    y = c.Number();
                    if (rel) y += cy;
                    path.AddLine(cx, cy, cx, y);
                    cy = y;
                    break;

                case 'C':
                    x1 = c.Number(); y1 = c.Number();
                    x2 = c.Number(); y2 = c.Number();
                    x = c.Number(); y = c.Number();
                    if (rel) { x1 += cx; y1 += cy; x2 += cx; y2 += cy; x += cx; y += cy; }
                    path.AddBezier(cx, cy, x1, y1, x2, y2, x, y);
                    cx = x; cy = y;
                    break;

                case 'A':
                    {
                        float rx = c.Number(), ry = c.Number(), rot = c.Number();
                        bool large = c.Number() != 0, sweep = c.Number() != 0;
                        x = c.Number(); y = c.Number();
                        if (rel) { x += cx; y += cy; }
                        ArcTo(path, cx, cy, rx, ry, rot, large, sweep, x, y);
                        cx = x; cy = y;
                        break;
                    }

                case 'Z':
                    path.CloseFigure();
                    cx = sx; cy = sy;
                    break;

                default:
                    // 碰到没实现的命令就整段停下：画一半的图标比不画更容易被当成「本来就这样」。
                    return path;
            }
        }
        return path;
    }

    /// <summary>
    /// 把一段 SVG 椭圆弧接到路径上。用的是 SVG 规范里的端点参数化换算：
    /// 给的是两个端点、半径、x 轴旋转和两个标志位，先解出圆心与起止角，再切段画成贝塞尔。
    /// </summary>
    private static void ArcTo(GraphicsPath path, float x1, float y1, float rx, float ry,
                              float rotDeg, bool large, bool sweep, float x2, float y2)
    {
        if (rx == 0 || ry == 0) { path.AddLine(x1, y1, x2, y2); return; }
        rx = Math.Abs(rx); ry = Math.Abs(ry);

        double phi = rotDeg * Math.PI / 180.0;
        double cosP = Math.Cos(phi), sinP = Math.Sin(phi);

        // 把两端点搬到「以弧心为原点、且半径不旋转」的坐标系里
        double dx = (x1 - x2) / 2.0, dy = (y1 - y2) / 2.0;
        double px = cosP * dx + sinP * dy;
        double py = -sinP * dx + cosP * dy;

        // 半径给小了（两点之间放不下这么大的弧）就按规范等比放大到刚好放得下
        double lambda = (px * px) / (rx * rx) + (py * py) / (ry * ry);
        if (lambda > 1)
        {
            double k = Math.Sqrt(lambda);
            rx = (float)(rx * k);
            ry = (float)(ry * k);
        }

        double rx2 = (double)rx * rx, ry2 = (double)ry * ry;
        double denom = rx2 * py * py + ry2 * px * px;
        double num = rx2 * ry2 - denom;
        double coef = Math.Sqrt(Math.Max(0, num / denom)) * (large == sweep ? -1 : 1);
        double cxp = coef * (double)rx * py / ry;
        double cyp = -coef * (double)ry * px / rx;

        double cx = cosP * cxp - sinP * cyp + (x1 + x2) / 2.0;
        double cy = sinP * cxp + cosP * cyp + (y1 + y2) / 2.0;

        double t1 = Math.Atan2((py - cyp) / ry, (px - cxp) / rx);
        double t2 = Math.Atan2((-py - cyp) / ry, (-px - cxp) / rx);
        double delta = t2 - t1;
        if (!sweep && delta > 0) delta -= 2 * Math.PI;
        else if (sweep && delta < 0) delta += 2 * Math.PI;

        // 每段不超过 90°：贝塞尔逼近一段弧的误差随张角迅速变大，90° 以内肉眼看不出来
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2)));
        double step = delta / steps;
        double alpha = 4.0 / 3.0 * Math.Tan(step / 4.0);

        double ptx = cx + rx * cosP * Math.Cos(t1) - ry * sinP * Math.Sin(t1);
        double pty = cy + rx * sinP * Math.Cos(t1) + ry * cosP * Math.Sin(t1);

        for (int i = 0; i < steps; i++)
        {
            double a0 = t1 + i * step, a1 = a0 + step;

            double ex = cx + rx * cosP * Math.Cos(a1) - ry * sinP * Math.Sin(a1);
            double ey = cy + rx * sinP * Math.Cos(a1) + ry * cosP * Math.Sin(a1);

            // 端点处的切线方向（对参数求导），乘 alpha 得到控制点
            double d0x = -rx * cosP * Math.Sin(a0) - ry * sinP * Math.Cos(a0);
            double d0y = -rx * sinP * Math.Sin(a0) + ry * cosP * Math.Cos(a0);
            double d1x = -rx * cosP * Math.Sin(a1) - ry * sinP * Math.Cos(a1);
            double d1y = -rx * sinP * Math.Sin(a1) + ry * cosP * Math.Cos(a1);

            path.AddBezier(
                (float)ptx, (float)pty,
                (float)(ptx + alpha * d0x), (float)(pty + alpha * d0y),
                (float)(ex - alpha * d1x), (float)(ey - alpha * d1y),
                (float)ex, (float)ey);

            ptx = ex; pty = ey;
        }
    }

    /// <summary>
    /// 极简扫描器。只认数字与命令字母，够 path 数据用。
    ///
    /// **不能按分隔符切**：path 数据里逗号和空白都可以省（`M0 0L10-5` 里的 <c>10-5</c> 是两个数），
    /// 负号本身就是分隔符。
    /// </summary>
    private sealed class Scan
    {
        private readonly string _s;
        private int _i;
        public Scan(string s) { _s = s; }
        public bool More => _i < _s.Length;
        public char Peek => _i < _s.Length ? _s[_i] : '\0';

        public char Take() => _s[_i++];

        public void SkipSeparators()
        {
            while (_i < _s.Length && (char.IsWhiteSpace(_s[_i]) || _s[_i] == ',')) _i++;
        }

        public float Number()
        {
            SkipSeparators();
            int start = _i;
            if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+')) _i++;
            while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            if (_i < _s.Length && _s[_i] == '.')
            {
                _i++;
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            }
            if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
            {
                int save = _i;
                _i++;
                if (_i < _s.Length && (_s[_i] == '-' || _s[_i] == '+')) _i++;
                if (_i < _s.Length && char.IsDigit(_s[_i])) { while (_i < _s.Length && char.IsDigit(_s[_i])) _i++; }
                else _i = save;
            }
            if (_i == start) { _i++; return 0; }   // 认不出来就前进一格，别死循环

            return float.TryParse(_s[start.._i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0;
        }
    }
}
