// The local voice models in the app (the Mac's VoiceModelManager): downloads with progress,
// resume and SHA-256 checks into Voice Models, removal, and the hardware check that warns
// before a download, or the first use, on a PC where the models would be too slow.

using Avalonia;
using Avalonia.Threading;
using InnerVoice.App.UI;
using InnerVoice.Core;
using InnerVoice.Core.Speech;
using InnerVoice.Core.Speech.Neural;

namespace InnerVoice.App.Speech;

public sealed class VoiceModels
{
    public static VoiceModels Shared { get; } = new();

    public abstract record State
    {
        public sealed record Downloading(double Fraction, long Bytes, bool Verifying) : State;
        public sealed record Failed(string Message) : State;
    }

    public event Action? Changed;
    public Dictionary<VoiceEngine, State> States { get; } = [];
    readonly Dictionary<VoiceEngine, CancellationTokenSource> downloads = [];

    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    /// Tests download from a fake server.
    public static HttpClient? TestHttp { get; set; }

    /// This PC's hardware, read once off the UI thread at startup; a fair guess until then.
    public VoiceHardware Hardware { get; private set; } = new(16, null, 0, 0, "Processor", Environment.ProcessorCount, "x64");
    public bool HardwareKnown { get; private set; }

    Task<VoiceHardware>? detecting;

    public void DetectHardware() => _ = Detected();

    /// The hardware, read once (off the UI thread).
    async Task<VoiceHardware> Detected()
    {
        // Unit tests use made-up hardware (or none); a real run and an agent test copy (APP_STORE)
        // ask the graphics card, which only reads.
        detecting ??= Task.Run(() =>
            Environment.GetEnvironmentVariable("INNERVOICE_TEST_HARDWARE") is { } fake && AppPaths.IsTestRun ? Fake(fake)
            : Environment.GetEnvironmentVariable("INNERVOICE_TEST") is { Length: > 0 } ? VoiceHardwareCheck.Detect(gpu: null)
            : VoiceHardwareCheck.Detect(ProbeGpu()));
        var hw = await detecting;
        if (!HardwareKnown) Use(hw);
        return Hardware;
    }

    public const string ProbeArgument = "--voice-hardware";

    /// The graphics card couldn't be checked (the probe crashed, hung or said nothing useful):
    /// the voices run on the processor, and Settings says so.
    public bool GpuUnknown { get; private set; }

    /// Asks a copy of this app (`InnerVoice.exe --voice-hardware`) for the graphics card, so a
    /// driver or DXGI call that crashes takes only that copy down. Windows only; null: none.
    GpuInfo? ProbeGpu()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
            var start = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            // Run as `dotnet InnerVoice.dll` (a debug run): the same, through dotnet.
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(VoiceModels).Assembly.Location);
            start.ArgumentList.Add(ProbeArgument);
            using var probe = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("didn't start");
            var output = probe.StandardOutput.ReadToEndAsync();
            _ = probe.StandardError.ReadToEndAsync();
            if (!probe.WaitForExit(20_000))
            {
                try { probe.Kill(entireProcessTree: true); } catch (Exception) { }
                throw new TimeoutException("the graphics card check didn't finish");
            }
            if (probe.ExitCode != 0) throw new InvalidOperationException($"the graphics card check stopped (exit {probe.ExitCode})");
            var line = output.Result.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
            if (line == "none") return null;
            return VoiceHardwareCheck.ParseGpu(line) ?? throw new InvalidOperationException($"unexpected answer: {line}");
        }
        catch (Exception e)
        {
            GpuUnknown = true;
            SpeechLog.Write($"voice hardware: {e.Message}; using the processor");
            return null;
        }
    }

    /// Tests: "gpu:12,ram:32" or "cpu,ram:8".
    static VoiceHardware Fake(string s)
    {
        var parts = s.Split(',').Select(p => p.Split(':')).ToDictionary(p => p[0], p => p.Length > 1 ? double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture) : 0);
        return new VoiceHardware((int)parts.GetValueOrDefault("ram", 16), parts.ContainsKey("gpu") ? "Test Graphics" : null, parts.GetValueOrDefault("gpu"), 0, "Test Processor", 8, "x64");
    }

    public void Use(VoiceHardware hw)
    {
        Hardware = hw;
        HardwareKnown = true;
        NeuralModels.Shared.MemoryGB = hw.MemoryGB;
        // DirectML only on a real DirectX 12 card with some memory of its own; integrated
        // graphics share the PC's memory and are often no faster than the processor.
        Onnx.PreferGpu = VoiceHardwareCheck.UsesGpu(hw);
        Onnx.GpuDevice = hw.GpuIndex;
        Changed?.Invoke();
    }

    public bool IsInstalled(VoiceEngine e) => VoicePacks.IsInstalled(e);

    /// Why the engine may be unusably slow here; null when this PC is up to it.
    public string? Warning(VoiceEngine e) => VoiceHardwareCheck.Warning(Hardware, e);

    /// Before a download, or the first use of a downloaded model, on a weak PC: says plainly
    /// why it may be too slow and suggests Windows Voices or ElevenLabs. True: go ahead.
    public async Task<bool> ConfirmHardware(Visual from, VoiceEngine e, bool downloading)
    {
        if (!HardwareKnown) await Detected();
        if (Warning(e) is not { } why) return true;
        if (!downloading && AppSettings.Current.VoiceModelsWarned.Contains(e.Raw())) return true;
        var title = downloading ? $"{e.Title()} may be too slow on this PC" : $"{e.Title()} may be slow on this PC";
        bool ok = await Dialogs.Confirm(from, title, why + "\n\n" + VoiceHardwareCheck.Recommended,
            downloading ? $"Download Anyway ({VoicePacks.For(e)!.SizeDescription})" : $"Use {e.Title()}", destructive: false);
        if (ok && !AppPaths.IsTestRun)
            AppSettings.Update(s => { if (!s.VoiceModelsWarned.Contains(e.Raw())) s.VoiceModelsWarned.Add(e.Raw()); });
        return ok;
    }

    /// Choosing an engine that isn't downloaded yet (the Voices page): offers the download,
    /// with the hardware warning first on a weak PC. True when it started (or was running).
    public async Task<bool> OfferDownload(Visual from, VoiceEngine e)
    {
        if (VoicePacks.For(e) is not { } pack || IsInstalled(e)) return false;
        if (downloads.ContainsKey(e)) return true;
        if (!HardwareKnown) await Detected();
        bool ok = Warning(e) is not null
            ? await ConfirmHardware(from, e, downloading: true)
            : await Dialogs.Confirm(from, $"Download {pack.Title}?",
                $"{pack.Summary} It’s a free download of {pack.SizeDescription}, once; then it runs on this PC and your book never leaves it.", "Download", destructive: false);
        if (ok) Download(e);
        return ok;
    }

    public void Download(VoiceEngine e)
    {
        if (VoicePacks.For(e) is not { } pack || downloads.ContainsKey(e)) return;
        var cts = new CancellationTokenSource();
        downloads[e] = cts;
        States[e] = new State.Downloading(VoicePacks.BytesPresent(pack) / (double)pack.Bytes, VoicePacks.BytesPresent(pack), false);
        Changed?.Invoke();
        var progress = new Progress<VoicePackProgress>(p =>
        {
            if (!downloads.ContainsKey(e)) return;
            States[e] = new State.Downloading(p.Fraction, p.Bytes, p.Verifying);
            Changed?.Invoke();
        });
        _ = Task.Run(async () =>
        {
            string? failure = null;
            try { await VoicePackDownloader.Download(pack, TestHttp ?? Http, progress, cts.Token); }
            catch (OperationCanceledException) { }
            catch (HttpRequestException) { failure = "Unable to download: check the internet connection, then try again. The download picks up where it stopped."; }
            catch (Exception ex) { failure = ex is VoicePackDownloader.NotEnoughSpaceException ? ex.Message : $"Unable to download: {ex.Message}"; }
            Dispatcher.UIThread.Post(() =>
            {
                downloads.Remove(e);
                if (failure is not null) States[e] = new State.Failed(failure);
                else States.Remove(e);
                VoicePacks.Forget();
                Changed?.Invoke();
                VoiceHooks.EnginesChanged();
            });
        });
    }

    public void Cancel(VoiceEngine e)
    {
        if (downloads.Remove(e, out var cts)) cts.Cancel();
        States.Remove(e);
        Changed?.Invoke();
    }

    public void Remove(VoiceEngine e)
    {
        if (VoicePacks.For(e) is not { } pack) return;
        Cancel(e);
        NeuralModels.Shared.Unload(e);
        if (e == VoiceEngine.Qwen) QwenSamples.Forget();
        VoicePacks.Remove(pack);
        Changed?.Invoke();
        VoiceHooks.EnginesChanged();
    }

    /// For tests.
    public async Task WaitForDownload(VoiceEngine e)
    {
        while (downloads.ContainsKey(e)) await Task.Delay(50);
    }
}
