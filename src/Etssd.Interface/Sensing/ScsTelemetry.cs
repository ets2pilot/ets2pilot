using System.IO.MemoryMappedFiles;

namespace Etssd.Interface.Sensing;

/// <summary>scs-sdk-plugin 的共享内存。映射在游戏加载插件后才出现，首次成功打开后一直持有。</summary>
public sealed class ScsTelemetry : IDisposable
{
    public const string MapName = @"Local\SCSTelemetry";
    public const int Size = 32 * 1024;

    private const int SimulationTimeOffset = 16;

    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;

    public bool TryOpen()
    {
        if (_view is not null)
        {
            return true;
        }
        try
        {
            _map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            _view = _map.CreateViewAccessor(0, Size, MemoryMappedFileAccess.Read);
            return true;
        }
        catch (FileNotFoundException)
        {
            _map?.Dispose();
            _map = null;
            return false;
        }
    }

    /// <summary>调用前须 <see cref="TryOpen"/> 成功。</summary>
    public bool SdkActive => View.ReadBoolean(0);

    /// <summary>SCS simulation_time，暂停时继续走。</summary>
    public ulong SimulationTimeUs => View.ReadUInt64(SimulationTimeOffset);

    public static ulong SimulationTimeUsOf(byte[] raw) => BitConverter.ToUInt64(raw, SimulationTimeOffset);

    /// <param name="unixNs">读取时刻，Unix epoch 纳秒。</param>
    public byte[] Snapshot(out long unixNs)
    {
        var raw = new byte[Size];
        unixNs = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100;
        View.ReadArray(0, raw, 0, Size);
        return raw;
    }

    public void Dispose()
    {
        _view?.Dispose();
        _map?.Dispose();
    }

    private MemoryMappedViewAccessor View =>
        _view ?? throw new InvalidOperationException("telemetry mapping is not open");
}
