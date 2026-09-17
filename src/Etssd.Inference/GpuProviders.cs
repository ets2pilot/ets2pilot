using Etssd.Core;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace Etssd.Inference;

/// <summary>按 EP 建 session。</summary>
internal static class GpuProviders
{
    private const string TensorRtRtx = "NvTensorRTRTXExecutionProvider";

    private static readonly Lock RegisterLock = new();

    private static bool _registered;

    /// <summary>注册 TensorRT-RTX EP 插件，进程内只执行一次。</summary>
    /// <exception cref="FileNotFoundException">插件文件不存在。</exception>
    public static void Register(ILogger log)
    {
        lock (RegisterLock)
        {
            if (_registered)
            {
                return;
            }
            if (!File.Exists(TrtRtxEp.PluginPath))
            {
                throw new FileNotFoundException("缺少 TensorRT-RTX EP 插件", TrtRtxEp.PluginPath);
            }
            OrtEnv.Instance().RegisterExecutionProviderLibrary(TensorRtRtx, TrtRtxEp.PluginPath);
            _registered = true;
            log.LogInformation(AppEvents.UserVisible, "EP 已注册: {Plugin}", TrtRtxEp.PluginPath);
        }
    }

    public static InferenceSession CreateDirectMlSession(string modelPath)
    {
        using var options = new SessionOptions
        {
            // DirectML EP 要求关闭 memory pattern 并顺序执行
            EnableMemoryPattern = false,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        options.AppendExecutionProvider_DML(0);
        return new InferenceSession(modelPath, options);
    }

    /// <summary>建 TensorRT-RTX 的 session，编译结果经 <see cref="EngineCache"/> 复用。</summary>
    /// <exception cref="InvalidOperationException">TensorRT-RTX 未发现可用 GPU。</exception>
    public static InferenceSession CreateTensorRtRtxSession(string modelPath, ILogger log)
    {
        var devices = OrtEnv.Instance().GetEpDevices().Where(d => d.EpName == TensorRtRtx).ToList();
        if (devices.Count == 0)
        {
            throw new InvalidOperationException("TensorRT-RTX 未发现可用 GPU");
        }
        var cached = EngineCache.PathFor(modelPath);
        if (File.Exists(cached))
        {
            try
            {
                return Open(cached, devices, writeTo: null);
            }
            catch (OnnxRuntimeException ex)
            {
                log.LogWarning(AppEvents.UserVisible, "{Cached} 不可用，重新编译: {Message}", Path.GetFileName(cached), ex.Message);
            }
        }
        log.LogInformation(AppEvents.UserVisible, "正在编译 {Model}", Path.GetFileName(modelPath));
        return Open(modelPath, devices, EngineCache.Discard(cached, log) ? cached : null);
    }

    /// <param name="writeTo">编译结果写成 EP context model 的路径，null 则只编译不写。</param>
    private static InferenceSession Open(string modelPath, List<OrtEpDevice> devices, string? writeTo)
    {
        using var options = new SessionOptions();
        options.AppendExecutionProvider(OrtEnv.Instance(), devices, new Dictionary<string, string>
        {
            // EP context model 加载后仍有 JIT 阶段，JIT 内核缓存在此目录
            ["nv_runtime_cache_path"] = EngineCache.Dir,
        });
        if (writeTo is not null)
        {
            options.AddSessionConfigEntry("ep.context_enable", "1");
            options.AddSessionConfigEntry("ep.context_file_path", writeTo);
            // engine 嵌进 onnx，一个模型一个缓存文件
            options.AddSessionConfigEntry("ep.context_embed_mode", "1");
        }
        return new InferenceSession(modelPath, options);
    }
}
