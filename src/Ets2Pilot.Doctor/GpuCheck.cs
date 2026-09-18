using Vortice.DXGI;

namespace Ets2Pilot.Doctor;

public sealed class GpuCheck : IDoctorCheck
{
    public string Name => "gpu";

    public CheckResult Run()
    {
        var adapters = new List<string>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            using (adapter)
            {
                var desc = adapter.Description1;
                if ((desc.Flags & AdapterFlags.Software) != 0)
                {
                    continue;
                }
                var vramMiB = (ulong)desc.DedicatedVideoMemory / (1024 * 1024);
                adapters.Add($"{desc.Description} ({vramMiB} MB)");
            }
        }
        return adapters.Count == 0
            ? new(CheckStatus.Failed, "未找到硬件显卡")
            : new(CheckStatus.Ok, string.Join("; ", adapters));
    }
}
