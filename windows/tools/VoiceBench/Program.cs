// VoiceBench: measures the local voices on this computer (README.md beside this file).
//   VoiceBench --hardware                          what the hardware check sees, and its verdicts
//   VoiceBench --bench <Voice Models> <out> [pocket|orpheus]
//                                                  speaks the stock voices, Tara in every tone and
//                                                  as a child, a clone, and Orpheus's lines
//   VoiceBench --clone <Voice Models> <recording.wav> <out>
//                                                  clones a voice from a recording, like Clone a
//                                                  Voice › On This PC, and speaks with it
//   VoiceBench --probe <model.onnx>…              a model's inputs and outputs
//   VoiceBench --check <file.wav>…                how much of a file is voiced, and its pitch

using System.Diagnostics;
using InnerVoice.Core;
using InnerVoice.Core.Speech;
using InnerVoice.Core.Speech.Neural;

var hw = VoiceHardwareCheck.Detect();

switch (args)
{
    case ["--hardware"]:
        Console.WriteLine(hw);
        foreach (var e in new[] { VoiceEngine.Pocket, VoiceEngine.Orpheus })
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
            var (voiced, f0) = Pitch.Analyze(x);
            Console.WriteLine($"{Path.GetFileName(w)}: {x.Length / 24000.0:0.00}s, voiced {voiced:P0}, median pitch {f0:0} Hz");
        }
        break;

    case ["--bench", var models, var outDir, .. var only]:
        Bench(models, outDir, only);
        break;

    case ["--clone", var models, var recording, var outDir]:
        Clone(models, recording, outDir);
        break;

    default:
        Console.WriteLine("VoiceBench --hardware | --bench <Voice Models folder> <output folder> [pocket|orpheus] | --clone <Voice Models folder> <recording.wav> <output folder> | --probe <model.onnx>… | --check <file.wav>…");
        break;
}

void Setup(string models)
{
    VoicePacks.Folder = models;
    NeuralModels.Shared.MemoryGB = hw.MemoryGB;
    Onnx.GpuDevice = hw.GpuIndex;
    Onnx.PreferGpu = VoiceHardwareCheck.UsesGpu(hw);
    Console.WriteLine($"{hw}\nVoice Models: {models}");
}

void Bench(string models, string outDir, string[] only)
{
    Setup(models);
    Directory.CreateDirectory(outDir);
    string[] lines =
    [
        "The rain had not stopped for three days, and the river was rising fast.",
        "“You can’t be serious,” she said, setting down her cup. “Not after everything that happened last winter.”",
        "He walked to the window and looked out over the empty street, wondering whether anyone would come at all. The lamps had been lit an hour ago, and the town was quiet.",
    ];
    if (only.Length == 0 || only.Contains("pocket"))
    {
        if (!VoicePacks.IsInstalled(VoicePacks.Pocket)) Console.WriteLine("Pocket TTS isn't in that folder.");
        else
        {
            var engine = new PocketEngine();
            var load = Stopwatch.StartNew();
            NeuralModels.Shared.Pocket();
            Console.WriteLine($"Pocket TTS loaded in {load.Elapsed.TotalSeconds:0.0} s on {Onnx.LastDevice}");
            // Every stock voice says the same line (as the Mac's self-test pocketVoices).
            var said = "The lamps had been lit an hour ago, and the town was quiet. Nobody came.";
            Measure("Pocket TTS stock voices", VoiceInfo.Pocket.Select(v => (PocketVoice.StockName(v.VoiceID)!, new SpeechRequest { Text = said, Choice = v.Choice })),
                    r => Speak(engine, r), "pocket-voice");
            // Tara in every tone, then as a child (casting's pitch and pace for a child).
            var tara = VoiceInfo.Pocket.First(v => v.VoiceID == "stock:tara");
            // A spoken segment as the script gives it: the line without its quote marks.
            var spokenLine = "You can’t be serious. Not after everything that happened last winter.";
            Measure("Pocket TTS tones", Tones.All.Select(t => (t.Raw(), new SpeechRequest { Text = spokenLine, Choice = tara.Choice, Kind = LineKind.Spoken, Tone = t })),
                    r => Speak(engine, r), "pocket-tone");
            Measure("Pocket TTS child", [("tara", new SpeechRequest { Text = "Can we go to the river tomorrow? Please?", Choice = VoiceCasting.AgeAdjusted(tara, VoiceAge.Child), Kind = LineKind.Spoken })],
                    r => Speak(engine, r), "pocket-child");
            // Short lines stop when they're said, at the even and the angry temperature.
            Measure("Pocket TTS short lines", new[] { Tone.Neutral, Tone.Angry }.Select(t => (t.Raw(), new SpeechRequest { Text = "No.", Choice = tara.Choice, Kind = LineKind.Spoken, Tone = t })),
                    r => Speak(engine, r), "pocket-short");
            Measure("Pocket TTS lines (Dan)", lines.Select((l, i) => ((i + 1).ToString(), new SpeechRequest { Text = l, Choice = new VoiceChoice(VoiceEngine.Pocket, "stock:dan") })),
                    r => Speak(engine, r), "pocket-line");
            NeuralModels.Shared.Unload(VoiceEngine.Pocket);
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
            Measure("Orpheus line (3B, 4-bit)", lines.Select((l, i) => ((i + 1).ToString(), new SpeechRequest { Text = l, Choice = new VoiceChoice(VoiceEngine.Orpheus, "tara") })),
                    r => engine.Render(r, default).Result, "orpheus");
        }
    }
    if (Process.GetCurrentProcess().PeakWorkingSet64 is > 0 and var peak) Console.WriteLine($"Peak memory: {peak / 1e9:0.00} GB");

    void Measure(string what, IEnumerable<(string Name, SpeechRequest Request)> requests, Func<SpeechRequest, float[]> speak, string prefix)
    {
        double audio = 0, time = 0;
        foreach (var (name, r) in requests)
        {
            var sw = Stopwatch.StartNew();
            var a = speak(r);
            Report($"{what}: {name}", a, sw.Elapsed.TotalSeconds, Path.Combine(outDir, $"{prefix}-{name}.wav"));
            audio += a.Length / 24000.0;
            time += sw.Elapsed.TotalSeconds;
        }
        Console.WriteLine($"  {what}: {audio / time:0.00} s of audio per second overall");
    }
}

// The engine's whole line, as the renderer gets it (Pocket TTS shades and paces it itself).
static float[] Speak(PocketEngine engine, SpeechRequest r) => engine.Render(r, default).Result;

void Clone(string models, string recording, string outDir)
{
    Setup(models);
    Directory.CreateDirectory(outDir);
    // As Clone a Voice › On This PC: trim the quiet ends, take up to CloneClip.Longest seconds
    // (without the words, the clip is cut at the longest), keep it among this run's clones.
    var audio = LocalClones.Trim(WavFile.Read(recording));
    var clip = audio[..Math.Min(audio.Length, (int)(CloneClip.Longest * SpeechAudio.SampleRate))];
    LocalClones.Shared = new LocalClones(Path.Combine(outDir, "Cloned Voices"));
    var c = LocalClones.Shared.Add("Bench Clone", Gender.Neutral, clip, "");
    WavFile.Write(Path.Combine(outDir, "clone-clip.wav"), clip);
    var engine = new PocketEngine();
    var choice = new VoiceChoice(VoiceEngine.Pocket, c.VoiceID);
    foreach (var (name, text, tone) in new[]
    {
        ("1", "The rain had not stopped for three days, and the river was rising fast.", Tone.Neutral),
        ("2", "“You can’t be serious,” she said. “Not after everything that happened last winter.”", Tone.Excited),
        ("3", "I suppose we should go home now, before it gets dark.", Tone.Sad),
    })
    {
        var sw = Stopwatch.StartNew();
        var a = engine.Render(new SpeechRequest { Text = text, Choice = choice, Kind = tone == Tone.Neutral ? null : LineKind.Spoken, Tone = tone }, default).Result;
        Report($"Clone line {name} ({tone.Raw()})", a, sw.Elapsed.TotalSeconds, Path.Combine(outDir, $"clone-{name}.wav"));
    }
    var (cv, cf) = Pitch.Analyze(clip);
    Console.WriteLine($"  The clip: {clip.Length / 24000.0:0.0} s, {cv:P0} voiced, {cf:0} Hz");
}

static void Report(string what, float[] audio, double seconds, string file)
{
    WavFile.Write(file, audio);
    double s = audio.Length / 24000.0;
    var (voiced, f0) = Pitch.Analyze(audio);
    Console.WriteLine($"  {what}: {s:0.00} s of audio in {seconds:0.00} s = {s / seconds:0.00}× ({voiced:P0} voiced, {f0:0} Hz)");
}
