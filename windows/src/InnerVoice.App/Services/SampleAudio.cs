using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace InnerVoice.App;

/// Records and plays pronunciation samples: up to 15 seconds from the default microphone,
/// saved as AAC in .m4a like the Mac's, so samples play in both apps. Windows only (Media
/// Foundation); Windows N editions need the Media Feature Pack for the AAC encoder.
public static class SampleAudio
{
    public static bool IsSupported => OperatingSystem.IsWindows();
    public static Guid? RecordingID { get; private set; }
    public static event Action? Changed;

    static WasapiCapture? capture;
    static MemoryStream? buffer;
    static WaveFileWriter? writer;
    static WaveOutEvent? player;
    static System.Timers.Timer? limit;

    /// Starts recording for a pronunciation. Returns an error message, or null.
    public static string? Start(Guid id)
    {
        if (!OperatingSystem.IsWindows()) return "Recording samples works on Windows.";
        return StartWindows(id);
    }

    [SupportedOSPlatform("windows")]
    static string? StartWindows(Guid id)
    {
        try
        {
            StopWindows();
            capture = new WasapiCapture();
            buffer = new MemoryStream();
            writer = new WaveFileWriter(new IgnoreDisposeStream(buffer), capture.WaveFormat);
            capture.DataAvailable += (_, e) => writer?.Write(e.Buffer, 0, e.BytesRecorded);
            capture.StartRecording();
            RecordingID = id;
            limit = new System.Timers.Timer(15_000) { AutoReset = false };
            limit.Elapsed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Finished?.Invoke(Stop()));
            limit.Start();
            Changed?.Invoke();
            return null;
        }
        catch (Exception e)
        {
            RecordingID = null;
            return e is System.Runtime.InteropServices.COMException
                ? "InnerVoice can't use the microphone. In Windows Settings › Privacy & security › Microphone, let apps use it, then try again."
                : e.Message;
        }
    }

    /// Raised when recording stops on its own at 15 seconds, with the sample.
    public static event Action<byte[]?>? Finished;

    /// Stops recording and returns the sample as .m4a bytes.
    public static byte[]? Stop()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return StopWindows();
    }

    [SupportedOSPlatform("windows")]
    static byte[]? StopWindows()
    {
        limit?.Stop();
        limit = null;
        if (capture is null) return null;
        capture.StopRecording();
        capture.Dispose();
        capture = null;
        writer?.Flush();
        writer?.Dispose();
        writer = null;
        RecordingID = null;
        Changed?.Invoke();
        if (buffer is null || buffer.Length < 1000) return null;
        var tmp = Path.Combine(Path.GetTempPath(), $"InnerVoice-sample-{Guid.NewGuid():N}.m4a");
        try
        {
            buffer.Position = 0;
            using var reader = new WaveFileReader(buffer);
            MediaFoundationApi.Startup();
            MediaFoundationEncoder.EncodeToAac(reader, tmp, 128_000);
            return File.ReadAllBytes(tmp);
        }
        catch { return null; }
        finally
        {
            buffer = null;
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public static string? Play(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) return "Playing samples works on Windows.";
        return PlayWindows(data);
    }

    [SupportedOSPlatform("windows")]
    static string? PlayWindows(byte[] data)
    {
        try
        {
            player?.Stop();
            player?.Dispose();
            var tmp = Path.Combine(Path.GetTempPath(), $"InnerVoice-play-{Guid.NewGuid():N}.m4a");
            File.WriteAllBytes(tmp, data);
            var reader = new MediaFoundationReader(tmp);
            player = new WaveOutEvent();
            player.Init(reader);
            player.PlaybackStopped += (_, _) => { reader.Dispose(); try { File.Delete(tmp); } catch { } };
            player.Play();
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    /// Keeps the memory stream open when the WAV writer is disposed.
    sealed class IgnoreDisposeStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] b, int o, int c) => inner.Read(b, o, c);
        public override long Seek(long o, SeekOrigin s) => inner.Seek(o, s);
        public override void SetLength(long v) => inner.SetLength(v);
        public override void Write(byte[] b, int o, int c) => inner.Write(b, o, c);
        protected override void Dispose(bool disposing) { }
    }
}
