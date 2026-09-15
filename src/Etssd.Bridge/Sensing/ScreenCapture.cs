using System.Diagnostics;
using Etssd.Bridge.Frames;
using Microsoft.Extensions.Logging;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Etssd.Bridge.Sensing;

/// <summary>
/// 截取 ETS2 窗口 client 区域当前的屏幕图像，经 D3D11 Video Processor 缩放到 <see cref="FrameRing"/> 的分辨率后写入 slot。
/// </summary>
/// <remarks>必须在单一线程上构造与调用，构造时把该线程设为 per-monitor DPI aware 以取得物理像素坐标。</remarks>
public sealed class ScreenCapture : IDisposable
{
    public const string WindowTitle = "Euro Truck Simulator 2";

    private readonly ILogger _log;
    private Pipeline? _pipeline;
    private string? _lastRejection;

    public ScreenCapture(ILogger log)
    {
        _log = log;
        Win32.SetThreadDpiAwarenessContext(Win32.DpiAwarenessPerMonitorV2);
    }

    /// <summary>把上次写入之后的新屏幕图像写入第 seq 帧。窗口不可截或 maxWait 内没有新图像时返回 false 且不改动 ring。</summary>
    public bool TryCapture(FrameRing ring, ulong seq, TimeSpan maxWait)
    {
        if (!TryLocateWindow(out var monitor, out var client, out var rejection))
        {
            Reject(rejection);
            return false;
        }
        if (_pipeline is null || _pipeline.Monitor != monitor)
        {
            _pipeline?.Dispose();
            _pipeline = Pipeline.Create(monitor);
        }
        var output = _pipeline.DesktopRect;
        if (client.Left < output.Left || client.Top < output.Top ||
            client.Right > output.Right || client.Bottom > output.Bottom)
        {
            Reject("ETS2 窗口跨越多个显示器");
            return false;
        }
        switch (_pipeline.Refresh(maxWait))
        {
            case RefreshResult.AccessLost:
                _pipeline.Dispose();
                _pipeline = null;
                Reject("桌面复制失效，重建中");
                return false;
            case RefreshResult.Stale:
                Reject($"{maxWait.TotalMilliseconds:F0}ms 内没有新的桌面图像");
                return false;
        }
        var source = new RawRect(
            client.Left - output.Left, client.Top - output.Top,
            client.Right - output.Left, client.Bottom - output.Top);
        _pipeline.Write(source, ring, seq);
        if (_lastRejection is not null)
        {
            _log.LogInformation("开始截图 {Width}x{Height}", source.Right - source.Left, source.Bottom - source.Top);
            _lastRejection = null;
        }
        return true;
    }

    public void Dispose()
    {
        _pipeline?.Dispose();
        _pipeline = null;
    }

    private void Reject(string reason)
    {
        if (reason != _lastRejection)
        {
            _log.LogInformation("跳过截图: {Reason}", reason);
            _lastRejection = reason;
        }
    }

    private static bool TryLocateWindow(out IntPtr monitor, out RawRect client, out string rejection)
    {
        monitor = IntPtr.Zero;
        client = default;
        var hwnd = Win32.FindWindowW(null, WindowTitle);
        if (hwnd == IntPtr.Zero)
        {
            rejection = "未找到 ETS2 窗口";
            return false;
        }
        // 与 dataset 一致，窗口不在前台时画面可能被遮挡
        if (Win32.IsIconic(hwnd) || Win32.GetForegroundWindow() != hwnd)
        {
            rejection = "ETS2 窗口不在前台";
            return false;
        }
        Win32.GetClientRect(hwnd, out var rect);
        var origin = new Win32.Point();
        Win32.ClientToScreen(hwnd, ref origin);
        var (width, height) = (rect.Right - rect.Left, rect.Bottom - rect.Top);
        if (width <= 0 || width * 9 != height * 16)
        {
            rejection = $"ETS2 窗口 {width}x{height} 不是 16:9";
            return false;
        }
        monitor = Win32.MonitorFromWindow(hwnd, Win32.MonitorDefaultToNull);
        client = new RawRect(origin.X, origin.Y, origin.X + width, origin.Y + height);
        rejection = "";
        return monitor != IntPtr.Zero;
    }

    private enum RefreshResult
    {
        Fresh,
        Stale,
        AccessLost,
    }

    /// <summary>绑定到一个显示器的 D3D11 资源，显示器或桌面模式变化时整体重建。</summary>
    private sealed class Pipeline : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly ID3D11VideoContext1 _videoContext;
        private readonly IDXGIOutputDuplication _duplication;
        private readonly ID3D11Texture2D _desktop;
        private readonly ID3D11VideoProcessorEnumerator _enumerator;
        private readonly ID3D11VideoProcessor _processor;
        private readonly ID3D11VideoProcessorInputView _inputView;
        private readonly ID3D11Texture2D _scaled;
        private readonly ID3D11VideoProcessorOutputView _outputView;
        private readonly ID3D11Texture2D _staging;
        private bool _fresh; // _desktop 含上次 Write 之后的桌面更新

        private Pipeline(IntPtr monitor, RawRect desktopRect, IDXGIAdapter1 adapter, IDXGIOutput1 output)
        {
            Monitor = monitor;
            DesktopRect = desktopRect;
            D3D11.D3D11CreateDevice(
                adapter, DriverType.Unknown,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_0], out _device!).CheckError();
            _context = _device.ImmediateContext;
            _videoContext = _context.QueryInterface<ID3D11VideoContext1>();
            using var videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
            _duplication = output.DuplicateOutput(_device);

            var (inWidth, inHeight) = (desktopRect.Right - desktopRect.Left, desktopRect.Bottom - desktopRect.Top);
            _desktop = _device.CreateTexture2D(new Texture2DDescription(
                Format.B8G8R8A8_UNorm, (uint)inWidth, (uint)inHeight, 1, 1, BindFlags.RenderTarget));
            _scaled = _device.CreateTexture2D(new Texture2DDescription(
                Format.B8G8R8A8_UNorm, FrameRing.Width, FrameRing.Height, 1, 1, BindFlags.RenderTarget));
            _staging = _device.CreateTexture2D(new Texture2DDescription(
                Format.B8G8R8A8_UNorm, FrameRing.Width, FrameRing.Height, 1, 1, BindFlags.None,
                ResourceUsage.Staging, CpuAccessFlags.Read));

            var content = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputWidth = (uint)inWidth,
                InputHeight = (uint)inHeight,
                OutputWidth = FrameRing.Width,
                OutputHeight = FrameRing.Height,
                Usage = VideoUsage.PlaybackNormal,
            };
            _enumerator = videoDevice.CreateVideoProcessorEnumerator(content);
            var support = _enumerator.CheckVideoProcessorFormat(Format.B8G8R8A8_UNorm);
            if (!support.HasFlag(VideoProcessorFormatSupport.Input) || !support.HasFlag(VideoProcessorFormatSupport.Output))
            {
                throw new NotSupportedException("显卡的 Video Processor 不支持 BGRA 输入输出");
            }
            _processor = videoDevice.CreateVideoProcessor(_enumerator, 0);
            _inputView = videoDevice.CreateVideoProcessorInputView(_desktop, _enumerator, new VideoProcessorInputViewDescription
            {
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            });
            _outputView = videoDevice.CreateVideoProcessorOutputView(_scaled, _enumerator, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            });

            var full = new RawRect(0, 0, FrameRing.Width, FrameRing.Height);
            _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
            _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
            _videoContext.VideoProcessorSetStreamColorSpace1(_processor, 0, ColorSpaceType.RgbFullG22NoneP709);
            _videoContext.VideoProcessorSetOutputColorSpace1(_processor, ColorSpaceType.RgbFullG22NoneP709);
            _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, full);
            _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, full);
        }

        public IntPtr Monitor { get; }

        /// <summary>显示器在虚拟桌面中的物理像素坐标。</summary>
        public RawRect DesktopRect { get; }

        public static Pipeline Create(IntPtr monitor)
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
            {
                using (adapter)
                {
                    for (uint j = 0; adapter.EnumOutputs(j, out var output).Success; j++)
                    {
                        using (output)
                        {
                            var desc = output.Description;
                            if (desc.Monitor != monitor)
                            {
                                continue;
                            }
                            using var output1 = output.QueryInterface<IDXGIOutput1>();
                            return new Pipeline(monitor, desc.DesktopCoordinates, adapter, output1);
                        }
                    }
                }
            }
            throw new InvalidOperationException("ETS2 窗口所在显示器没有对应的 DXGI output");
        }

        /// <summary>把桌面复制中尚未取走的更新拷入 _desktop。上次 <see cref="Write"/> 之后还没有更新时最多等待 maxWait。</summary>
        /// <remarks>
        /// 积压的更新在下一次 present 时才合并成一帧交付，新帧生成期间 AcquireNextFrame(0) 返回 WaitTimeout，
        /// 所以 0 超时不能用来判断有无新图像。
        /// </remarks>
        public RefreshResult Refresh(TimeSpan maxWait)
        {
            var start = Stopwatch.GetTimestamp();
            while (true)
            {
                var remaining = maxWait - Stopwatch.GetElapsedTime(start);
                // uint.MaxValue 表示 INFINITE
                var timeoutMs = _fresh || remaining <= TimeSpan.Zero
                    ? 0u
                    : (uint)Math.Ceiling(Math.Min(remaining.TotalMilliseconds, uint.MaxValue - 1));
                var result = _duplication.AcquireNextFrame(timeoutMs, out var info, out var frame);
                if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    return _fresh ? RefreshResult.Fresh : RefreshResult.Stale;
                }
                if (result == Vortice.DXGI.ResultCode.AccessLost)
                {
                    return RefreshResult.AccessLost;
                }
                result.CheckError();
                try
                {
                    // 只有鼠标指针变化的更新不带新的桌面图像
                    if (info.LastPresentTime != 0)
                    {
                        using var texture = frame.QueryInterface<ID3D11Texture2D>();
                        _context.CopyResource(_desktop, texture);
                        _fresh = true;
                    }
                }
                finally
                {
                    frame.Dispose();
                    _duplication.ReleaseFrame();
                }
            }
        }

        public unsafe void Write(RawRect source, FrameRing ring, ulong seq)
        {
            _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, source);
            _videoContext.VideoProcessorBlt(_processor, _outputView, 0, 1,
                [new VideoProcessorStream { Enable = true, InputSurface = _inputView }]);
            _context.CopyResource(_staging, _scaled);

            var mapped = _context.Map(_staging, 0, MapMode.Read);
            try
            {
                var pixels = ring.BeginWrite(seq);
                var rowBytes = FrameRing.Width * 4;
                for (var y = 0; y < FrameRing.Height; y++)
                {
                    // RowPitch 可能大于 Width * 4，逐行去掉 padding
                    new ReadOnlySpan<byte>((byte*)mapped.DataPointer + (long)y * mapped.RowPitch, rowBytes)
                        .CopyTo(pixels.Slice(y * rowBytes, rowBytes));
                }
                ring.EndWrite(seq);
                _fresh = false;
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }
        }

        public void Dispose()
        {
            _staging.Dispose();
            _outputView.Dispose();
            _scaled.Dispose();
            _inputView.Dispose();
            _processor.Dispose();
            _enumerator.Dispose();
            _desktop.Dispose();
            _duplication.Dispose();
            _videoContext.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
    }
}
