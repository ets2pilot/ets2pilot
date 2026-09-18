using System.Text.Json;
using Ets2Pilot.Bridge.Http;
using Microsoft.ML.OnnxRuntime;

namespace Ets2Pilot.Inference;

/// <summary>整帧上的一块像素矩形。</summary>
public sealed record Region(int Left, int Top, int Width, int Height);

/// <summary>单帧编码全部区域。模型在 metadata_props["regions"] 声明区域名到像素矩形。</summary>
public sealed class ImageEncoder(InferenceSession session) : IDisposable
{
    public IReadOnlyDictionary<string, Region> Regions { get; } = JsonSerializer
        .Deserialize<Dictionary<string, Region>>(session.ModelMetadata.CustomMetadataMap["regions"], Json.Options)!;

    /// <param name="crops">区域名到该区域的 RGB 平面排列 uint8 [3, height, width]。</param>
    /// <returns>模型的输出，调用方释放。</returns>
    public IDisposableReadOnlyCollection<OrtValue> Forward(IReadOnlyDictionary<string, byte[]> crops)
    {
        var names = Regions.Keys.ToArray();
        var inputs = names
            .Select(name => OrtValue.CreateTensorValueFromMemory(crops[name], [3, Regions[name].Height, Regions[name].Width]))
            .ToArray();
        try
        {
            using var run = new RunOptions();
            return session.Run(run, names, inputs, session.OutputNames);
        }
        finally
        {
            foreach (var input in inputs)
            {
                input.Dispose();
            }
        }
    }

    public void Dispose() => session.Dispose();
}
