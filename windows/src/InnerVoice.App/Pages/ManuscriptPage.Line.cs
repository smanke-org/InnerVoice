using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Pages;

public sealed partial class ManuscriptPage
{
    /// What the narrator sees on clicking a line: who speaks it and how sure that is.
    void ShowLinePopover(Line line, Rect rect)
    {
        var p = Document.Project;
        var speaker = p.Character(line.SpeakerID);
        bool needs = p.NeedsYou(line);
        var who = speaker is null ? "Who's Speaking?" : speaker.Name + (needs ? "?" : "");
        var color = needs ? Look.Attention : Look.Hex(speaker?.ColorHex);
        var body = Ui.Column(Look.Sm,
            Ui.Row(Look.Sm, Ui.Dot(needs ? CharacterPalette.AttentionHex : speaker?.ColorHex, 12), Ui.Headline(who)),
            Ui.Caption($"{line.Kind.Title()} · {line.Source.Title()}" + (line.Source is AssignmentSource.Ai or AssignmentSource.Rule or AssignmentSource.Exchange ? $" · {line.Confidence:P0} sure" : "")),
            Ui.Caption(Document.Index.Reference(line.ParagraphID)));
        body.MinWidth = 220;
        var flyout = new Flyout { Content = body, Placement = PlacementMode.Bottom };
        var anchor = AnchorAt(rect);
        flyout.Closed += (_, _) => RemoveAnchor(anchor);
        flyout.ShowAt(anchor);
    }

    /// An invisible control over the clicked words, for a flyout to point at.
    Control AnchorAt(Rect rect)
    {
        var overlay = AnchorLayer();
        var point = Canvas.TranslatePoint(rect.Position, overlay) ?? rect.Position;
        var anchor = new Border { Width = Math.Max(1, rect.Width), Height = Math.Max(1, rect.Height), Background = Brushes.Transparent, IsHitTestVisible = false };
        Avalonia.Controls.Canvas.SetLeft(anchor, point.X);
        Avalonia.Controls.Canvas.SetTop(anchor, point.Y);
        overlay.Children.Add(anchor);
        return anchor;
    }

    readonly Avalonia.Controls.Canvas anchorLayer = new() { IsHitTestVisible = false };

    Avalonia.Controls.Canvas AnchorLayer() => anchorLayer;

    void RemoveAnchor(Control anchor) => anchorLayer.Children.Remove(anchor);
}
