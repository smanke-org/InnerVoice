// WAV in and out: the audio cache, Export Audio's WAV choice, and what Windows' voices hand
// back. Reads 8/16/24/32-bit PCM and 32-bit float in any channel count and rate; mixes to mono
// and converts to the app's 24 kHz.

using System.Text;

namespace InnerVoice.Core.Speech;

public static class WavFile
{
    /// Mono float samples at the app's rate.
    public static float[] Read(Stream s)
    {
        using var r = new BinaryReader(s, Encoding.ASCII, leaveOpen: true);
        if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not a WAV file.");
        r.ReadInt32();
        if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not a WAV file.");
        int format = 1, channels = 1, rate = SpeechAudio.SampleRate, bits = 16;
        byte[]? data = null;
        while (s.Position + 8 <= s.Length)
        {
            var id = new string(r.ReadChars(4));
            uint size = r.ReadUInt32();
            if (id == "fmt ")
            {
                format = r.ReadUInt16();
                channels = r.ReadUInt16();
                rate = r.ReadInt32();
                r.ReadInt32();
                r.ReadUInt16();
                bits = r.ReadUInt16();
                int rest = (int)size - 16;
                if (format == 0xFFFE && rest >= 10)
                {
                    // WAVE_FORMAT_EXTENSIBLE: the real format is the sub-format GUID's first two bytes.
                    r.ReadUInt16(); r.ReadUInt16(); r.ReadUInt32();
                    format = r.ReadUInt16();
                    rest -= 10;
                }
                if (rest > 0) r.ReadBytes(rest);
            }
            else if (id == "data")
            {
                // Some writers leave the size 0 or -1 while streaming: read to the end.
                long available = s.Length - s.Position;
                data = r.ReadBytes((int)Math.Min(size == 0 || size == uint.MaxValue ? available : size, available));
            }
            else r.ReadBytes((int)Math.Min(size, s.Length - s.Position));
            if ((size & 1) == 1 && s.Position < s.Length) r.ReadByte();
        }
        if (data is null) throw new InvalidDataException("The WAV file has no audio.");
        channels = Math.Max(1, channels);
        int bytesPer = bits / 8;
        int frames = data.Length / (bytesPer * channels);
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int at = (f * channels + c) * bytesPer;
                sum += (format, bits) switch
                {
                    (3, 32) => BitConverter.ToSingle(data, at),
                    (_, 8) => (data[at] - 128) / 128f,
                    (_, 16) => BitConverter.ToInt16(data, at) / 32768f,
                    (_, 24) => ((data[at] | data[at + 1] << 8 | (sbyte)data[at + 2] << 16)) / 8388608f,
                    (_, 32) => BitConverter.ToInt32(data, at) / 2147483648f,
                    _ => throw new InvalidDataException($"Unsupported WAV format ({format}, {bits}-bit)."),
                };
            }
            mono[f] = sum / channels;
        }
        return SpeechAudio.Resampled(mono, rate);
    }

    public static float[] Read(string path)
    {
        using var f = File.OpenRead(path);
        return Read(f);
    }

    public static float[] Read(byte[] bytes) => Read(new MemoryStream(bytes));

    /// 24 kHz mono: 32-bit float for the cache (exact), 16-bit PCM for files people keep.
    public static void Write(Stream s, float[] samples, bool float32 = false, int rate = SpeechAudio.SampleRate)
    {
        using var w = new BinaryWriter(s, Encoding.ASCII, leaveOpen: true);
        int bytesPer = float32 ? 4 : 2;
        int dataSize = samples.Length * bytesPer;
        w.Write("RIFF"u8);
        w.Write(36 + dataSize);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)(float32 ? 3 : 1));
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * bytesPer);
        w.Write((short)bytesPer);
        w.Write((short)(bytesPer * 8));
        w.Write("data"u8);
        w.Write(dataSize);
        if (float32) foreach (var x in samples) w.Write(x);
        else foreach (var x in samples) w.Write((short)Math.Round(Math.Clamp(x, -1f, 1f) * 32767));
    }

    public static void Write(string path, float[] samples, bool float32 = false)
    {
        using var f = File.Create(path);
        Write(f, samples, float32);
    }

    public static byte[] Bytes(float[] samples, bool float32 = false)
    {
        var m = new MemoryStream();
        Write(m, samples, float32);
        return m.ToArray();
    }
}
