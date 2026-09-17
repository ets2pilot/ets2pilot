using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Etssd.Inference;

/// <summary>键盘激活事件。ETS2 在前台时轮询 Q、W、S，调用 <see cref="AutopilotSwitch"/> 的转移函数。</summary>
/// <remarks>
/// W 与 S 是 ETS2 默认的油门与刹车，按键只读取不拦截。
/// Q 在锁定与解锁之间切换，解锁后立即开启自动驾驶。锁定供键盘人工驾驶使用，踩油门不会开启自动驾驶。
/// </remarks>
public sealed class AutopilotKeys(AutopilotSwitch autopilot)
{
    /// <summary>自动驾驶开启时 S 连续按住该时长后关闭。</summary>
    public static readonly TimeSpan DisengageHold = TimeSpan.FromSeconds(0.5);

    private const string GameWindowTitle = "Euro Truck Simulator 2";

    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);

    public Task RunAsync(CancellationToken ct) => Task.Factory.StartNew(() => Run(ct), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken ct)
    {
        var qWasDown = false;
        var wWasDown = false;
        long? sDownSinceMs = null;
        while (!ct.WaitHandle.WaitOne(Interval))
        {
            var foreground = IsGameForeground();
            var q = foreground && IsDown(VIRTUAL_KEY.VK_Q);
            var w = foreground && IsDown(VIRTUAL_KEY.VK_W);
            var s = foreground && IsDown(VIRTUAL_KEY.VK_S);
            var now = Environment.TickCount64;
            sDownSinceMs = s ? sDownSinceMs ?? now : null;

            var engaged = false;
            if (q && !qWasDown)
            {
                if (autopilot.Unlock())
                {
                    engaged = autopilot.Engage();
                }
                else
                {
                    autopilot.Lock();
                }
            }
            else if (w && !wWasDown)
            {
                engaged = autopilot.Engage();
            }
            else if (sDownSinceMs is { } since && now - since >= DisengageHold.TotalMilliseconds)
            {
                autopilot.Disengage();
            }
            // 自动驾驶开启之前已按住的 S 不计入长按
            if (engaged && s)
            {
                sDownSinceMs = now;
            }
            qWasDown = q;
            wWasDown = w;
        }
    }

    private static bool IsGameForeground()
    {
        var hwnd = PInvoke.FindWindow(null, GameWindowTitle);
        return !hwnd.IsNull && PInvoke.GetForegroundWindow() == hwnd;
    }

    private static bool IsDown(VIRTUAL_KEY key) => PInvoke.GetAsyncKeyState((int)key) < 0;
}
