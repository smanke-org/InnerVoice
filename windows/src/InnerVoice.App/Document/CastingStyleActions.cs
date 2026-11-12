using System.Runtime.CompilerServices;
using InnerVoice.Core;

namespace InnerVoice.App.Document;

/// Casting Style edits (docs/CASTING-STYLE.md), ported from the Mac's CastingStyleActions.swift.
/// Each is one undo step. The style only decides who reads each character: nothing is
/// deleted when it changes.
public static class CastingStyleActions
{
    public static void SetCastingStyle(this ProjectDocument d, CastingStyle? style)
    {
        if (style == d.Project.Settings.CastingStyle) return;
        d.UpdateSettings("Change Casting Style", s => s.CastingStyle = style);
    }

    public static void SetNarrationRole(this ProjectDocument d, VoiceRole role) =>
        d.UpdateSettings("Change Who Reads the Narration", s => s.NarrationRole = role == VoiceRole.Narrator ? null : role);

    public static void SetMinorVoices(this ProjectDocument d, MinorVoices minor) =>
        d.UpdateSettings("Change Minor Characters", s => s.MinorVoices = minor == MinorVoices.Narrator ? null : minor);

    public static void SetShadeStrength(this ProjectDocument d, ShadeStrength strength) =>
        d.UpdateSettings("Change Shading", s => s.ShadeStrength = strength == ShadeStrength.Clear ? null : strength);

    /// Just Me: the narrator's own voice (characters of the same gender aren't shaded).
    public static void SetNarratorGender(this ProjectDocument d, CharacterGender? gender) =>
        d.UpdateSettings("Set Your Voice", s => s.NarratorGender = gender);

    public static void SetRole(this ProjectDocument d, Guid id, VoiceRole? role) =>
        d.UpdateCharacter(id, "Change Who Reads", c => c.Role = role);

    public static void SetMain(this ProjectDocument d, IReadOnlyCollection<Guid> ids, bool? main) =>
        d.Change(main == true ? "Mark as Main Character" : "Unmark Main Character", p =>
        {
            foreach (var c in p.Characters.Where(c => ids.Contains(c.Id))) c.IsMain = main;
        });

    /// The narrator's choice of gender; null goes back to the book's. Automatic colors follow.
    public static void SetGender(this ProjectDocument d, IReadOnlyCollection<Guid> ids, CharacterGender? gender)
    {
        d.Change(ids.Count == 1 ? "Set Gender" : $"Set Gender of {ids.Count} Characters", p =>
        {
            foreach (var c in p.Characters.Where(c => ids.Contains(c.Id))) c.Gender = gender;
        });
        _ = d.RecolorAutomaticAsync();
    }

    public static void SetShade(this ProjectDocument d, Guid id, Shade? shade) =>
        d.UpdateCharacter(id, "Change Shade", c => c.Shade = shade?.Copy());

    /// Three Narrators: a narrator for the narration, the men and the women, made the first
    /// time (one with no part yet takes the narration).
    public static void SetUpThreeNarrators(this ProjectDocument d) => d.Change("Set Up Three Narrators", p =>
    {
        if (!p.Narrators.Any(n => n.Role == VoiceRole.Narrator) && p.Narrators.FirstOrDefault(n => n.Role is null) is { } free)
            free.Role = VoiceRole.Narrator;
        foreach (var (role, name) in new[] { (VoiceRole.Narrator, "Narration"), (VoiceRole.Men, "Men’s Narrator"), (VoiceRole.Women, "Women’s Narrator") })
            if (!p.Narrators.Any(n => n.Role == role)) p.Narrators.Add(new Narrator { Name = name, Role = role });
    });

    /// The part a narrator reads; one narrator per part.
    public static void SetNarratorRole(this ProjectDocument d, Guid narratorID, VoiceRole? role) => d.Change("Change Narrator’s Part", p =>
    {
        foreach (var n in p.Narrators)
        {
            if (n.Id == narratorID) n.Role = role;
            else if (role is not null && n.Role == role) n.Role = null;
        }
    });

    /// Who reads a character now: their own assignment, else their part's narrator.
    public static Narrator? EffectiveNarrator(this ProjectDocument d, BookCharacter c)
    {
        var p = d.Project;
        var g = c.Gender ?? (CastingStyleRules.UsesGender(p.Settings) ? ShadeProfile.Read(c, p, d.Index).Gender?.ToCharacterGender() : null);
        return CastingStyleRules.NarratorFor(c, p, g);
    }

    static readonly ConditionalWeakTable<BookIndex, Dictionary<Guid, Shade>> ShadeCache = [];

    /// Each character's shade cue, worked out once per change (the index is new with every
    /// change), because the margin asks while scrolling.
    public static Dictionary<Guid, Shade> Shades(this ProjectDocument d) =>
        ShadeCache.GetValue(d.Index, _ => CastingStyleRules.Shades(d.Project, d.Index, d.Project.Settings.NarratorGender));

    /// Who reads each character, cached the same way.
    static readonly ConditionalWeakTable<BookIndex, Dictionary<Guid, VoiceRole>> ReaderCache = [];

    public static Dictionary<Guid, VoiceRole> Readers(this ProjectDocument d) =>
        ReaderCache.GetValue(d.Index, _ => CastingStyleRules.Readers(d.Project, d.Index));
}
