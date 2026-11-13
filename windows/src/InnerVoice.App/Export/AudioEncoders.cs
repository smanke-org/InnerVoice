// M4A (AAC) for Export Audio and EPUB with Audio. A Windows build (net10.0-windows) encodes
// with Media Foundation through WinRT's MediaTranscoder: built into Windows 10 and 11, no
// download. N editions without the Media Feature Pack, and builds without the Windows SDK,
// have no AAC encoder: Export Audio offers WAV only and says why.

using InnerVoice.Core.Speech;

namespace InnerVoice.App.Export;

public static class AudioEncoders
{
    /// The M4A encoder, or null when this PC (or build) has none. Tests set a fake.
    public static IAudioEncoder? M4a { get; set; } = Default();

    /// Why M4A isn't offered, in the narrator's words.
    public const string NoM4a = "M4A needs Windows’ AAC encoder, which this copy of Windows doesn’t have (Windows N editions need the Media Feature Pack). WAV works everywhere.";

    static IAudioEncoder? Default()
    {
#if WINDOWS
        return OperatingSystem.IsWindows() ? new MediaFoundationM4a() : null;
#else
        return null;
#endif
    }

#if WINDOWS
    /// 128 kbps AAC, 44.1 kHz mono, like the Mac's.
    sealed class MediaFoundationM4a : IAudioEncoder
    {
        public async Task EncodeM4a(string wavPath, string m4aPath, CancellationToken ct)
        {
            var source = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(wavPath)).AsTask(ct);
            var folder = await global::Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(Path.GetFullPath(m4aPath))!).AsTask(ct);
            var target = await folder.CreateFileAsync(Path.GetFileName(m4aPath), global::Windows.Storage.CreationCollisionOption.ReplaceExisting).AsTask(ct);
            var profile = global::Windows.Media.MediaProperties.MediaEncodingProfile.CreateM4a(global::Windows.Media.MediaProperties.AudioEncodingQuality.Medium);
            profile.Audio = global::Windows.Media.MediaProperties.AudioEncodingProperties.CreateAac(AudioExporter.FileRate, 1, 128_000);
            var transcoder = new global::Windows.Media.Transcoding.MediaTranscoder();
            var prepared = await transcoder.PrepareFileTranscodeAsync(source, target, profile).AsTask(ct);
            if (!prepared.CanTranscode)
                throw new InvalidOperationException(prepared.FailureReason == global::Windows.Media.Transcoding.TranscodeFailureReason.CodecNotFound
                    ? NoM4a : $"Windows couldn’t make the M4A ({prepared.FailureReason}).");
            await prepared.TranscodeAsync().AsTask(ct);
        }
    }
#endif
}
