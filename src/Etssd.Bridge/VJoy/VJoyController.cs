using Microsoft.Extensions.Logging;

namespace Etssd.Bridge.VJoy;

/// <summary>
/// 独占 vJoy 设备驱动卡车。steer 走 X 轴，throttle 走 Y 轴，须与 ETS2 控制器设置中绑定的轴一致。
/// </summary>
/// <remarks>超过 <see cref="CommandTimeout"/> 未收到指令时回中，控制方进程退出后卡车不会保持最后的指令。</remarks>
public sealed class VJoyController : IDisposable
{
    // 与 dataset 的 actuator.py 一致
    private const uint DeviceId = 1;
    private const int AxisMax = 32767;

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMilliseconds(500);

    private readonly Lock _lock = new();
    private readonly Timer _watchdog;
    private long _lastCommandTicks;
    private bool _neutral;

    private VJoyController()
    {
        Neutral();
        _watchdog = new Timer(_ => OnWatchdog(), null, CommandTimeout / 5, CommandTimeout / 5);
    }

    /// <summary>取得设备，驱动缺失或设备被占用时返回 null。</summary>
    public static VJoyController? TryAcquire(ILogger log)
    {
        if (VJoyInstall.DllPath is not { } dll || !File.Exists(dll))
        {
            log.LogWarning("未安装 vJoy，/control 不可用");
            return null;
        }
        if (!VJoyNative.vJoyEnabled() || !VJoyNative.AcquireVJD(DeviceId))
        {
            log.LogWarning("vJoy 设备 {Id} 状态 {Status}，无法取得，/control 不可用",
                DeviceId, VJoyNative.GetVJDStatus(DeviceId));
            return null;
        }
        return new VJoyController();
    }

    /// <param name="throttle">[-1, 1]，正为油门，负为刹车。</param>
    /// <param name="steer">[-1, 1]，与 telemetry 的 gameSteer 同向。</param>
    public void Send(double throttle, double steer)
    {
        lock (_lock)
        {
            // ETS2 绑定的两个轴都与指令约定反向
            VJoyNative.SetAxis(ToAxis(-steer), DeviceId, HidUsage.X);
            VJoyNative.SetAxis(ToAxis(-throttle), DeviceId, HidUsage.Y);
            _lastCommandTicks = Environment.TickCount64;
            _neutral = false;
        }
    }

    public void Dispose()
    {
        _watchdog.Dispose();
        lock (_lock)
        {
            Neutral();
            VJoyNative.RelinquishVJD(DeviceId);
        }
    }

    private void OnWatchdog()
    {
        lock (_lock)
        {
            if (!_neutral && Environment.TickCount64 - _lastCommandTicks > CommandTimeout.TotalMilliseconds)
            {
                Neutral();
            }
        }
    }

    private void Neutral()
    {
        VJoyNative.SetAxis(ToAxis(0), DeviceId, HidUsage.X);
        VJoyNative.SetAxis(ToAxis(0), DeviceId, HidUsage.Y);
        _neutral = true;
    }

    private static int ToAxis(double value) =>
        (int)Math.Round((Math.Clamp(value, -1, 1) + 1) * 0.5 * AxisMax);
}
