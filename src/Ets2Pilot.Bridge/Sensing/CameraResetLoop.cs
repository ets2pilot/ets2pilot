using System.Runtime.InteropServices;
using Ets2Pilot.Bridge.Http;
using Ets2Pilot.Bridge.Webhook;
using Ets2Pilot.Core;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Ets2Pilot.Bridge.Sensing;

/// <summary>相机回正线程。存在 image+telemetry 订阅且 ETS2 在前台时，按固定间隔向前台窗口按 1 键。</summary>
/// <remarks>ETS2 默认键位下 1 键回正驾驶室相机。按键会送进任何前台窗口，前台检查不可省略。</remarks>
public sealed class CameraResetLoop(SubscriberRegistry registry, ILogger log)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private const ushort ScanCode1 = 0x02;

    private bool _failed;

    public Task RunAsync(CancellationToken ct) => Task.Factory.StartNew(() => Run(ct), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken ct)
    {
        while (!ct.WaitHandle.WaitOne(Interval))
        {
            if (registry.Snapshot.Any(s => s.Request.Type == SubscriptionType.ImageTelemetry) && IsEts2Foreground())
            {
                Press();
            }
        }
    }

    private static bool IsEts2Foreground()
    {
        var hwnd = PInvoke.FindWindow(null, ScreenCapture.WindowTitle);
        return !hwnd.IsNull && PInvoke.GetForegroundWindow() == hwnd;
    }

    private void Press()
    {
        // ETS2 按扫描码识别键位，VirtualKey 留空
        INPUT[] inputs =
        [
            Keyboard(KEYBD_EVENT_FLAGS.KEYEVENTF_SCANCODE),
            Keyboard(KEYBD_EVENT_FLAGS.KEYEVENTF_SCANCODE | KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP),
        ];
        var sent = PInvoke.SendInput(inputs, Marshal.SizeOf<INPUT>());
        if (sent == inputs.Length)
        {
            _failed = false;
            return;
        }
        if (!_failed)
        {
            // 常见原因是 ETS2 以管理员运行而 bridge 没有
            log.LogWarning(AppEvents.UserVisible, "相机回正按键发送失败 (Win32 错误 {Error})", Marshal.GetLastWin32Error());
            _failed = true;
        }
    }

    private static INPUT Keyboard(KEYBD_EVENT_FLAGS flags) => new()
    {
        type = INPUT_TYPE.INPUT_KEYBOARD,
        Anonymous = new INPUT._Anonymous_e__Union { ki = new KEYBDINPUT { wScan = ScanCode1, dwFlags = flags } },
    };
}
