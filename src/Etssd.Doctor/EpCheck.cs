using System.Runtime.InteropServices;
using Etssd.Core;

namespace Etssd.Doctor;

/// <summary>随程序分发的 TensorRT-RTX EP 插件是否在场，NVIDIA 驱动是否满足插件的 CUDA 版本要求。</summary>
public sealed class EpCheck : IDoctorCheck
{
    public string Name => "ep";

    public CheckResult Run()
    {
        if (!File.Exists(TrtRtxEp.PluginPath))
        {
            return new(CheckStatus.Failed, $"缺少 {TrtRtxEp.PluginPath}，发布产物不完整");
        }
        int version;
        try
        {
            if (cuDriverGetVersion(out version) != 0)
            {
                return new(CheckStatus.Failed, "NVIDIA 驱动未报告 CUDA 版本", TrtRtxEp.DriverUrl);
            }
        }
        catch (DllNotFoundException)
        {
            return new(CheckStatus.Failed, "未安装 NVIDIA 驱动", TrtRtxEp.DriverUrl);
        }
        var cuda = $"{version / 1000}.{version % 1000 / 10}";
        var required = $"{TrtRtxEp.MinCudaDriverVersion / 1000}.{TrtRtxEp.MinCudaDriverVersion % 1000 / 10}";
        return version < TrtRtxEp.MinCudaDriverVersion
            ? new(CheckStatus.Failed, $"驱动支持 CUDA {cuda}，插件要求 {required}", TrtRtxEp.DriverUrl)
            : new(CheckStatus.Ok, $"驱动支持 CUDA {cuda}，插件 {TrtRtxEp.Dir}");
    }

    /// <summary>驱动 API，不需要 cuInit。返回 0 表示成功，version 编码为 1000 * major + 10 * minor。</summary>
    [DllImport("nvcuda")]
    private static extern int cuDriverGetVersion(out int version);
}
