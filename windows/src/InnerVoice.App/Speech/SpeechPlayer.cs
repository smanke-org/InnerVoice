// Ported from the Mac's Speech/SpeechPlayer.swift and Pauses.swift: plays segments one after
// another, making the next ones while the current one plays. One player for the whole app:
// starting something new stops what was playing. Pauses are looked up per gap (so a change
// in Settings › Voices › Pauses is heard on the next one), after each voice's own silence is
// trimmed. Play's speed changes tempo, not pitch (WSOLA in the Kit).

using Avalonia.Threading;
using InnerVoice.Core;
using InnerVoice.Core.Speech;

namespace InnerVoice.App.Speech;

/// Where played audio goes: the PC's speakers (WinMM through NAudio) or nowhere (tests, and
/// any computer without Windows).
public interface IAudioOutput : IDisposable
{
    /// Queues samples (24 kHz mono) behind anything already queued.
    void Enqueue(float[] samples);
    /// Seconds still queued and not yet heard.
    double Pending { get; }
    void Stop();
}

/// Plays nothing and finishes at once: test runs and non-Windows builds.
public sealed class SilentOutput : IAudioOutput
{
    public void Enqueue(float[] samples) { }
    public double Pending => 0;
    public void Stop() { }
    public void Dispose() { }
}

/// The PC's default output device, through WinMM.
sealed class WaveOutput : IAudioOutput
{
    readonly NAudio.Wave.WaveOutEvent device = new() { DesiredLatency = 120 };
    readonly NAudio.Wave.BufferedWaveProvider buffer = new(NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(SpeechAudio.SampleRate, 1))
    {
        BufferDuration = TimeSpan.FromMinutes(5),
        DiscardOnBufferOverflow = false,
        ReadFully = true,
    };

    public WaveOutput()
    {
        device.Init(buffer);
    }

    public void Enqueue(float[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        buffer.AddSamples(bytes, 0, bytes.Length);
        if (device.PlaybackState != NAudio.Wave.PlaybackState.Playing) device.Play();
    }

    public double Pending => buffer.BufferedDuration.TotalSeconds;

    public void Stop()
    {
        buffer.ClearBuffer();
        device.Stop();
    }

    public void Dispose() => device.Dispose();
}

public sealed class SpeechPlayer
{
    public static SpeechPlayer Shared { get; } = new();

    /// What `Output` makes: real speakers on Windows unless the run is a test (and
    /// INNERVOICE_SPEECH_AUDIBLE isn't set).
    public static Func<IAudioOutput> MakeOutput { get; set; } = () =>
        OperatingSystem.IsWindows() && (!AppPaths.IsTestRun || Environment.GetEnvironmentVariable("INNERVOICE_SPEECH_AUDIBLE") is { Length: > 0 })
            ? new WaveOutput() : new SilentOutput();

    IAudioOutput? output;
    CancellationTokenSource? cts;

    /// Changes, on the UI thread: playing, preparing, current segment, failure.
    public event Action? Changed;

    public SpeechSegment? Current { get; private set; }
    /// The last segment that started playing (kept after it ends, for tests).
    public SpeechSegment? LastStarted { get; private set; }
    public bool IsPlaying { get; private set; }
    /// "Making Mara’s voice…" while the first audio is being made.
    public bool Preparing { get; private set; }
    /// The engine making the first audio.
    public VoiceEngine? PreparingEngine { get; private set; }
    public string? Failure { get; set; }

    /// Play's speed, 0.5× to 2× (Settings › Voices › Pauses), without changing the voices' pitch.
    public double Speed
    {
        get => Math.Clamp(AppSettings.Current.PlaybackSpeed, 0.5, 2);
        set => AppSettings.Update(s => s.PlaybackSpeed = Math.Clamp(value, 0.5, 2));
    }

    void Notify() => Dispatcher.UIThread.Post(() => Changed?.Invoke());

    /// Plays the segments. `onSegment` hears each one as it starts (to follow along in the
    /// manuscript), on the UI thread; `onFinish` when they were all played (not stopped).
    public void Play(IReadOnlyList<SpeechSegment> segments, VoiceResolver resolver, Action<SpeechSegment>? onSegment = null,
                     Action? onFinish = null, SpeechRenderer? renderer = null)
    {
        Stop();
        if (segments.Count == 0) return;
        renderer ??= SpeechRenderer.Shared;
        Failure = null;
        IsPlaying = true;
        Preparing = true;
        PreparingEngine = segments.Select(s => resolver.RequestFor(s)?.Choice.Engine).FirstOrDefault(e => e is not null);
        Notify();
        var source = new CancellationTokenSource();
        cts = source;
        var ct = source.Token;
        output ??= MakeOutput();
        var device = output;
        _ = Task.Run(async () =>
        {
            bool finished = false;
            try
            {
                var made = new Dictionary<int, Task<float[]>>();
                void Make(int i)
                {
                    if (i >= segments.Count || made.ContainsKey(i)) return;
                    made[i] = resolver.RequestFor(segments[i]) is { } request
                        ? renderer.Render(request, ct)
                        : Task.FromResult(SpeechAudio.Silence(0.05));
                }
                for (int i = 0; i < segments.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    Make(i); Make(i + 1); Make(i + 2);
                    // Without the voice's own leading and trailing silence: the pauses are the narrator's.
                    var audio = SpeechAudio.Trimmed(await made[i]);
                    made.Remove(i);
                    ct.ThrowIfCancellationRequested();
                    var seg = segments[i];
                    double speed = Speed;
                    // Wait for the last piece to be heard before this one starts.
                    while (device.Pending > 0.05) await Task.Delay(15, ct);
                    Preparing = false;
                    Current = seg;
                    LastStarted = seg;
                    Dispatcher.UIThread.Post(() => { if (!ct.IsCancellationRequested) onSegment?.Invoke(seg); });
                    Notify();
                    var heard = speed == 1 ? audio : await Task.Run(() => SpeechAudio.TimeStretched(audio, speed), ct);
                    device.Enqueue(heard);
                    int seconds = (int)Math.Round(SpeechAudio.Seconds(audio));
                    Dispatcher.UIThread.Post(() => Activity.Record(ActivityKey.SecondsPlayed, seconds));
                    // Looked up now, so a change in Settings › Voices › Pauses is heard on the next gap.
                    double gap = seg.Pause == PauseKind.None ? seg.PauseAfter : PauseSettings.Current.Seconds(seg.Pause);
                    if (gap > 0) device.Enqueue(SpeechAudio.Silence(gap / speed));
                }
                while (device.Pending > 0.02) await Task.Delay(15, ct);
                finished = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                if (!ct.IsCancellationRequested)
                {
                    Failure = e is SpeechException ? e.Message : SpeechException.Failed(e.Message, e).Message;
                    SpeechLog.Write($"play failed ({PreparingEngine?.Raw() ?? "?"}): {e.GetType().Name}: {e.Message}");
                }
            }
            finally
            {
                if (cts == source)
                {
                    IsPlaying = false;
                    Preparing = false;
                    Current = null;
                    Notify();
                    if (finished && Failure is null) Dispatcher.UIThread.Post(() => onFinish?.Invoke());
                }
            }
        }, ct);
    }

    public void Stop()
    {
        cts?.Cancel();
        cts = null;
        output?.Stop();
        if (IsPlaying || Preparing || Current is not null)
        {
            IsPlaying = false;
            Preparing = false;
            Current = null;
            Notify();
        }
    }
}

/// Speech errors, for diagnosing "nothing plays": %LocalAppData%\InnerVoice\Logs\speech.log.
/// Never the book's text.
public static class SpeechLog
{
    public static void Write(string message)
    {
        System.Diagnostics.Debug.WriteLine("InnerVoice speech: " + message);
        // Test runs (the fake ElevenLabs server's errors) never write the narrator's log.
        if (AppPaths.IsTestRun) return;
        try
        {
            var dir = Path.Combine(AppPaths.Support, "Logs");
            Directory.CreateDirectory(dir);
            var version = typeof(SpeechLog).Assembly.GetName().Version?.ToString(3) ?? "?";
            File.AppendAllText(Path.Combine(dir, "speech.log"), $"{DateTime.UtcNow:O} {version} {message}\n");
        }
        catch { }
    }
}
