using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Ets2Pilot.Inference;

/// <summary>解码速度与角速度轨迹。模型在 metadata_props 声明需要的帧数 frames 与频率 freq。</summary>
public sealed class Decoder(InferenceSession session) : IDisposable
{
    public int Frames { get; } = int.Parse(session.ModelMetadata.CustomMetadataMap["frames"], CultureInfo.InvariantCulture);

    /// <summary>Hz。</summary>
    public double Freq { get; } = double.Parse(session.ModelMetadata.CustomMetadataMap["freq"], CultureInfo.InvariantCulture);

    /// <param name="telemetry">telemetry encoder 的全部输出。</param>
    /// <param name="images">[T] 逐帧的 image encoder 输出，由旧到新，按输出逐个 collate 为 [T, ...]。</param>
    /// <returns>各 [H]，速度与角速度。</returns>
    public (double[] Speed, double[] YawRate) Forward(
        IReadOnlyList<OrtValue> telemetry, IReadOnlyList<IReadOnlyList<OrtValue>> images)
    {
        var collated = Enumerable.Range(0, images[0].Count)
            .Select(i => Collate([.. images.Select(frame => frame[i])]))
            .ToArray();
        try
        {
            using var run = new RunOptions();
            using var outputs = session.Run(run, session.InputNames, [.. telemetry, .. collated], session.OutputNames);
            return (ToDoubles(outputs[0]), ToDoubles(outputs[1]));
        }
        finally
        {
            foreach (var value in collated)
            {
                value.Dispose();
            }
        }
    }

    public void Dispose() => session.Dispose();

    private static OrtValue Collate(IReadOnlyList<OrtValue> frames)
    {
        var info = frames[0].GetTensorTypeAndShape();
        var collated = OrtValue.CreateAllocatedTensorValue(
            OrtAllocator.DefaultInstance, info.ElementDataType, [frames.Count, .. info.Shape]);
        var target = collated.GetTensorMutableRawData();
        for (var i = 0; i < frames.Count; i++)
        {
            var source = frames[i].GetTensorMutableRawData();
            source.CopyTo(target[(i * source.Length)..]);
        }
        return collated;
    }

    private static double[] ToDoubles(OrtValue value) =>
        value.GetTensorTypeAndShape().ElementDataType == TensorElementType.Double
            ? value.GetTensorDataAsSpan<double>().ToArray()
            : Array.ConvertAll(value.GetTensorDataAsSpan<float>().ToArray(), v => (double)v);
}
