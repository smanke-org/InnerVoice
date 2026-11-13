using Microsoft.ML.OnnxRuntime;

namespace InnerVoice.Core.Speech.Neural;

/// Opens the voice models with ONNX Runtime: on the graphics card through DirectML (DirectX 12)
/// when there is one, otherwise (or when DirectML can't load a model) on the CPU.
public static class Onnx
{
    /// Try the graphics card first. The app turns this off when the hardware check finds no
    /// DirectX 12 card; tests and Macs never have DirectML.
    public static bool PreferGpu { get; set; } = OperatingSystem.IsWindows();

    /// The DirectX adapter to use (0: the default; the app picks the one with the most video memory).
    public static int GpuDevice { get; set; }

    /// Where the last model opened runs, for Settings › Voices ("Graphics card" or "CPU").
    public static string LastDevice { get; private set; } = "CPU";

    public static InferenceSession Open(string path)
    {
        if (PreferGpu && OperatingSystem.IsWindows())
        {
            try
            {
                var gpu = new SessionOptions
                {
                    // DirectML wants these off (its docs).
                    EnableMemoryPattern = false,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                };
                gpu.AppendExecutionProvider_DML(GpuDevice);
                var s = new InferenceSession(path, gpu);
                LastDevice = "Graphics card";
                return s;
            }
            catch (Exception) { /* no DirectML, or an operator it can't run: the CPU below */ }
        }
        var cpu = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount - 1),
        };
        LastDevice = "CPU";
        return new InferenceSession(path, cpu);
    }

    public static OrtValue Floats(float[] data, params long[] shape) => OrtValue.CreateTensorValueFromMemory(data, shape);
    public static OrtValue Longs(long[] data, params long[] shape) => OrtValue.CreateTensorValueFromMemory(data, shape);

    /// Runs a model with named inputs; the caller disposes the outputs.
    public static IDisposableReadOnlyCollection<OrtValue> Run(InferenceSession s, IReadOnlyDictionary<string, OrtValue> inputs, IReadOnlyList<string>? outputs = null)
    {
        using var options = new RunOptions();
        var names = inputs.Keys.ToArray();
        var values = names.Select(n => inputs[n]).ToArray();
        return s.Run(options, names, values, outputs ?? s.OutputNames);
    }

    public static long[] Shape(OrtValue v) => v.GetTensorTypeAndShape().Shape;

    /// A description of a model's inputs and outputs (for tools/VoiceBench --probe).
    public static string Describe(InferenceSession s)
    {
        static string Dims(NodeMetadata m) => m.IsTensor ? $"{m.ElementDataType} [{string.Join(",", m.Dimensions)}] {string.Join(",", m.SymbolicDimensions)}" : m.OnnxValueType.ToString();
        var lines = s.InputMetadata.Select(kv => $"  in  {kv.Key}: {Dims(kv.Value)}")
            .Concat(s.OutputMetadata.Select(kv => $"  out {kv.Key}: {Dims(kv.Value)}"));
        return string.Join('\n', lines);
    }
}
