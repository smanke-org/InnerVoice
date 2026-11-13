// VoiceBench: measures the local voices on this computer (README.md beside this file).
//   VoiceBench --hardware                       what the hardware check sees, and its verdicts
//   VoiceBench --bench <Voice Models> <out>     speaks a few lines with Qwen3-TTS and Orpheus
//   VoiceBench --probe <model.onnx>…           a model's inputs and outputs
//   VoiceBench --check <file.wav>…             how much of a file is voiced, and its pitch
//   VoiceBench --seeds <Voice Models> <n>      how steadily a description sets the voice's pitch

using System.Diagnostics;
using InnerVoice.Core;
using InnerVoice.Core.Speech;
using InnerVoice.Core.Speech.Neural;

var hw = VoiceHardwareCheck.Detect();

switch (args)
{
    case ["--hardware"]:
        Console.WriteLine(hw);
        foreach (var e in new[] { VoiceEngine.Qwen, VoiceEngine.Orpheus })
            Console.WriteLine($"{e.Title()}: {VoiceHardwareCheck.Fit(hw, e)}. {VoiceHardwareCheck.Warning(hw, e) ?? "No warning."}");
        break;

    case ["--probe", .. var files]:
        foreach (var f in files)
        {
            using var s = Onnx.Open(f);
            Console.WriteLine($"{Path.GetFileName(f)} ({Onnx.LastDevice})\n{Onnx.Describe(s)}");
        }
        break;

    case ["--check", .. var wavs]:
        foreach (var w in wavs)
        {
            var x = WavFile.Read(w);
            var (voiced, f0) = VoiceCheck.Analyze(x);
            Console.WriteLine($"{Path.GetFileName(w)}: {x.Length / 24000.0:0.00}s, voiced {voiced:P0}, median pitch {f0:0} Hz");
        }
        break;

    case ["--seeds", var models, var n]:
        VoicePacks.Folder = models;
        foreach (var d in new[] { "A man in his forties with a warm, easygoing baritone voice.", "A young woman with a bright, clear, lively voice." })
            for (int i = 0; i < int.Parse(n); i++)
            {
                var a = NeuralModels.Shared.Design().Design(QwenSamples.SampleText, d, "english", i == 0 ? Sampler.Seed(d) : Sampler.Seed(d + i), 1200, default);
                var (v, f) = VoiceCheck.Analyze(a);
                Console.WriteLine($"{d[..12]} seed {i}: {a.Length / 24000.0:0.0}s {f:0} Hz");
            }
        break;

    case ["--bench", var models, var outDir, .. var only]:
        Bench(models, outDir, only);
        break;

    default:
        Console.WriteLine("VoiceBench --hardware | --bench <Voice Models folder> <output folder> [qwen|orpheus] | --probe <model.onnx>… | --check <file.wav>…");
        break;
}

void Bench(string models, string outDir, string[] only)
{
    VoicePacks.Folder = models;
    NeuralModels.Shared.MemoryGB = hw.MemoryGB;
    Onnx.GpuDevice = hw.GpuIndex;
    Directory.CreateDirectory(outDir);
    Console.WriteLine($"{hw}\nVoice Models: {models}");
    string[] lines =
    [
        "The rain had not stopped for three days, and the river was rising fast.",
        "“You can’t be serious,” she said, setting down her cup. “Not after everything that happened last winter.”",
        "He walked to the window and looked out over the empty street, wondering whether anyone would come at all. The lamps had been lit an hour ago, and the town was quiet.",
    ];
    if (only.Length == 0 || only.Contains("qwen"))
    {
        if (!VoicePacks.IsInstalled(VoicePacks.Qwen)) Console.WriteLine("Qwen3-TTS isn't in that folder.");
        else
        {
            var engine = new QwenEngine();
            var load = Stopwatch.StartNew();
            NeuralModels.Shared.Design();
            Console.WriteLine($"Qwen3-TTS VoiceDesign loaded in {load.Elapsed.TotalSeconds:0.0} s on {Onnx.LastDevice}");
            var choice = VoiceInfo.QwenPresets.First(v => v.VoiceID == "preset:baritone").Choice;
            var sw = Stopwatch.StartNew();
            var sample = QwenSamples.Sample(choice.Design!, Tone.Neutral, default);
            Report("Qwen3-TTS voice sample (VoiceDesign 1.7B)", sample.Audio, sw.Elapsed.TotalSeconds, Path.Combine(outDir, "qwen-sample.wav"));
            load.Restart();
            NeuralModels.Shared.Clone();
            Console.WriteLine($"Qwen3-TTS Base loaded in {load.Elapsed.TotalSeconds:0.0} s on {Onnx.LastDevice}");
            Measure("Qwen3-TTS line (Base 0.6B clone)", lines, l => engine.Render(new SpeechRequest { Text = l, Choice = choice }, default).Result, "qwen");
            NeuralModels.Shared.Unload(VoiceEngine.Qwen);
        }
    }
    if (only.Length == 0 || only.Contains("orpheus"))
    {
        if (!VoicePacks.IsInstalled(VoicePacks.Orpheus)) Console.WriteLine("Orpheus isn't in that folder.");
        else
        {
            var engine = new OrpheusEngine();
            var load = Stopwatch.StartNew();
            NeuralModels.Shared.Orpheus();
            Console.WriteLine($"Orpheus loaded in {load.Elapsed.TotalSeconds:0.0} s on {Onnx.LastDevice}");
            Measure("Orpheus line (3B, 4-bit)", lines, l => engine.Render(new SpeechRequest { Text = l, Choice = new VoiceChoice(VoiceEngine.Orpheus, "tara") }, default).Result, "orpheus");
            var sigh = engine.Render(new SpeechRequest { Text = "I suppose we should go home now.", Choice = new VoiceChoice(VoiceEngine.Orpheus, "leo"), Sound = VocalSound.Sigh }, default).Result;
            WavFile.Write(Path.Combine(outDir, "orpheus-sigh.wav"), sigh);
        }
    }
    Console.WriteLine($"Peak memory: {Process.GetCurrentProcess().PeakWorkingSet64 / 1e9:0.0} GB");

    void Measure(string what, string[] texts, Func<string, float[]> speak, string prefix)
    {
        double audio = 0, time = 0;
        for (int i = 0; i < texts.Length; i++)
        {
            var sw = Stopwatch.StartNew();
            var a = speak(texts[i]);
            Report($"{what} {i + 1}", a, sw.Elapsed.TotalSeconds, Path.Combine(outDir, $"{prefix}-{i + 1}.wav"));
            audio += a.Length / 24000.0;
            time += sw.Elapsed.TotalSeconds;
        }
        Console.WriteLine($"  {what}: {audio / time:0.00} s of audio per second overall");
    }
}

static void Report(string what, float[] audio, double seconds, string file)
{
    WavFile.Write(file, audio);
    double s = audio.Length / 24000.0;
    var (voiced, f0) = VoiceCheck.Analyze(audio);
    Console.WriteLine($"  {what}: {s:0.00} s of audio in {seconds:0.0} s = {s / seconds:0.00}× ({voiced:P0} voiced, {f0:0} Hz)");
}

static class VoiceCheck
{
    public static (double Voiced, double Pitch) Analyze(float[] x) => InnerVoice.Core.Speech.Neural.Pitch.Analyze(x);
}
