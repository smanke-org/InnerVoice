using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Styling;
using Avalonia.Threading;
using InnerVoice.App.Document;
using InnerVoice.App.Export;
using InnerVoice.App.Pages;
using InnerVoice.App.Windows;
using InnerVoice.Core;
using Xunit;

namespace InnerVoice.App.Tests;

/// Casting Style in the Windows app (docs/CASTING-STYLE.md): Three Narrators and their
/// scripts, Just Me cues, and the pages that show them (snapshots with INNERVOICE_SNAP_DIR).
public class CastingStyleAppTests
{
    static void Pump()
    {
        for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    static string Text(List<Exporter.Para> paras) => string.Join("\n", paras.Select(p => string.Concat(p.Runs.Select(r => r.Text))));

    [AvaloniaFact]
    public void ThreeNarratorsReadTheirPartsAndScriptsFollow()
    {
        var doc = ProjectDocument.Unsaved(TestApp.Sample());
        var tom = doc.Project.Characters.First(c => c.Name.StartsWith("Tom"));
        var mara = doc.Project.Characters.First(c => c.Name.StartsWith("Mara"));
        doc.SetGender([tom.Id], CharacterGender.Male);
        doc.SetGender([mara.Id], CharacterGender.Female);
        doc.SetCastingStyle(CastingStyle.Shared);
        doc.SetUpThreeNarrators();
        Assert.All(new[] { VoiceRole.Narrator, VoiceRole.Men, VoiceRole.Women }, r => Assert.Contains(doc.Project.Narrators, n => n.Role == r));
        var men = doc.Project.Narrators.First(n => n.Role == VoiceRole.Men);
        Assert.Equal(men.Id, doc.EffectiveNarrator(doc.Project.Character(tom.Id)!)?.Id);
        // A narrator cast by hand still wins over the part.
        var women = doc.Project.Narrators.First(n => n.Role == VoiceRole.Women);
        doc.UpdateCharacter(tom.Id, "Cast Narrator", c => c.NarratorID = women.Id);
        Assert.Equal(women.Id, doc.EffectiveNarrator(doc.Project.Character(tom.Id)!)?.Id);
        doc.Undo();
        var script = Text(Exporter.Build(new Exporter.Options { NarratorID = men.Id, IncludeCues = false, IncludeSummaries = false }, doc));
        string LineText(Guid who) { var l = doc.Project.Lines.First(l => l.SpeakerID == who && l.Kind == LineKind.Spoken); return doc.Project.TextOf(l, doc.Index); }
        Assert.Contains(LineText(tom.Id), script);
        Assert.DoesNotContain(LineText(mara.Id), script);
        // Undo takes it all back, narrators included.
        for (int i = 0; i < 4; i++) doc.Undo();
        Assert.Null(doc.Project.Settings.CastingStyle);
        Assert.DoesNotContain(doc.Project.Narrators, n => n.Role is not null);
    }

    [AvaloniaFact]
    public void JustMeShadesForTheNarratorsVoice()
    {
        var doc = ProjectDocument.Unsaved(TestApp.Sample());
        var tom = doc.Project.Characters.First(c => c.Name.StartsWith("Tom"));
        var mara = doc.Project.Characters.First(c => c.Name.StartsWith("Mara"));
        doc.SetGender([tom.Id], CharacterGender.Male);
        doc.SetGender([mara.Id], CharacterGender.Female);
        doc.SetCastingStyle(CastingStyle.OneVoice);
        doc.SetNarratorGender(CharacterGender.Female);
        var shades = doc.Shades();
        Assert.True(shades[tom.Id].Pitch < 0);
        Assert.False(shades.ContainsKey(mara.Id));
        Assert.NotNull(shades[tom.Id].Arrow);
        doc.SetShade(tom.Id, Shade.Make(-1, 1, "gruff"));
        Assert.Equal("lower, quicker, gruff", doc.Shades()[tom.Id].Cue);
    }

    /// A click on a name in the margin picks every line in its paragraph, and Enter in Assign
    /// To confirms the guess for all of them.
    [AvaloniaFact]
    public void MarginNameAssignsTheWholeParagraph()
    {
        var doc = ProjectDocument.Unsaved(TestApp.Sample());
        var w = new ProjectWindow(doc) { Width = 1280, Height = 820 };
        w.Show();
        w.Workspace.Go(PageKind.Manuscript);
        Pump();
        var canvas = ((ManuscriptPage)w.FindPage(PageKind.Manuscript)!).Canvas;
        var para = doc.Project.Chapters.SelectMany(c => c.Paragraphs).First(p => doc.Project.Lines.Count(l => l.ParagraphID == p.Id) >= 2);
        var lines = doc.Project.Lines.Where(l => l.ParagraphID == para.Id).ToList();
        canvas.ScrollToParagraph(para.Id);
        Pump();
        var at = canvas.TranslatePoint(canvas.MarginPointOf(lines[0])!.Value, w)!.Value;
        Assert.Equal(lines[0].Id, canvas.MarginLineAt(canvas.MarginPointOf(lines[0])!.Value)?.Id);
        w.MouseDown(at, Avalonia.Input.MouseButton.Left);
        w.MouseUp(at, Avalonia.Input.MouseButton.Left);
        Pump();
        Assert.Equal(lines.Select(l => l.Id).ToHashSet(), canvas.SelectedLines);
        // Assign To for the paragraph: Enter gives its best guess to every line.
        var ids = lines.Select(l => l.Id).ToList();
        Guid? assigned = null;
        var panel = new InnerVoice.App.Controls.AssignPanel(doc, ids, (s, _) => assigned = s, () => { }, isParagraph: true);
        var box = panel.GetLogicalDescendants().OfType<Avalonia.Controls.TextBox>().First();
        box.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
        var after = doc.Project.Lines.Where(l => l.ParagraphID == para.Id).ToList();
        Assert.NotNull(assigned);
        Assert.All(after, l => { Assert.Equal(assigned, l.SpeakerID); Assert.Equal(AssignmentSource.User, l.Source); });
        w.Close();
    }

    /// Ctrl-click two names in the margin picks both paragraphs without asking; a click on
    /// a picked name then keeps them all for one Assign To.
    [AvaloniaFact]
    public void CtrlClickPicksSeveralParagraphs()
    {
        var doc = ProjectDocument.Unsaved(TestApp.Sample());
        var w = new ProjectWindow(doc) { Width = 1280, Height = 820 };
        w.Show();
        w.Workspace.Go(PageKind.Manuscript);
        Pump();
        var canvas = ((ManuscriptPage)w.FindPage(PageKind.Manuscript)!).Canvas;
        var paras = doc.Project.Chapters.SelectMany(c => c.Paragraphs).Where(p => doc.Project.Lines.Any(l => l.ParagraphID == p.Id)).Take(2).ToList();
        canvas.ScrollToParagraph(paras[0].Id);
        Pump();
        var expected = new HashSet<Guid>();
        foreach (var para in paras)
        {
            var first = doc.Project.Lines.First(l => l.ParagraphID == para.Id);
            expected.UnionWith(doc.Project.Lines.Where(l => l.ParagraphID == para.Id).Select(l => l.Id));
            var at = canvas.TranslatePoint(canvas.MarginPointOf(first)!.Value, w)!.Value;
            w.MouseDown(at, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.Control);
            w.MouseUp(at, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.Control);
            Pump();
        }
        Assert.Equal(expected, canvas.SelectedLines);
        // A plain click on a picked name asks for them all, so the pick stays.
        var again = canvas.TranslatePoint(canvas.MarginPointOf(doc.Project.Lines.First(l => l.ParagraphID == paras[0].Id))!.Value, w)!.Value;
        w.MouseDown(again, Avalonia.Input.MouseButton.Left);
        w.MouseUp(again, Avalonia.Input.MouseButton.Left);
        Pump();
        Assert.Equal(expected, canvas.SelectedLines);
        w.Close();
    }

    /// Ctrl-click picks several cast cards; the bar's Male sets every one in one undo step.
    [AvaloniaFact]
    public void CastPicksSetGenderForAll()
    {
        var doc = ProjectDocument.Unsaved(TestApp.Sample());
        var w = new ProjectWindow(doc) { Width = 1280, Height = 820 };
        w.Show();
        w.Workspace.Go(PageKind.Cast);
        Pump();
        var cast = (CastPage)w.FindPage(PageKind.Cast)!;
        var ids = doc.Project.Characters.Take(2).Select(c => c.Id).ToList();
        Assert.Equal(2, ids.Count);
        foreach (var id in ids) cast.CardClicked(id, Avalonia.Input.KeyModifiers.Control);
        cast.CardClicked(ids[1], Avalonia.Input.KeyModifiers.Control);
        Assert.Equal([ids[0]], cast.Picked);
        cast.CardClicked(ids[1], Avalonia.Input.KeyModifiers.Control);
        Pump();
        Assert.Equal(ids.ToHashSet(), cast.Picked.ToHashSet());
        var male = cast.GetLogicalDescendants().OfType<Avalonia.Controls.Button>().First(b => b.Content as string == "Male");
        male.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.All(ids, id => Assert.Equal(CharacterGender.Male, doc.Project.Character(id)!.Gender));
        doc.Undo();
        Assert.All(ids, id => Assert.Null(doc.Project.Character(id)!.Gender));
        w.Close();
    }

    [AvaloniaFact]
    public void Pages()
    {
        foreach (var (variant, name) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            Avalonia.Application.Current!.RequestedThemeVariant = variant;
            var doc = ProjectDocument.Unsaved(TestApp.Sample());
            var tom = doc.Project.Characters.First(c => c.Name.StartsWith("Tom"));
            doc.SetGender([tom.Id], CharacterGender.Male);
            var w = new ProjectWindow(doc) { Width = 1280, Height = 820 };
            w.Show();
            Pump();
            void Shot(string file)
            {
                Pump();
                if (Environment.GetEnvironmentVariable("INNERVOICE_SNAP_DIR") is { } dir)
                {
                    Directory.CreateDirectory(dir);
                    w.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"{file}-{name}.png"), (int?)null);
                }
            }
            doc.SetCastingStyle(CastingStyle.Shared);
            doc.SetUpThreeNarrators();
            w.Workspace.Go(PageKind.Cast);
            Shot("Cast-ThreeNarrators");
            doc.SetCastingStyle(CastingStyle.OneVoice);
            doc.SetNarratorGender(CharacterGender.Female);
            w.Workspace.Go(PageKind.Cast);
            Shot("Cast-JustMe");
            (w.FindPage(PageKind.Cast) as CastPage)?.ShowCharacter(tom.Id);
            Shot("Character-JustMe");
            w.Workspace.Go(PageKind.Manuscript);
            Shot("Manuscript-JustMe");
            w.Workspace.Go(PageKind.Teleprompter);
            Shot("Teleprompter-JustMe");
            w.Close();
        }
    }
}
