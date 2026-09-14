using System.IO.MemoryMappedFiles;

namespace Etssd.Interface.Frames;

/// <summary>
/// 帧共享内存的写端。布局为 SlotCount 个 [u64 seq][BGRA8 pixels]，无 header，第 seq 帧写入 slot seq % SlotCount。
/// </summary>
/// <remarks>
/// 写 pixels 期间 slot 的 seq 为 0。读端拷贝前后读到的 seq 都等于通知中的 seq 才算有效。seq 单调递增不复用。
/// </remarks>
public sealed unsafe class FrameRing : IDisposable
{
    public const int ProtocolVersion = 1;
    public const string MappingName = @"Local\etssd.frames.v1";
    public const int SlotCount = 4;
    public const int Width = 1920;
    public const int Height = 1080;
    public const int FrameBytes = Width * Height * 4;
    public const int SlotBytes = sizeof(ulong) + FrameBytes;

    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;

    /// <summary>映射名可能仍被上次运行的读端持有，所以用 CreateOrOpen。单实例由 HTTP 端口保证。</summary>
    public FrameRing()
    {
        _map = MemoryMappedFile.CreateOrOpen(MappingName, (long)SlotCount * SlotBytes);
        _view = _map.CreateViewAccessor();
        byte* ptr = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _base = ptr;
    }

    /// <summary>作废 slot 并返回 pixels 区，写完必须调用 <see cref="EndWrite"/>。</summary>
    public Span<byte> BeginWrite(ulong seq)
    {
        var slot = SlotPtr(seq);
        *(ulong*)slot = 0;
        return new Span<byte>(slot + sizeof(ulong), FrameBytes);
    }

    public void EndWrite(ulong seq) => *(ulong*)SlotPtr(seq) = seq;

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _map.Dispose();
    }

    private byte* SlotPtr(ulong seq) => _base + (long)(seq % SlotCount) * SlotBytes;
}
