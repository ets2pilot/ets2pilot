using System.Runtime.InteropServices;
using Etssd.Interface.Http;
using Etssd.Interface.Webhook;
using Microsoft.Extensions.Logging;

namespace Etssd.Interface.Sensing;

/// <summary>相机回正线程。存在 image+telemetry 订阅且 ETS2 在前台时，按固定间隔向前台窗口按 1 键。</summary>
/// <remarks>ETS2 默认键位下 1 键回正驾驶室相机。按键会送进任何前台窗口，前台检查不可省略。</remarks>
public sealed class CameraResetLoop(SubscriberRegistry registry, ILogger log)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private const ushort ScanCode1 = 0x02;

    private bool _failed;

    public Task RunAsync(CancellationToken ct) => DedicatedThread.RunAsync("etssd-camera-reset", () => Run(ct));

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
        var hwnd = Win32.FindWindowW(null, ScreenCapture.WindowTitle);
        return hwnd != IntPtr.Zero && Win32.GetForegroundWindow() == hwnd;
    }

    private void Press()
    {
        // ETS2 按扫描码识别键位，VirtualKey 留空
        Win32.Input[] inputs =
        [
            Keyboard(Win32.KeyEventScanCode),
            Keyboard(Win32.KeyEventScanCode | Win32.KeyEventKeyUp),
        ];
        var sent = Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.Input>());
        if (sent == inputs.Length)
        {
            _failed = false;
            return;
        }
        if (!_failed)
        {
            // 常见原因是 ETS2 以管理员运行而 interface 没有
            log.LogWarning("相机回正按键发送失败 (Win32 错误 {Error})", Marshal.GetLastWin32Error());
            _failed = true;
        }
    }

    private static Win32.Input Keyboard(uint flags) => new()
    {
        Type = Win32.InputKeyboard,
        Union = new Win32.InputUnion { Keyboard = new Win32.KeyboardInput { ScanCode = ScanCode1, Flags = flags } },
    };
}
