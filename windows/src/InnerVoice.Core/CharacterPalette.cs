// Ported from the CharacterPalette part of Sources/InnerVoiceKit/RuleAttribution.swift.

namespace InnerVoice.Core;

/// Character colors made for highlighting text. Red is never used: it means Who's Speaking?
/// Three families: masculine (blues and greens) for men, feminine (pinks and purples) for
/// women, and neutral (warm colors) for anyone the book hasn't said yet.
public static class CharacterPalette
{
    public enum Family { Masculine, Feminine, Neutral }

    public static string Title(this Family f) => f switch
    {
        Family.Masculine => "Masculine",
        Family.Feminine => "Feminine",
        _ => "Neutral",
    };

    public static Family FamilyFor(Gender? g) => g switch
    {
        Gender.Male => Family.Masculine,
        Gender.Female => Family.Feminine,
        _ => Family.Neutral,
    };

    public sealed record Swatch(string Name, string Hex, Family Family);

    /// Each family in the order new characters get them: neighbors are the easiest to tell apart.
    public static readonly IReadOnlyList<Swatch> Swatches = new (string, string, Family)[]
    {
        ("Blue", "#3B82F6", Family.Masculine), ("Emerald", "#10B981", Family.Masculine), ("Indigo", "#6366F1", Family.Masculine),
        ("Teal", "#14B8A6", Family.Masculine), ("Sky", "#0EA5E9", Family.Masculine), ("Green", "#22C55E", Family.Masculine),
        ("Slate", "#64748B", Family.Masculine), ("Cyan", "#06B6D4", Family.Masculine),
        // Pinks come later: highlighted, they sit closest to the red of Who's Speaking?
        ("Fuchsia", "#D946EF", Family.Feminine), ("Violet", "#8B5CF6", Family.Feminine), ("Lavender", "#A78BFA", Family.Feminine),
        ("Orchid", "#C026D3", Family.Feminine), ("Pink", "#EC4899", Family.Feminine), ("Purple", "#A855F7", Family.Feminine),
        ("Lilac", "#E879F9", Family.Feminine), ("Blush", "#F472B6", Family.Feminine),
        ("Amber", "#F59E0B", Family.Neutral), ("Lime", "#84CC16", Family.Neutral), ("Orange", "#F97316", Family.Neutral),
        ("Brown", "#A16207", Family.Neutral), ("Gold", "#CA8A04", Family.Neutral), ("Olive", "#65A30D", Family.Neutral),
    }.Select(t => new Swatch(t.Item1, t.Item2, t.Item3)).ToList();

    public static IEnumerable<Swatch> SwatchesIn(Family family) => Swatches.Where(s => s.Family == family);

    /// The family a palette color belongs to; null for a color picked with Other….
    public static Family? FamilyOf(string hex) =>
        Swatches.FirstOrDefault(s => string.Equals(s.Hex, hex, StringComparison.OrdinalIgnoreCase))?.Family;

    /// The color for Who's Speaking? and Unsure lines.
    public const string AttentionHex = "#EF4444";

    /// The first color in the family no character uses yet, or the family's least used one.
    public static string Next(IEnumerable<string> used, Family family = Family.Neutral)
    {
        var counts = new Dictionary<string, int>();
        foreach (var u in used) counts[u.ToUpperInvariant()] = counts.GetValueOrDefault(u.ToUpperInvariant()) + 1;
        return Next(counts, family);
    }

    static string Next(Dictionary<string, int> counts, Family family)
    {
        var pool = SwatchesIn(family).ToList();
        var free = pool.FirstOrDefault(s => !counts.ContainsKey(s.Hex.ToUpperInvariant()));
        if (free is not null) return free.Hex;
        return pool.MinBy(s => counts.GetValueOrDefault(s.Hex.ToUpperInvariant()))!.Hex;
    }

    /// The color a new character starts with: a title ("Mrs.", "Sir") already says which
    /// family; anyone else starts neutral until the book says more.
    public static string ForNew(string name, IEnumerable<string> used) => Next(used, FamilyFor(GenderReader.TitleGender(name)));

    /// The family for each character on Automatic. Reads the whole book for pronouns, so the
    /// app runs it off the UI thread and hands the result to `RecolorAutomatic`.
    public static Dictionary<Guid, Family> Families(Project p, BookIndex index)
    {
        var result = new Dictionary<Guid, Family>();
        foreach (var c in p.Characters.Where(c => c.ColorIsAutomatic))
            result[c.Id] = FamilyFor(GenderReader.GenderOf(c, p, index));
        return result;
    }

    /// Gives every character on Automatic a color from their gender's family: main characters
    /// first. A character whose color already fits keeps it; colors the narrator chose are
    /// never changed. Returns how many characters changed color.
    public static int RecolorAutomatic(Project p, BookIndex index, Dictionary<Guid, Family>? known = null, Guid? only = null)
    {
        var moving = Enumerable.Range(0, p.Characters.Count).Where(i =>
        {
            var c = p.Characters[i];
            return c.ColorIsAutomatic && (only is null || c.Id == only) && (known is null || known.ContainsKey(c.Id));
        }).ToList();
        if (moving.Count == 0) return 0;
        var movingSet = moving.ToHashSet();
        var counts = new Dictionary<string, int>();
        for (int i = 0; i < p.Characters.Count; i++)
        {
            if (movingSet.Contains(i)) continue;
            var h = p.Characters[i].ColorHex.ToUpperInvariant();
            counts[h] = counts.GetValueOrDefault(h) + 1;
        }
        var order = moving
            .OrderByDescending(i => index.StatsFor(p.Characters[i].Id)?.Lines ?? 0)
            .ThenBy(i => p.Characters[i].Name, StringComparer.Ordinal)
            .ToList();
        var family = new Dictionary<int, Family>();
        foreach (var i in order)
        {
            var id = p.Characters[i].Id;
            family[i] = known is not null && known.TryGetValue(id, out var f) ? f : FamilyFor(GenderReader.GenderOf(p.Characters[i], p, index));
        }
        var placed = new HashSet<int>();
        foreach (var i in order)
        {
            var hex = p.Characters[i].ColorHex.ToUpperInvariant();
            if (FamilyOf(hex) == family[i] && !counts.ContainsKey(hex))
            {
                counts[hex] = 1;
                placed.Add(i);
            }
        }
        int changed = 0;
        foreach (var i in order.Where(i => !placed.Contains(i)))
        {
            var hex = Next(counts, family[i]);
            counts[hex.ToUpperInvariant()] = counts.GetValueOrDefault(hex.ToUpperInvariant()) + 1;
            if (!string.Equals(p.Characters[i].ColorHex, hex, StringComparison.OrdinalIgnoreCase))
            {
                p.Characters[i].ColorHex = hex;
                changed++;
            }
        }
        return changed;
    }

    /// Reddish colors are too close to Who's Speaking? to tell apart.
    public static bool IsTooRed(string hex)
    {
        if (Rgb(hex) is not var (r, g, b)) return false;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (!(max - min > 0.25) || max != r) return false;
        double hue = (g - b) / (max - min) * 60;
        return hue > -20 && hue < 15;
    }

    public static (double R, double G, double B)? Rgb(string hex)
    {
        var h = hex.Trim('#');
        if (h.Length != 6 || !uint.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var v)) return null;
        return (((v >> 16) & 0xFF) / 255.0, ((v >> 8) & 0xFF) / 255.0, (v & 0xFF) / 255.0);
    }
}
