// This PC's pronunciations (the Mac's Speech/ComputerPronunciations.swift): one list every book
// uses unless it turned it off (PronunciationLists in Core has the rules). Kept in
// pronunciations.json beside settings.json in the app's data folder, as `{listID, entries}` like
// the Mac's; the listID is made once. Windows has no settings sync, so the list stays on this PC.
// Test runs keep it in memory and never save it.

using System.Text.Json;
using Avalonia;
using Avalonia.Platform.Storage;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Speech;

public sealed class ComputerPronunciations
{
    public static ComputerPronunciations Shared { get; } = new();

    /// The list changed (any window showing it redraws).
    public event Action? Changed;

    public PronunciationList List { get; private set; }

    static string FilePath => Path.Combine(AppPaths.Support, "pronunciations.json");
    static readonly JsonSerializerOptions Json = new(ProjectJson.Options) { WriteIndented = true };

    ComputerPronunciations()
    {
        List = AppPaths.IsTestRun ? new PronunciationList() : Load();
        if (!AppPaths.IsTestRun && !File.Exists(FilePath)) Write(List);
        PronunciationLists.Computer = List.Copy();
    }

    static PronunciationList Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<PronunciationList>(File.ReadAllBytes(FilePath), Json) is { } l) return l;
        }
        catch { }
        return new PronunciationList();
    }

    static void Write(PronunciationList list)
    {
        if (AppPaths.IsTestRun) return;
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(list, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
    }

    /// Every change goes through here: saved, and speech uses it from the next line on.
    public void Change(Action<PronunciationList> body)
    {
        var l = List.Copy();
        body(l);
        if (l.SameAs(List)) return;
        List = l;
        PronunciationLists.Computer = l.Copy();
        Write(l);
        Changed?.Invoke();
    }

    public void Update(Guid id, Action<ListedPronunciation> body) =>
        Change(l => { if (l.Entries.FirstOrDefault(e => e.Id == id) is { } e) body(e); });

    public void Remove(IReadOnlySet<Guid> ids) => Change(l => l.Entries.RemoveAll(e => ids.Contains(e.Id)));

    public Guid Add(string term = "New Word", string sayItAs = "")
    {
        var e = new ListedPronunciation(term, sayItAs);
        Change(l => l.Entries.Add(e));
        return e.Id;
    }

    /// Adds entries with `conflict` for words already here with a different pronunciation (the
    /// agent's `pronunciations share` and `computer import`).
    public PronunciationLists.MergeResult Merge(IEnumerable<ListedPronunciation> entries, PronunciationConflict conflict)
    {
        var result = new PronunciationLists.MergeResult();
        Change(l => result = PronunciationLists.Merge(entries, l, conflict));
        return result;
    }

    /// For tests: answers the conflict question without showing it (Replace, KeepExisting, or
    /// null for Cancel). Unset, the question is asked.
    public static Func<IReadOnlyList<ListedPronunciation>, PronunciationConflict?>? AnswerConflicts;

    /// Adds entries, asking first when some words are already here with a different
    /// pronunciation. `incoming` names where they come from ("This Book’s", "the Imported
    /// Ones"). Returns what happened, or null when the narrator canceled.
    public async Task<PronunciationLists.MergeResult?> AddAsking(Visual from, IReadOnlyList<ListedPronunciation> entries, string incoming)
    {
        var conflicts = PronunciationLists.Conflicts(entries, List);
        var policy = PronunciationConflict.KeepExisting;
        if (conflicts.Count > 0)
        {
            PronunciationConflict? answer;
            if (AnswerConflicts is { } test) answer = test(conflicts);
            else
            {
                var title = conflicts.Count == 1
                    ? $"“{conflicts[0].Term}” is already in This PC’s list with a different pronunciation."
                    : $"{conflicts.Count} words are already in This PC’s list with a different pronunciation.";
                var detail = string.Join("\n", conflicts.Take(6).Select(c => $"{c.Term}: {List.Entry(c.Term)?.SayItAs ?? ""} → {c.SayItAs}"))
                    + (conflicts.Count > 6 ? "\n…" : "");
                // The Mac's order: Keep This PC’s (the default), Use This Book’s, Cancel.
                answer = await Dialogs.Choose(from, title, detail, ["Keep This PC’s", $"Use {incoming}"], "Cancel") switch
                {
                    0 => PronunciationConflict.KeepExisting,
                    1 => PronunciationConflict.Replace,
                    _ => null,
                };
            }
            if (answer is not { } a) return null;
            policy = a;
        }
        return Merge(entries, policy);
    }

    /// What happened, for the narrator: "Added 2, replaced 1."
    public static string Describe(PronunciationLists.MergeResult r)
    {
        var parts = new List<string>();
        if (r.Added > 0) parts.Add($"added {r.Added}");
        if (r.Replaced > 0) parts.Add($"replaced {r.Replaced}");
        if (r.Kept > 0) parts.Add($"kept {r.Kept} of This PC’s");
        if (r.Unchanged > 0) parts.Add($"{r.Unchanged} already there");
        if (parts.Count == 0) return "Nothing to add.";
        var s = string.Join(", ", parts);
        return char.ToUpperInvariant(s[0]) + s[1..] + ".";
    }

    /// Adds every entry the book has to This PC's list (Book › Add This Book’s Pronunciations
    /// to This PC…, and the Pronunciations page's button).
    public static async Task AddBook(Visual from, InnerVoice.App.Document.ProjectDocument document)
    {
        var entries = PronunciationLists.BookEntries(document.Project);
        if (entries.Count == 0)
        {
            await Dialogs.Alert(from, "This book has no pronunciations to add.", "Only entries with a Say It As are added.");
            return;
        }
        if (await Shared.AddAsking(from, entries, "This Book’s") is not { } r) return;
        await Dialogs.Alert(from, "This book’s pronunciations are in This PC’s list.",
            Describe(r) + " Every book on this PC now uses them, unless it has its own entry for the word.");
    }

    // MARK: Import and export

    public static readonly Avalonia.Platform.Storage.FilePickerFileType ImportTypes = new("Pronunciations (CSV, text or JSON)")
    {
        Patterns = ["*.csv", "*.txt", "*.text", "*.tsv", "*.json"],
    };

    public async Task ImportFile(Visual from)
    {
        var path = await Dialogs.OpenFile(from, "Import Pronunciations", ImportTypes);
        if (path is not null) await ImportFile(from, path);
    }

    public async Task ImportFile(Visual from, string path)
    {
        var name = Path.GetFileName(path);
        List<ListedPronunciation> entries;
        try { entries = PronunciationFile.Read(File.ReadAllBytes(path), name); }
        catch (Exception e)
        {
            await Dialogs.Alert(from, $"Couldn’t import {name}", e.Message);
            return;
        }
        if (await AddAsking(from, entries, "the Imported Ones") is not { } r) return;
        await Dialogs.Alert(from, $"Imported {name}", Describe(r));
    }

    public async Task ExportFile(Visual from)
    {
        var csv = new Avalonia.Platform.Storage.FilePickerFileType("CSV (Excel, Numbers)") { Patterns = ["*.csv"] };
        var json = new Avalonia.Platform.Storage.FilePickerFileType("JSON (InnerVoice)") { Patterns = ["*.json"] };
        var top = Avalonia.Controls.TopLevel.GetTopLevel(from);
        if (top is null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Export This PC’s Pronunciations", SuggestedFileName = "Pronunciations", DefaultExtension = "csv",
            FileTypeChoices = [csv, json], ShowOverwritePrompt = true,
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try { Export(path); }
        catch (Exception e) { await Dialogs.Alert(from, $"Couldn’t export {Path.GetFileName(path)}", e.Message); }
    }

    /// Writes the list to `path`: JSON for a .json name, else CSV. Returns how many words.
    public int Export(string path)
    {
        var format = PronunciationFile.FormatFor(path) ?? PronunciationFile.Format.Csv;
        File.WriteAllBytes(path, PronunciationFile.Export(List.Entries, format));
        return List.Entries.Count;
    }
}
