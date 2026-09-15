namespace Etssd.Inference;

/// <summary>控制器用到的一帧车辆状态，从 Local\SCSTelemetry 遥测块解析。</summary>
/// <param name="TimeUs">paused_simulation_time 的累积，微秒，暂停时冻结。</param>
/// <param name="Speed">m/s。</param>
/// <param name="YawRate">rad/s。</param>
/// <param name="Pitch">rad，正值低头即下坡。</param>
public readonly record struct VehicleState(ulong TimeUs, double Speed, double YawRate, double Pitch)
{
    public static VehicleState From(byte[] telemetry) => new(
        BitConverter.ToUInt64(telemetry, 8),
        BitConverter.ToSingle(telemetry, 948),
        BitConverter.ToSingle(telemetry, 1884) * Math.Tau,
        -BitConverter.ToDouble(telemetry, 2232) * Math.Tau);
}

/// <param name="Steer">[-1, 1]，正为左。</param>
/// <param name="Accel">[-1, 1]，正油门负刹车。</param>
public readonly record struct Command(double Steer, double Accel);

/// <summary>固定规划时刻的预测逐步 (v, ω)，按帧偏移与当前状态给出控制量。移植自 model repo 的 infer/controller.py。</summary>
public sealed class Controller
{
    private const double MinTurnRadiusM = 6.0;
    private const double SpeedFloorMps = 0.5;
    private const double SteerWindowFrames = 4.0;
    private const double CurvatureFeedback = 0.2;
    private const double SpeedPreviewFrames = 15.0;
    private const double AccelKi = 1.0;
    private const double AccelIntegralLimitMps2 = 0.95;
    private const double FullThrottleAccelMps2 = 1.0;
    private const double CoastDecelMps2 = 0.5;
    private const double FullBrakeDecelMps2 = 5.0;
    private const double Gravity = 9.81;

    private readonly double _frameS;
    private readonly double[] _speed;
    private readonly double[] _cumYaw;
    private double _integral;
    private double? _lastOffset;

    /// <param name="anchor">规划时刻的车辆状态。</param>
    /// <param name="speed">[H] 规划后第 i + 1 帧的车速。</param>
    /// <param name="yawRate">[H] 规划后第 i + 1 帧的横摆角速度。</param>
    /// <param name="freq">轨迹的频率，Hz。</param>
    public Controller(VehicleState anchor, double[] speed, double[] yawRate, double freq)
    {
        _frameS = 1 / freq;
        Horizon = speed.Length;
        var pad = (int)Math.Ceiling(SteerWindowFrames);
        _speed = Extend(anchor.Speed, speed, pad);
        _cumYaw = Cumulative(Extend(anchor.YawRate, yawRate, pad));
    }

    /// <summary>规划覆盖的帧数 H。</summary>
    public int Horizon { get; }

    /// <summary>规划后 offset 帧、处于 state 时应施加的控制量。</summary>
    public Command At(double offset, VehicleState state)
    {
        var error = Interp(_speed, offset + SpeedPreviewFrames) - state.Speed;
        if (_lastOffset is { } last)
        {
            var dt = Math.Max(offset - last, 0) * _frameS;
            _integral = Math.Clamp(_integral + AccelKi * error * dt, -AccelIntegralLimitMps2, AccelIntegralLimitMps2);
        }
        _lastOffset = offset;
        // 下坡时重力沿坡面的分量替车加速，需要的加速度随之减少
        var accel = error / (SpeedPreviewFrames * _frameS) - Gravity * Math.Sin(state.Pitch) + _integral;
        return new Command(Steer(offset, state), Pedal(accel));
    }

    private double Steer(double offset, VehicleState state)
    {
        var dyaw = Interp(_cumYaw, offset + SteerWindowFrames) - Interp(_cumYaw, offset);
        var speed = Math.CopySign(Math.Max(Math.Abs(state.Speed), SpeedFloorMps), state.Speed != 0 ? state.Speed : 1);
        var target = dyaw / (speed * SteerWindowFrames);
        var measured = state.YawRate / speed;
        var curvature = target + CurvatureFeedback * (target - measured);
        return Math.Clamp(MinTurnRadiusM * curvature, -1, 1);
    }

    private static double Pedal(double accel)
    {
        if (accel >= 0)
        {
            return Math.Min(1, accel / FullThrottleAccelMps2);
        }
        // 从滑行边界起刹，输出连续
        var braking = accel + CoastDecelMps2;
        return braking >= 0 ? 0 : Math.Max(-1, braking / FullBrakeDecelMps2);
    }

    /// <summary>[1 + rest + pad] 首项接 rest，末尾按末项重复 pad 个。</summary>
    private static double[] Extend(double first, double[] rest, int pad) =>
        [first, .. rest, .. Enumerable.Repeat(rest[^1], pad)];

    /// <summary>逐 knot 读数的梯形累积，首项为零。</summary>
    private static double[] Cumulative(double[] values)
    {
        var cumulative = new double[values.Length];
        for (var i = 1; i < values.Length; i++)
        {
            cumulative[i] = cumulative[i - 1] + (values[i] + values[i - 1]) / 2;
        }
        return cumulative;
    }

    /// <summary>knot 为 0..n-1 的线性插值，两端外按端点取值，同 np.interp。</summary>
    private static double Interp(double[] values, double x)
    {
        if (x <= 0)
        {
            return values[0];
        }
        if (x >= values.Length - 1)
        {
            return values[^1];
        }
        var i = (int)x;
        return values[i] + (values[i + 1] - values[i]) * (x - i);
    }
}
