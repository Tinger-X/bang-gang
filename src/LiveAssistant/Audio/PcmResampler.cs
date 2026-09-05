namespace LiveAssistant;

/// <summary>
/// 轻量线性重采样器：把"任意采样率、任意声道数"的 float 交错数据，
/// 转换为固定 2 声道、目标采样率的交错数据。用于系统与麦克风采样率不一致时对齐。
/// 常见情形（两路同采样率）为直通，无性能损耗。
/// </summary>
internal sealed class PcmResampler
{
    private readonly int _srcCh;      // 源声道数
    private readonly double _step;    // 源采样偏移 / 每输出采样

    private double _p;                // 下一输出在源坐标中的位置
    private int _base;                // 本次已消费的源帧数（下一块的起始索引）
    private readonly float[] _prev;   // 上一块末帧（每声道）
    private bool _hasPrev;

    /// <param name="dstRate">目标（主/系统）采样率。</param>
    public PcmResampler(int srcRate, int srcChannels, int dstRate)
    {
        _srcCh = srcChannels;
        _step = (double)srcRate / dstRate;
        _prev = new float[srcChannels];
    }

    /// <summary>输入一块 native 交错 float（frames × srcCh），输出目标 2 声道交错 float。</summary>
    public float[] Process(float[] src, int frames)
    {
        // 输出容量估算（含跨块滞留的一小段）
        int cap = Math.Max(8, (int)((frames + 2) * (1.0 / Math.Max(_step, 1e-6))) * 2 + 8);
        var outBuf = new List<float>(cap);

        int lastAvail = _base + frames - 1;          // 本块已知的最后一个源采样索引
        int ch = _srcCh;

        // 允许 f+1 <= lastAvail（即插值两端都存在）；f 可达 lastAvail-1
        while (_p <= lastAvail - 1)
        {
            int f = (int)_p;
            double frac = _p - f;
            float l, r;
            if (ch >= 2)
            {
                float a0l = Sample(src, frames, f, 0, ch);
                float a0r = Sample(src, frames, f, 1, ch);
                float a1l = Sample(src, frames, f + 1, 0, ch);
                float a1r = Sample(src, frames, f + 1, 1, ch);
                l = a0l + (float)((a1l - a0l) * frac);
                r = a0r + (float)((a1r - a0r) * frac);
            }
            else // 单声道 → 双声道复制
            {
                float a0 = Sample(src, frames, f, 0, ch);
                float a1 = Sample(src, frames, f + 1, 0, ch);
                l = r = a0 + (float)((a1 - a0) * frac);
            }
            outBuf.Add(l);
            outBuf.Add(r);
            _p += _step;
        }

        // 保存本块末帧，供下块跨界插值
        if (frames > 0)
        {
            for (int c = 0; c < ch; c++)
                _prev[c] = src[(frames - 1) * ch + c];
            _hasPrev = true;
        }
        _base += frames;
        return outBuf.ToArray();
    }

    private float Sample(float[] buf, int frames, int idx, int chan, int ch)
    {
        // idx 可能为跨界所需的"上一块末帧"
        if (idx == _base - 1 && _hasPrev)
        {
            // 仅在 idx 恰好是上一块末帧时命中 _prev（f==_base-1）
            if (chan < _prev.Length) return _prev[chan];
            return 0f;
        }
        int local = idx - _base;
        if (local < 0 || local >= frames) return 0f;
        int flat = local * ch + Math.Min(chan, ch - 1);
        return buf[flat];
    }

    /// <summary>数据结束：补一个保持帧（zero-order hold）以排空最后若干输出。</summary>
    public float[] Flush(int srcChannels)
    {
        int ch = srcChannels;
        float[] tail = new float[ch];
        for (int c = 0; c < ch; c++) tail[c] = _prev[c];
        return Process(tail, 1); // 视作一个额外源帧
    }
}
