using System.Buffers.Binary;
using System.Text.Json;
using Etssd.Bridge.Http;
using Microsoft.ML.OnnxRuntime;

namespace Etssd.Inference;

/// <summary>窗口级编码 telemetry。模型在 metadata_props["telemetry"] 声明每个输入在遥测块中的偏移与类型。</summary>
public sealed class TelemetryEncoder(InferenceSession session) : IDisposable
{
    private readonly Dictionary<string, Field> _fields = JsonSerializer
        .Deserialize<Dictionary<string, Field>>(session.ModelMetadata.CustomMetadataMap["telemetry"], Json.Options)!;

    /// <param name="window">[T] 遥测块，由旧到新。</param>
    /// <returns>模型的输出，调用方释放。</returns>
    public IDisposableReadOnlyCollection<OrtValue> Forward(IReadOnlyList<byte[]> window)
    {
        var inputs = _fields.Values
            .Select(field => OrtValue.CreateTensorValueFromMemory(window.Select(field.Read).ToArray(), [window.Count]))
            .ToArray();
        try
        {
            using var run = new RunOptions();
            return session.Run(run, _fields.Keys, inputs, session.OutputNames);
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

    /// <summary>JSON 中为 snake_case：bool、int8、uint8 … float64。</summary>
    private enum ScalarType
    {
        Bool,
        Int8,
        Uint8,
        Int16,
        Uint16,
        Int32,
        Uint32,
        Int64,
        Uint64,
        Float32,
        Float64,
    }

    private sealed record Field(int Offset, ScalarType Type)
    {
        /// <summary>按声明的类型读出 Offset 处的值，转换为 float64。</summary>
        public double Read(byte[] telemetry)
        {
            var bytes = telemetry.AsSpan(Offset);
            return Type switch
            {
                ScalarType.Bool => bytes[0] != 0 ? 1 : 0,
                ScalarType.Int8 => (sbyte)bytes[0],
                ScalarType.Uint8 => bytes[0],
                ScalarType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
                ScalarType.Uint16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
                ScalarType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(bytes),
                ScalarType.Uint32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
                ScalarType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(bytes),
                ScalarType.Uint64 => BinaryPrimitives.ReadUInt64LittleEndian(bytes),
                ScalarType.Float32 => BinaryPrimitives.ReadSingleLittleEndian(bytes),
                ScalarType.Float64 => BinaryPrimitives.ReadDoubleLittleEndian(bytes),
                _ => throw new ArgumentOutOfRangeException(nameof(Type), Type, null),
            };
        }
    }
}
