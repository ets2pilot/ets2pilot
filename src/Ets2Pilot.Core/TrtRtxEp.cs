namespace Ets2Pilot.Core;

/// <summary>随程序分发的 TensorRT-RTX EP 插件，由 Ets2Pilot.Inference.csproj 在构建时取得并复制到输出目录。</summary>
public static class TrtRtxEp
{
    public const string DriverUrl = "https://www.nvidia.com/drivers";

    /// <summary>cu13 构建的插件要求驱动支持 CUDA 13.0，cuDriverGetVersion 的编码。</summary>
    public const int MinCudaDriverVersion = 13000;

    public static string Dir { get; } = Path.Combine(AppContext.BaseDirectory, "ep", "trtrtx");

    public static string PluginPath { get; } = Path.Combine(Dir, "onnxruntime_providers_nv_tensorrt_rtx.dll");
}
