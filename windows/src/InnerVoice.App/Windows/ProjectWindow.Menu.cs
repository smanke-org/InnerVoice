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
    MenuItem undoItem = null!, redoItem = null!;

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
        };
        edit.Items.Add(undoItem);
        edit.Items.Add(redoItem);
        edit.Items.Add(new Separator());
        edit.Items.Add(Item("_Find", "Ctrl+F", () => { Workspace.Go(PageKind.Manuscript); (Get(PageKind.Manuscript) as ManuscriptPage)?.FocusFind(); }));
        edit.Items.Add(Item("Find _Next", "F3", () => (Get(PageKind.Manuscript) as ManuscriptPage)?.FindNext(1)));
        edit.Items.Add(Item("Find _Previous", "Shift+F3", () => (Get(PageKind.Manuscript) as ManuscriptPage)?.FindNext(-1)));
        menu.Items.Add(edit);

        var view = Top("_View");
        for (int i = 0; i < PageKinds.Numbered.Length; i++)
        {
            var kind = PageKinds.Numbered[i];
            var item = Item(kind.Title(), $"Ctrl+{i + 1}", () => Workspace.Go(kind));
            view.Items.Add(item);
        }
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
