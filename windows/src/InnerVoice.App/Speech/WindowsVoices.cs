// Windows' built-in voices: the stand-in for the Mac's Apple voices (engine `apple`, shown as
// "Windows Voices"). Instant, no download, and the fallback on slow PCs. A Windows build
// (net10.0-windows) speaks with WinRT's Windows.Media.SpeechSynthesis, which has the modern
// OneCore voices (Aria, Guy, Jenny…, plus any language voices the narrator added in Settings
// › Time & language › Speech); SAPI (System.Speech) adds the older desktop voices and any
// third-party SAPI voices, and is all a plain net10.0 build has.
//
// Like Apple's voices they can't act: a tone only nudges speed, pitch and loudness (the
// Mac's AppleSpeech.nudge), and a shade's pitch and pace become the voice's own settings.

using InnerVoice.Core;
using InnerVoice.Core.Speech;

namespace InnerVoice.App.Speech;

public sealed class WindowsVoices : IVoiceEngine
{
    public VoiceEngine Engine => VoiceEngine.Apple;
    public bool ShadesPitch => true;
    public bool ShadesPace => true;

    List<VoiceInfo>? cache;
    readonly Lock gate = new();

    public bool IsReady => OperatingSystem.IsWindows() && Voices.Count > 0;

    public string? NotReadyReason => !OperatingSystem.IsWindows()
        ? "Windows Voices need Windows."
        : Voices.Count == 0 ? "This PC has no English voices. Add one in Windows Settings › Time & language › Speech." : null;

    /// English voices, best first. Asking Windows is slow and views ask on every redraw: kept
    /// until `Reload` (Settings › Voices has a button for voices added since).
    public IReadOnlyList<VoiceInfo> Voices
    {
        get
        {
            lock (gate)
            {
                cache ??= OperatingSystem.IsWindows() ? Load() : [];
                return cache;
            }
        }
    }

    public void Reload()
    {
        lock (gate) cache = null;
    }

    static List<VoiceInfo> Load()
    {
        var found = new List<VoiceInfo>();
        try { found.AddRange(OneCore.List()); } catch { }
        try
        {
            // SAPI voices WinRT doesn't already have (the desktop copies of Zira and David
            // are the same voices; third-party SAPI voices are new).
            var names = found.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            found.AddRange(Sapi.List().Where(v => !names.Contains(v.Name)));
        }
        catch { }
        return found.OrderByDescending(v => v.Quality).ThenBy(v => v.Name, StringComparer.Ordinal).ToList();
    }

    public VoiceInfo? Info(VoiceChoice choice) => Voices.FirstOrDefault(v => v.VoiceID == choice.VoiceID) ?? StandIn(choice);

    /// A Mac book's Apple voice (com.apple.voice.premium.en-US.Ava) isn't on a PC: it speaks
    /// with the closest Windows voice, same gender when the name tells it, so a book cast on
    /// the Mac plays here without recasting. The book keeps the Mac's choice.
    public VoiceInfo? StandIn(VoiceChoice choice) => StandInVoice(choice) is { } match
        ? match with { VoiceID = choice.VoiceID, Name = $"{choice.VoiceID.Split('.').Last()} (Mac voice)", Detail = $"Speaks as {match.Name} on this PC" }
        : null;

    VoiceInfo? StandInVoice(VoiceChoice choice)
    {
        if (!choice.VoiceID.StartsWith("com.apple.", StringComparison.Ordinal) || Voices.Count == 0) return null;
        var gender = MacGenders.GetValueOrDefault(choice.VoiceID.Split('.').Last(), Gender.Neutral);
        return Voices.FirstOrDefault(v => v.Gender == gender) ?? Voices[0];
    }

    VoiceInfo? Resolve(VoiceChoice choice) => Voices.FirstOrDefault(v => v.VoiceID == choice.VoiceID) ?? StandInVoice(choice);

    /// (rate, pitch, volume) for each tone, as the Mac's AppleSpeech.nudge.
    public static (double Rate, double Pitch, double Volume) Nudge(Tone tone) => tone switch
    {
        Tone.Excited => (1.08, 1.08, 1),
        Tone.Confused => (0.95, 1.03, 1),
        Tone.Sad => (0.88, 0.94, 0.85),
        Tone.Angry => (1.06, 0.97, 1),
        Tone.Whispered => (0.9, 1, 0.5),
        Tone.Scared => (1.1, 1.08, 0.9),
        Tone.Tender => (0.92, 1, 0.85),
        Tone.Sarcastic => (0.94, 0.97, 1),
        _ => (1, 1, 1),
    };

    public async Task<float[]> Render(SpeechRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw SpeechException.EngineUnavailable(VoiceEngine.Apple);
        var voice = Resolve(request.Choice) ?? throw SpeechException.VoiceMissing(VoiceNames.Name(request.Choice));
        var (r, p, v) = Nudge(request.Tone);
        // A shade (Casting Style): pitch in semitones, pace in steps.
        if (request.Shade is { } s)
        {
            p *= ShadeAudio.PitchMultiplier(s.Pitch);
            r *= ShadeAudio.PaceRate(s.Pace);
        }
        double rate = Math.Clamp((request.Choice.Rate ?? 1) * r, 0.5, 3);
        double pitch = Math.Clamp((request.Choice.Pitch ?? 1) * p, 0.5, 2);
        double volume = v * (request.Kind == LineKind.Thought ? 0.85 : 1);
        float[] samples = voice.VoiceID.StartsWith(Sapi.Prefix, StringComparison.Ordinal)
            ? await Task.Run(() => Sapi.Speak(voice.VoiceID, request.Text, rate, pitch), ct)
            : await OneCore.Speak(voice.VoiceID, request.Text, rate, pitch, ct);
        return SpeechAudio.Scaled(samples, (float)volume);
    }

    public static string Accent(string language, string name)
    {
        var l = language.ToLowerInvariant();
        if (name == "Fiona") return "Scottish";
        return l.Length >= 5 ? l[..5] switch
        {
            "en-us" => "American",
            "en-gb" => "British",
            "en-au" => "Australian",
            "en-ie" => "Irish",
            "en-in" => "Indian",
            "en-za" => "South African",
            "en-nz" => "New Zealand",
            "en-ca" => "Canadian",
            _ => "English",
        } : "English";
    }

    /// "Microsoft Aria" → "Aria"; "Microsoft David Desktop" → "David".
    public static string ShortName(string display)
    {
        var n = display.Trim();
        if (n.StartsWith("Microsoft ", StringComparison.Ordinal)) n = n["Microsoft ".Length..];
        if (n.EndsWith(" Desktop", StringComparison.Ordinal)) n = n[..^" Desktop".Length];
        int dash = n.IndexOf(" - ", StringComparison.Ordinal);
        return dash > 0 ? n[..dash] : n;
    }

    /// The Mac's Apple voice names and genders, for stand-ins.
    static readonly Dictionary<string, Gender> MacGenders = new[]
    {
        "Ava", "Zoe", "Allison", "Susan", "Samantha", "Nicky", "Joelle", "Noelle", "Serena", "Stephanie", "Kate", "Martha", "Karen",
        "Catherine", "Moira", "Fiona", "Tessa", "Veena", "Tara", "Isha", "Sangeeta", "Matilda", "Siobhan", "Flo", "Grandma", "Sandy",
        "Shelley", "Kathy", "Victoria", "Agnes", "Vicki",
    }.Select(n => (n, Gender.Female)).Concat(new[]
    {
        "Evan", "Nathan", "Tom", "Alex", "Aaron", "Fred", "Daniel", "Arthur", "Oliver", "Jamie", "Lee", "Gordon", "Rishi", "Aman",
        "Malcolm", "Eddy", "Grandpa", "Reed", "Rocko", "Ralph", "Junior", "Albert", "Bruce",
    }.Select(n => (n, Gender.Male))).ToDictionary(x => x.n, x => x.Item2);

    // MARK: WinRT (OneCore)

    static class OneCore
    {
        public static IEnumerable<VoiceInfo> List()
        {
#if WINDOWS
            foreach (var v in global::Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices)
            {
                if (!v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)) continue;
                var name = ShortName(v.DisplayName);
                var gender = v.Gender == global::Windows.Media.SpeechSynthesis.VoiceGender.Female ? Gender.Female : Gender.Male;
                // Natural voices (where Windows offers them) sound best; the OneCore ones are standard.
                bool natural = v.DisplayName.Contains("Natural", StringComparison.OrdinalIgnoreCase) || v.Id.Contains("Natural", StringComparison.OrdinalIgnoreCase);
                yield return new VoiceInfo(VoiceEngine.Apple, v.Id, name, gender, VoiceAge.Adult, Accent(v.Language, name),
                                           natural ? 2 : 1, natural ? "Natural" : "");
            }
#else
            yield break;
#endif
        }

        public static async Task<float[]> Speak(string id, string text, double rate, double pitch, CancellationToken ct)
        {
#if WINDOWS
            using var synth = new global::Windows.Media.SpeechSynthesis.SpeechSynthesizer();
            var voice = global::Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == id)
                ?? throw SpeechException.VoiceMissing(id.Split('\\').Last());
            synth.Voice = voice;
            synth.Options.SpeakingRate = rate;
            synth.Options.AudioPitch = pitch;
            // The voice's own leading silence is trimmed later; keep sentence boundaries natural.
            using var stream = await synth.SynthesizeTextToStreamAsync(text).AsTask(ct);
            var bytes = new byte[stream.Size];
            using (var input = stream.GetInputStreamAt(0))
            using (var reader = new global::Windows.Storage.Streams.DataReader(input))
            {
                await reader.LoadAsync((uint)stream.Size).AsTask(ct);
                reader.ReadBytes(bytes);
            }
            return WavFile.Read(bytes);
#else
            await Task.CompletedTask;
            throw SpeechException.EngineUnavailable(VoiceEngine.Apple);
#endif
        }
    }

    // MARK: SAPI

    static class Sapi
    {
        public const string Prefix = "sapi:";

        public static IEnumerable<VoiceInfo> List()
        {
            if (!OperatingSystem.IsWindows()) yield break;
            using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
            foreach (var iv in synth.GetInstalledVoices())
            {
                if (!iv.Enabled) continue;
                var v = iv.VoiceInfo;
                if (!v.Culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase)) continue;
                var name = ShortName(v.Name);
                var gender = v.Gender switch
                {
                    System.Speech.Synthesis.VoiceGender.Female => Gender.Female,
                    System.Speech.Synthesis.VoiceGender.Male => Gender.Male,
                    _ => MacGenders.GetValueOrDefault(name, Gender.Neutral),
                };
                var age = v.Age switch
                {
                    System.Speech.Synthesis.VoiceAge.Child => VoiceAge.Child,
                    System.Speech.Synthesis.VoiceAge.Teen => VoiceAge.Young,
                    System.Speech.Synthesis.VoiceAge.Senior => VoiceAge.Older,
                    _ => VoiceAge.Adult,
                };
                yield return new VoiceInfo(VoiceEngine.Apple, Prefix + v.Name, name, gender, age, Accent(v.Culture.Name, name), 0, "Classic");
            }
        }

        public static float[] Speak(string id, string text, double rate, double pitch)
        {
            if (!OperatingSystem.IsWindows()) throw SpeechException.EngineUnavailable(VoiceEngine.Apple);
            using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
            synth.SelectVoice(id[Prefix.Length..]);
            // SAPI's rate is -10…10, where 10 is about three times as fast.
            synth.Rate = (int)Math.Clamp(Math.Round(10 * Math.Log(rate) / Math.Log(3)), -10, 10);
            var output = new MemoryStream();
            synth.SetOutputToWaveStream(output);
            synth.Speak(text);
            synth.SetOutputToNull();
            var samples = WavFile.Read(output.ToArray());
            // SAPI has no pitch setting: shift the finished audio, keeping its length.
            if (Math.Abs(pitch - 1) > 0.005)
            {
                double f = pitch;
                samples = SpeechAudio.TimeStretched(SpeechAudio.Interpolated(samples, f), 1 / f);
            }
            return samples;
        }
    }
}
