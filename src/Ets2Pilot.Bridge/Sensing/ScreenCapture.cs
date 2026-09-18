using System.Diagnostics;
using System.Runtime.InteropServices;
using Ets2Pilot.Bridge.Frames;
using Ets2Pilot.Core;
using Microsoft.Extensions.Logging;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.UI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.HiDpi;
using WinRT;

namespace Ets2Pilot.Bridge.Sensing;

/// <summary>
/// 用 Windows.Graphics.Capture 截取 ETS2 窗口的 client 区域，经 D3D11 Video Processor 缩放到 <see cref="FrameRing"/> 的分辨率后写入 slot。
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
        PInvoke.SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }

    /// <summary>游戏窗口是否存在。截图与 telemetry 映射都只在窗口出现后可用。</summary>
    public static bool IsWindowPresent() => !PInvoke.FindWindow(null, WindowTitle).IsNull;

    /// <summary>把上次写入之后的新窗口图像写入第 seq 帧。窗口不可截或 maxWait 内没有新图像时返回 false 且不改动 ring。</summary>
    public bool TryCapture(FrameRing ring, ulong seq, TimeSpan maxWait)
    {
        var hwnd = PInvoke.FindWindow(null, WindowTitle);
        if (hwnd.IsNull)
        {
            Reject("未找到 ETS2 窗口");
            return false;
        }
        PInvoke.GetClientRect(hwnd, out var rect);
        var (width, height) = (rect.Width, rect.Height);
        if (width * 9 != height * 16)
        {
            Reject($"ETS2 窗口 {width}x{height} 不是 16:9");
            return false;
        }
        if (_pipeline is null || _pipeline.Hwnd != hwnd || _pipeline.Width != width || _pipeline.Height != height)
        {
            _pipeline?.Dispose();
            _pipeline = new Pipeline(hwnd, width, height);
        }
        using var frame = _pipeline.TakeFrame(maxWait);
        if (frame is null)
        {
            Reject($"{maxWait.TotalMilliseconds:F0}ms 内没有新的窗口图像");
            return false;
        }
        // 窗口帧的原点是 DWMWA_EXTENDED_FRAME_BOUNDS 的左上角，含标题栏与边框
        var bounds = new RECT();
        PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, MemoryMarshal.AsBytes(new Span<RECT>(ref bounds)));
        var origin = new System.Drawing.Point();
        PInvoke.ClientToScreen(hwnd, ref origin);
        var source = new RawRect(origin.X - bounds.left, origin.Y - bounds.top, origin.X - bounds.left + width, origin.Y - bounds.top + height);
        _pipeline.Write(frame, source, ring, seq);
        if (_lastRejection is not null)
        {
            _log.LogInformation(AppEvents.UserVisible, "开始截图 {Width}x{Height}", width, height);
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
            _log.LogInformation(AppEvents.UserVisible, "跳过截图: {Reason}", reason);
            _lastRejection = reason;
        }
    }

    /// <summary>绑定到一个窗口及其 client 尺寸的 capture session 与 D3D11 资源，任一变化时整体重建。</summary>
    private sealed class Pipeline : IDisposable
    {
        private readonly AutoResetEvent _arrived = new(false);
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly ID3D11VideoContext _videoContext;
        private readonly IDirect3DDevice _winrtDevice;
        private readonly Direct3D11CaptureFramePool _pool;
        private readonly GraphicsCaptureSession _session;
        private readonly ID3D11Texture2D _client;
        private readonly ID3D11VideoProcessorEnumerator _enumerator;
        private readonly ID3D11VideoProcessor _processor;
        private readonly ID3D11VideoProcessorInputView _inputView;
        private readonly ID3D11Texture2D _scaled;
        private readonly ID3D11VideoProcessorOutputView _outputView;
        private readonly ID3D11Texture2D _staging;
        private Direct3D11CaptureFrame? _latest;

        public Pipeline(IntPtr hwnd, int width, int height)
        {
            Hwnd = hwnd;
            Width = width;
            Height = height;
            D3D11.D3D11CreateDevice(null, DriverType.Hardware,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_0], out _device!).CheckError();
            _context = _device.ImmediateContext;
            _videoContext = _context.QueryInterface<ID3D11VideoContext>();
            using var videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
            using (var dxgiDevice = _device.QueryInterface<IDXGIDevice>())
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable));
                using var _ = new ComObject(inspectable);
                _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            }

            var item = GraphicsCaptureItem.TryCreateFromWindowId(new WindowId((ulong)hwnd))
                ?? throw new InvalidOperationException("无法为 ETS2 窗口创建 GraphicsCaptureItem");
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            _pool.FrameArrived += OnFrameArrived;
            _session = _pool.CreateCaptureSession(item);
            _session.IsCursorCaptureEnabled = false;
            _session.IsBorderRequired = false;
            // 默认 16ms，高刷新率下会丢掉大部分帧
            _session.MinUpdateInterval = TimeSpan.FromMilliseconds(1);
            _session.StartCapture();

            _client = _device.CreateTexture2D(new Texture2DDescription(
                Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget));
            _scaled = _device.CreateTexture2D(new Texture2DDescription(
                Format.B8G8R8A8_UNorm, FrameRing.Width, FrameRing.Height, 1, 1, BindFlags.RenderTarget));
            _staging = _device.CreateTexture2D(new Texture2DDescription(
                Format.B8G8R8A8_UNorm, FrameRing.Width, FrameRing.Height, 1, 1, BindFlags.None,
                ResourceUsage.Staging, CpuAccessFlags.Read));

            var content = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputWidth = (uint)width,
                InputHeight = (uint)height,
                OutputWidth = FrameRing.Width,
                OutputHeight = FrameRing.Height,
                Usage = VideoUsage.PlaybackNormal,
            };
            _enumerator = videoDevice.CreateVideoProcessorEnumerator(content);
            _processor = videoDevice.CreateVideoProcessor(_enumerator, 0);
            _inputView = videoDevice.CreateVideoProcessorInputView(_client, _enumerator, new VideoProcessorInputViewDescription
            {
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            });
            _outputView = videoDevice.CreateVideoProcessorOutputView(_scaled, _enumerator, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            });

            var full = new RawRect(0, 0, FrameRing.Width, FrameRing.Height);
            _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, full);
            _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, full);
        }

        public IntPtr Hwnd { get; }

        public int Width { get; }

        public int Height { get; }

        /// <summary>取走上次调用之后到达的最新一帧，没有时最多等待 maxWait。调用方负责 Dispose 返回的帧。</summary>
        public Direct3D11CaptureFrame? TakeFrame(TimeSpan maxWait)
        {
            var start = Stopwatch.GetTimestamp();
            while (true)
            {
                if (Interlocked.Exchange(ref _latest, null) is { } frame)
                {
                    return frame;
                }
                var remaining = maxWait - Stopwatch.GetElapsedTime(start);
                if (remaining <= TimeSpan.Zero || !_arrived.WaitOne(remaining))
                {
                    return null;
                }
            }
        }

        /// <param name="source">client 区域在 frame 中的像素矩形。</param>
        public unsafe void Write(Direct3D11CaptureFrame frame, RawRect source, FrameRing ring, ulong seq)
        {
            // 帧池纹理轮换使用，拷进固定纹理后才能绑定 Video Processor 的 input view
            using (var surface = new ComObject(MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface)))
            using (var access = surface.QueryInterface<IDirect3DDxgiInterfaceAccess>())
            using (var texture = access.GetInterface<ID3D11Texture2D>())
            {
                _context.CopySubresourceRegion(_client, 0, 0, 0, 0, texture, 0,
                    new Box(source.Left, source.Top, 0, source.Right, source.Bottom, 1));
            }
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
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }
        }

        public void Dispose()
        {
            _session.Dispose();
            _pool.Dispose();
            Interlocked.Exchange(ref _latest, null)?.Dispose();
            _arrived.Dispose();
            _staging.Dispose();
            _outputView.Dispose();
            _scaled.Dispose();
            _inputView.Dispose();
            _processor.Dispose();
            _enumerator.Dispose();
            _client.Dispose();
            _winrtDevice.Dispose();
            _videoContext.Dispose();
            _context.Dispose();
            _device.Dispose();
        }

        [DllImport("d3d11.dll")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        /// <remarks>帧池只有 2 个缓冲，不及时取走时新帧被丢弃，所以在回调线程上随到随取、只留最新一帧。</remarks>
        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            if (sender.TryGetNextFrame() is not { } frame)
            {
                return;
            }
            Interlocked.Exchange(ref _latest, frame)?.Dispose();
            _arrived.Set();
        }
    }
}
