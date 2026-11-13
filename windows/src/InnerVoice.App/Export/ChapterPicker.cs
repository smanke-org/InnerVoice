// The Chapters list Export Audio and Export EPUB with Audio share (the Mac's
// workspace.audioExportChapters): a checkbox per chapter, Select All, and Shift-click for a
// range. The choice stays with the open book until a chapter is added or deleted.

using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core.Speech;

namespace InnerVoice.App.Export;

public sealed class ChapterPicker : StackPanel
{
    static readonly ConditionalWeakTable<ProjectDocument, HashSet<int>> chosen = new();

    readonly ProjectDocument document;
    readonly List<CheckBox> boxes = [];
    readonly CheckBox all = new() { Content = "Select All" };
    readonly TextBlock count = Ui.Caption("");
    int? anchor;
    bool updating;

    public event Action? Changed;

    public ChapterPicker(ProjectDocument document)
    {
        this.document = document;
        Spacing = Look.Xs;
        int n = document.Project.Chapters.Count;
        // Chapters added or deleted since the last export: start again from every chapter.
        if (chosen.TryGetValue(document, out var set) && set.Any(c => c >= n)) chosen.Remove(document);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(all);
        count.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(count, 1);
        header.Children.Add(count);
        Children.Add(header);
        var list = new StackPanel { Spacing = 2 };
        for (int i = 0; i < n; i++)
        {
            int c = i;
            var box = new CheckBox { Content = $"{c + 1}   {AudioExporter.ChapterTitle(document.Project, c)}" };
            ToolTip.SetTip(box, "Click a chapter to leave it in or out. Shift-click picks every chapter between it and the last one you clicked.");
            box.IsCheckedChanged += (_, _) => { if (!updating) Pick(c, box.IsChecked == true, shift); };
            box.AddHandler(PointerPressedEvent, (_, e) => shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift), handledEventsToo: true);
            boxes.Add(box);
            list.Children.Add(box);
        }
        Children.Add(new ScrollViewer { Content = list, MaxHeight = 200 });
        all.IsCheckedChanged += (_, _) =>
        {
            if (updating) return;
            Set(all.IsChecked == true ? null : []);
        };
        Refresh();
    }

    bool shift;

    /// The picked chapters, by index, in book order.
    public List<int> Picked
    {
        get
        {
            int n = document.Project.Chapters.Count;
            return chosen.TryGetValue(document, out var set) ? Enumerable.Range(0, n).Where(set.Contains).ToList() : Enumerable.Range(0, n).ToList();
        }
    }

    public bool EveryChapter => Picked.Count == document.Project.Chapters.Count;

    void Pick(int i, bool on, bool range)
    {
        var set = Picked.ToHashSet();
        var which = range && anchor is { } a ? Enumerable.Range(Math.Min(a, i), Math.Abs(a - i) + 1) : [i];
        foreach (var c in which) { if (on) set.Add(c); else set.Remove(c); }
        anchor = i;
        Set(set.Count == document.Project.Chapters.Count ? null : set);
    }

    void Set(HashSet<int>? set)
    {
        chosen.Remove(document);
        if (set is not null) chosen.Add(document, set);
        Refresh();
        Changed?.Invoke();
    }

    void Refresh()
    {
        updating = true;
        var picked = Picked.ToHashSet();
        for (int i = 0; i < boxes.Count; i++) boxes[i].IsChecked = picked.Contains(i);
        all.IsChecked = picked.Count == boxes.Count ? true : picked.Count == 0 ? false : null;
        count.Text = picked.Count == boxes.Count ? $"All {boxes.Count:N0} chapters" : $"{picked.Count:N0} of {boxes.Count:N0} chapters";
        updating = false;
    }
}
