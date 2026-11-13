// The Mac's PlayCommand.audition: one sample line in a voice, for choosing voices: the
// character's own words when they have lines.

using InnerVoice.App.Document;
using InnerVoice.Core;
using InnerVoice.Core.Speech;

namespace InnerVoice.App.Speech;

public static class Audition
{
    public static void Play(VoiceChoice choice, BookCharacter? character, ProjectDocument document, Tone? tone = null, Shade? shade = null)
    {
        var project = document.Project;
        var index = document.Index;
        string text;
        var sampleTone = tone ?? Tone.Neutral;
        if (character is { } c)
        {
            // A line of theirs of a good length, said plainly unless asked otherwise.
            var theirs = project.Lines.Where(l => l.SpeakerID == c.Id && l.Kind == LineKind.Spoken).ToList();
            var pick = theirs.FirstOrDefault(l =>
            {
                int n = Words.Count(project.TextOf(l, index));
                return n >= 6 && n <= 30 && (tone is not null || (l.Tone ?? Tone.Neutral) == Tone.Neutral);
            }) ?? theirs.FirstOrDefault();
            if (pick is not null)
            {
                text = project.TextOf(pick, index);
                if (tone is null) sampleTone = pick.Tone ?? Tone.Neutral;
            }
            else text = $"My name is {c.FirstName}.";
        }
        else text = "Chapter One. The storm came in off the sea just after dark.";
        var seg = new SpeechSegment
        {
            Id = "audition", ParagraphID = Guid.NewGuid(), LineID = character is null ? null : Guid.NewGuid(),
            Text = SpeechScript.Clean(text), Tone = sampleTone, Kind = LineKind.Spoken, PauseAfter = 0, Pause = PauseKind.None,
        };
        var resolver = new VoiceResolver(project, choice.IsNarrator ? null : choice.Engine, index, writtenByNarrator: AppSettings.Current.WrittenByNarrator);
        if (!choice.IsNarrator) resolver.Narrator = VoiceResolver.WithNotes(choice, character);
        resolver.AuditionShade = shade;
        resolver.Characters.Clear();
        SpeechPlayer.Shared.Play([seg], resolver);
    }

    /// A Pronunciations entry said in the book's narrator voice, the way a line would be
    /// (the Mac's `PlayCommand.hear`): the sentence where it's first used, with every entry
    /// applied, or the word alone.
    public static void Hear(Pronunciation p, ProjectDocument document)
    {
        var project = document.Project;
        var index = document.Index;
        var seg = new SpeechSegment
        {
            Id = "pronunciation", ParagraphID = Guid.NewGuid(), LineID = null,
            Text = SpeechScript.PronunciationSample(p, project, index), Tone = Tone.Neutral, PauseAfter = 0, Pause = PauseKind.None,
        };
        var resolver = new VoiceResolver(project, null, index, writtenByNarrator: AppSettings.Current.WrittenByNarrator);
        resolver.Characters.Clear();
        SpeechPlayer.Shared.Play([seg], resolver);
    }
}
