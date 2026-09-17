namespace Etssd.Inference;

/// <summary>一阶低通。dt 取自样本时间戳，时间戳不前进时输出保持。</summary>
/// <param name="tauS">时间常数，秒。</param>
public sealed class LowPass(double tauS)
{
    private (ulong TimeUs, double Value)? _last;

    /// <summary>首个样本原样输出。</summary>
    /// <param name="timeUs">样本时间戳，微秒。</param>
    /// <param name="raw">样本值。</param>
    public double Next(ulong timeUs, double raw)
    {
        if (_last is not { } last)
        {
            _last = (timeUs, raw);
            return raw;
        }
        var dt = Math.Max((long)timeUs - (long)last.TimeUs, 0) / 1e6;
        var value = last.Value + (1 - Math.Exp(-dt / tauS)) * (raw - last.Value);
        _last = (Math.Max(timeUs, last.TimeUs), value);
        return value;
    }
}
