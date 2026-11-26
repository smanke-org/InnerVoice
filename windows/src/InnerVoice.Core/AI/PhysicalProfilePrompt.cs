// Ported from PhysicalProfilePrompt in Sources/InnerVoiceKit/PhysicalProfile.swift (the
// prompt and grammar are the Mac's, word for word).

using System.Text.Json.Serialization;

namespace InnerVoice.Core.AI;

public static class PhysicalProfilePrompt
{
    public const string System = "You build character profiles for an author. From what the book says about one character, write how they look. Use only the facts given. Mark a value inferred when the book doesn't state it outright and you are interpreting or guessing; leave a field empty rather than invent something with no basis. Note real changes in the story (a haircut, a scar, aging) with the chapter number, and note contradictions the story doesn't explain. Answer with JSON only.";

    public sealed record PhysicalFact(int? Chapter, FactKind Kind, string Text, string Quote);

    /// The facts used: Appearance and Age, with chapter numbers, in story order.
    public static List<PhysicalFact> Facts(BookCharacter c, Project project, BookIndex index) =>
        c.Facts.Where(f => f.Kind is FactKind.Appearance or FactKind.Age)
            .Select(f => new PhysicalFact(f.SourceParagraphID is { } id && index.Positions.TryGetValue(id, out var p) ? p.Chapter : null, f.Kind, f.Text, f.Quote))
            .OrderBy(f => f.Chapter ?? int.MaxValue).ToList();

    /// Characters worth a profile: the book describes their looks.
    public static bool Wanted(BookCharacter c) => c.Facts.Any(f => f.Kind == FactKind.Appearance);

    public static List<ChatMessage> Messages(BookCharacter c, Project project, BookIndex index)
    {
        var list = string.Join("\n", Facts(c, project, index).Take(24).Select(f =>
            "- " + (f.Chapter is { } ch ? $"[chapter {ch + 1}] " : "") + $"{f.Kind.Title()}: {f.Text}"
            + (f.Quote.Length == 0 ? "" : $" (“{(f.Quote.Length > 160 ? f.Quote[..160] : f.Quote)}”)")));
        var user = $"Character: {c.Name}\nWhat the book says:\n{(list.Length == 0 ? "(nothing)" : list)}\n\n" +
            "Fields: age, height, build, hair, eyes, skin, face, marks (scars, tattoos, birthmarks), clothing (what they usually wear), other. " +
            "Each field is {\"text\":\"…\",\"inferred\":true|false}; \"\" text if unknown. summary: two or three plain sentences describing them, " +
            "summaryInferred true if any of it is guessed. changes: looks that change during the story, each {\"chapter\":N,\"field\":\"hair\",\"text\":\"Cuts her hair short\",\"inferred\":false}. " +
            "flags: contradictions with no story reason, each {\"field\":\"eyes\",\"text\":\"Blue in chapter 3, green in chapter 20\"}.\n" +
            "Reply as {\"summary\":\"…\",\"summaryInferred\":false,\"age\":{…},\"height\":{…},\"build\":{…},\"hair\":{…},\"eyes\":{…},\"skin\":{…},\"face\":{…},\"marks\":{…},\"clothing\":{…},\"other\":{…},\"changes\":[…],\"flags\":[…]}";
        return [new(ChatRole.System, System), new(ChatRole.User, user)];
    }

    public const string Grammar = """
        root ::= "{" ws "\"summary\"" ws ":" ws long ws "," ws "\"summaryInferred\"" ws ":" ws bool ws fields ws "," ws "\"changes\"" ws ":" ws changes ws "," ws "\"flags\"" ws ":" ws flags ws "}"
        fields ::= "," ws "\"age\"" ws ":" ws value ws "," ws "\"height\"" ws ":" ws value ws "," ws "\"build\"" ws ":" ws value ws "," ws "\"hair\"" ws ":" ws value ws "," ws "\"eyes\"" ws ":" ws value ws "," ws "\"skin\"" ws ":" ws value ws "," ws "\"face\"" ws ":" ws value ws "," ws "\"marks\"" ws ":" ws value ws "," ws "\"clothing\"" ws ":" ws value ws "," ws "\"other\"" ws ":" ws value
        value ::= "{" ws "\"text\"" ws ":" ws string ws "," ws "\"inferred\"" ws ":" ws bool ws "}"
        changes ::= "[" ws ( change ( ws "," ws change ){0,5} )? ws "]"
        change ::= "{" ws "\"chapter\"" ws ":" ws [0-9]{1,3} ws "," ws "\"field\"" ws ":" ws field ws "," ws "\"text\"" ws ":" ws string ws "," ws "\"inferred\"" ws ":" ws bool ws "}"
        flags ::= "[" ws ( flag ( ws "," ws flag ){0,3} )? ws "]"
        flag ::= "{" ws "\"field\"" ws ":" ws field ws "," ws "\"text\"" ws ":" ws string ws "}"
        field ::= "\"" ( "age" | "height" | "build" | "hair" | "eyes" | "skin" | "face" | "marks" | "clothing" | "other" ) "\""
        bool ::= "true" | "false"
        string ::= "\"" ( [^"\\\n] | "\\" ["\\/bfnrt] ){0,120} "\""
        long ::= "\"" ( [^"\\\n] | "\\" ["\\/bfnrt] ){0,400} "\""
        ws ::= [ ]?
        """;

    sealed class V { [JsonPropertyName("text")] public string Text { get; set; } = ""; [JsonPropertyName("inferred")] public bool? Inferred { get; set; } }
    sealed class C
    {
        [JsonPropertyName("chapter")] public int? Chapter { get; set; }
        [JsonPropertyName("field")] public string Field { get; set; } = "";
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("inferred")] public bool? Inferred { get; set; }
    }
    sealed class F { [JsonPropertyName("field")] public string Field { get; set; } = ""; [JsonPropertyName("text")] public string Text { get; set; } = ""; }
    sealed class Reply
    {
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("summaryInferred")] public bool? SummaryInferred { get; set; }
        [JsonPropertyName("age")] public V? Age { get; set; }
        [JsonPropertyName("height")] public V? Height { get; set; }
        [JsonPropertyName("build")] public V? Build { get; set; }
        [JsonPropertyName("hair")] public V? Hair { get; set; }
        [JsonPropertyName("eyes")] public V? Eyes { get; set; }
        [JsonPropertyName("skin")] public V? Skin { get; set; }
        [JsonPropertyName("face")] public V? Face { get; set; }
        [JsonPropertyName("marks")] public V? Marks { get; set; }
        [JsonPropertyName("clothing")] public V? Clothing { get; set; }
        [JsonPropertyName("other")] public V? Other { get; set; }
        [JsonPropertyName("changes")] public List<C>? Changes { get; set; }
        [JsonPropertyName("flags")] public List<F>? Flags { get; set; }
    }

    static readonly string[] Empties = ["unknown", "n/a", "none", "not stated"];

    /// `chapters`: how many chapters the book has (a chapter number past it is dropped).
    public static PhysicalProfile? Parse(string text, int chapters)
    {
        if (JsonText.Extract(text) is not { } json || Replies.Decode<Reply>(json) is not { } r) return null;
        static string Clean(string s) => s.Trim();
        var p = new PhysicalProfile { Summary = Clean(r.Summary ?? ""), SummaryInferred = r.SummaryInferred ?? false };
        (PhysicalField, V?)[] pairs =
        [
            (PhysicalField.Age, r.Age), (PhysicalField.Height, r.Height), (PhysicalField.Build, r.Build), (PhysicalField.Hair, r.Hair),
            (PhysicalField.Eyes, r.Eyes), (PhysicalField.Skin, r.Skin), (PhysicalField.Face, r.Face), (PhysicalField.Marks, r.Marks),
            (PhysicalField.Clothing, r.Clothing), (PhysicalField.Other, r.Other),
        ];
        foreach (var (f, v) in pairs)
        {
            if (v is null) continue;
            var t = Clean(v.Text);
            if (t.Length == 0 || Empties.Contains(t.ToLowerInvariant())) continue;
            p.Set(f, new PhysicalValue(VoiceNotesFromBook.CapitalizedFirst(t), v.Inferred ?? false));
        }
        foreach (var c in r.Changes ?? [])
        {
            if (PhysicalFields.Parse(c.Field) is not { } f || Clean(c.Text).Length == 0) continue;
            int? ch = c.Chapter is { } n && n >= 1 && n <= chapters ? n - 1 : null;
            p.Changes.Add(new PhysicalChange { Chapter = ch, Field = f, Text = VoiceNotesFromBook.CapitalizedFirst(Clean(c.Text)), Inferred = c.Inferred ?? false });
        }
        foreach (var c in r.Flags ?? [])
        {
            if (PhysicalFields.Parse(c.Field) is not { } f || Clean(c.Text).Length == 0) continue;
            p.Flags.Add(new PhysicalFlag { Field = f, Text = VoiceNotesFromBook.CapitalizedFirst(Clean(c.Text)) });
        }
        return p.IsEmpty ? null : p;
    }
}
