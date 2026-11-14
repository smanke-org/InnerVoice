using Avalonia.Controls;
using Avalonia.Input;
using InnerVoice.App.Document;
using InnerVoice.App.Pages;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Windows;

/// The window's menu bar and keyboard shortcuts.
public sealed partial class ProjectWindow
{
    MenuItem undoItem = null!, redoItem = null!, findItem = null!;

    Menu BuildMenu()
    {
        var menu = new Menu();

        var file = Top("_File");
        file.Items.Add(Item("_New from Manuscript…", "Ctrl+N", () => _ = App.Current.NewFromManuscript(this)));
        file.Items.Add(Item("_Open…", "Ctrl+O", () => _ = App.Current.OpenWithPicker(this)));
        var recent = new MenuItem { Header = "Open _Recent" };
        recent.SubmenuOpened += (_, _) =>
        {
            recent.Items.Clear();
            foreach (var path in AppSettings.Current.RecentProjects.Where(File.Exists))
                recent.Items.Add(Item(Path.GetFileNameWithoutExtension(path), null, () => App.Current.OpenPath(path)));
            if (recent.Items.Count == 0) recent.Items.Add(new MenuItem { Header = "No Recent Projects", IsEnabled = false });
        };
        recent.Items.Add(new MenuItem { Header = "…" });
        file.Items.Add(recent);
        file.Items.Add(new Separator());
        // The voices' exports, in every project type but Narration Tools (as on the Mac).
        var exportAudio = Item("Export _Audio…", null, () => _ = InnerVoice.App.Export.AudioExportWindow.Show(this, Document));
        var exportEpub = Item("Export EPUB with Au_dio…", null, () => _ = InnerVoice.App.Export.EpubExportWindow.Show(this, Document));
        var voicesSeparator = new Separator();
        file.SubmenuOpened += (_, _) => exportAudio.IsVisible = exportEpub.IsVisible = voicesSeparator.IsVisible = Document.Project.ShowsVoices;
        file.Items.Add(exportAudio);
        file.Items.Add(exportEpub);
        file.Items.Add(voicesSeparator);
        file.Items.Add(Item("_Save", "Ctrl+S", () => _ = Save()));
        file.Items.Add(Item("Save _As…", "Ctrl+Shift+S", () => _ = SaveAs()));
        AddFileItems(file);
        file.Items.Add(new Separator());
        file.Items.Add(Item("_Close", "Ctrl+W", Close));
        menu.Items.Add(file);

        var edit = Top("_Edit");
        undoItem = Item("_Undo", "Ctrl+Z", Document.Undo);
        redoItem = Item("_Redo", "Ctrl+Y", Document.Redo);
        edit.SubmenuOpened += (_, _) =>
        {
            undoItem.Header = Document.UndoName is { } u ? $"_Undo {u}" : "_Undo";
            undoItem.IsEnabled = Document.UndoName is not null;
            redoItem.Header = Document.RedoName is { } r ? $"_Redo {r}" : "_Redo";
            redoItem.IsEnabled = Document.RedoName is not null;
            findItem.Header = FindsCharacter ? "_Find a Character" : "_Find in Manuscript…";
        };
        edit.Items.Add(undoItem);
        edit.Items.Add(redoItem);
        edit.Items.Add(new Separator());
        // Ctrl+F finds a character on the Cast and Voices pages (their own field), and in the manuscript elsewhere.
        findItem = Item("_Find in Manuscript…", "Ctrl+F", Find);
        edit.Items.Add(findItem);
        // Ctrl+Shift+F is always Find in Manuscript (the Mac's ⇧⌘F).
        KeyBindings.Add(new KeyBinding { Gesture = KeyGesture.Parse("Ctrl+Shift+F"), Command = new Relay(FindInManuscript) });
        edit.Items.Add(Item("Find _Next", "F3", () => (Get(PageKind.Manuscript) as ManuscriptPage)?.FindNext(1)));
        edit.Items.Add(Item("Find _Previous", "Shift+F3", () => (Get(PageKind.Manuscript) as ManuscriptPage)?.FindNext(-1)));
        menu.Items.Add(edit);

        var view = Top("_View");
        view.SubmenuOpened += (_, _) => UpdateTypedItems();
        for (int i = 0; i < PageKinds.Numbered.Length; i++)
        {
            var kind = PageKinds.Numbered[i];
            var item = Item(kind.Title(), $"Ctrl+{i + 1}", () => { if (VisiblePages().Contains(kind)) Workspace.Go(kind); });
            view.Items.Add(kind is PageKind.Index or PageKind.Teleprompter ? Typed(item, p => p.ShowsNarrationTools) : item);
        }
        view.Items.Add(Typed(Item("Voi_ces", "Ctrl+5", () => { if (Document.Project.ShowsVoices) Workspace.Go(PageKind.Voices); }), p => p.ShowsVoices));
        // Ctrl+6: the Mac's ⌘4 (Windows keeps Ctrl+4 and Ctrl+5 for the Teleprompter and Voices).
        view.Items.Add(Typed(Item("_Pronunciations", "Ctrl+6", () => { if (Document.Project.ShowsPronunciations) Workspace.Go(PageKind.Pronunciations); }), p => p.ShowsPronunciations));
        view.Items.Add(Item("Re_view", null, () => Workspace.Go(PageKind.Review)));
        view.Items.Add(new Separator());
        foreach (var style in new[] { ColorStyle.Highlight, ColorStyle.Outline, ColorStyle.Underline })
            view.Items.Add(Item(style.Title(), null, () => Document.UpdateSettings(style.Title(), s => s.Style = style)));
        view.Items.Add(Item("Show/Hide _Initials", null, () =>
        {
            bool on = !Document.Project.Settings.ShowInitials;
            Document.UpdateSettings(on ? "Show Initials" : "Hide Initials", s => s.ShowInitials = on);
        }));
        var margin = new MenuItem { Header = "_Margin Shows" };
        foreach (var (label, value) in new[] { ("Initials", MarginNames.Initials), ("First Name", MarginNames.FirstName), ("Full Name", MarginNames.FullName) })
            margin.Items.Add(Item(label, null, () => AppSettings.Update(s => s.MarginNames = value)));
        view.Items.Add(margin);
        var spacing = new MenuItem { Header = "Line _Spacing" };
        spacing.Items.Add(Item("Single", null, () => AppSettings.Update(s => s.LineSpacing = LineSpacing.Single)));
        spacing.Items.Add(Item("Double", null, () => AppSettings.Update(s => s.LineSpacing = LineSpacing.Double)));
        view.Items.Add(spacing);
        view.Items.Add(Item("Serif / Sans Serif Text", null, () => AppSettings.Update(s => s.ReadingSerif = !s.ReadingSerif)));
        view.Items.Add(Item("_Bigger Text", "Ctrl+OemPlus", () => AppSettings.Update(s => s.ReadingFontSize = Math.Min(AppSettings.MaxReadingSize, s.ReadingFontSize + 1))));
        view.Items.Add(Item("S_maller Text", "Ctrl+OemMinus", () => AppSettings.Update(s => s.ReadingFontSize = Math.Max(AppSettings.MinReadingSize, s.ReadingFontSize - 1))));
        view.Items.Add(new Separator());
        var appearance = new MenuItem { Header = "_Appearance" };
        foreach (var a in Enum.GetValues<Appearance>())
            appearance.Items.Add(Item(a.ToString(), null, () => { AppSettings.Update(s => s.Appearance = a); App.Current.ApplyAppearance(); }));
        view.Items.Add(appearance);
        AddStepsMenu(view);
        menu.Items.Add(view);

        AddBookMenu(menu);

        var help = Top("_Help");
        AddHelpItems(help);
        menu.Items.Add(help);
        return menu;
    }

    bool FindsCharacter => Workspace.Page is PageKind.Cast || (Workspace.Page is PageKind.Voices && Document.Project.ShowsVoices);

    void Find()
    {
        if (!FindsCharacter) { FindInManuscript(); return; }
        if (Workspace.Page is PageKind.Cast) (Get(PageKind.Cast) as CastPage)?.FocusCharacterFind();
        else (Get(PageKind.Voices) as VoicesPage)?.FocusCharacterFind();
    }

    void FindInManuscript()
    {
        Workspace.Go(PageKind.Manuscript);
        (Get(PageKind.Manuscript) as ManuscriptPage)?.FocusFind();
    }

    static MenuItem Top(string header) => new() { Header = header };

    MenuItem Item(string header, string? gesture, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        if (gesture is not null)
        {
            var g = KeyGesture.Parse(gesture);
            item.InputGesture = g;
            KeyBindings.Add(new KeyBinding { Gesture = g, Command = new Relay(action) });
        }
        return item;
    }

    sealed class Relay(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
