using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.UI;

namespace InnerVoice.App.Windows;

/// The toolbar's Cloud Usage button (a bar chart): tokens, cost and what's left for the cloud
/// models this book uses, in a popover. Shown only while Claude is the chosen model or the
/// book has ElevenLabs turned on.
public sealed partial class ProjectWindow
{
    Button? cloudUsage;
    Avalonia.Controls.Shapes.Path? cloudUsageIcon;

    Control CloudUsageButton()
    {
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M0,12 h3 v-7 h-3 z M5,12 h3 v-12 h-3 z M10,12 h3 v-5 h-3 z"),
            Fill = Look.Brush(Look.Ink), Width = 13, Height = 12, Stretch = Stretch.Uniform,
        };
        var button = new Button
        {
            Content = Ui.Row(Look.Xs, icon, Ui.Text("Cloud Usage", 13)),
            Margin = new Thickness(Look.Sm, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, "Tokens, cost and what's left for the cloud models this book uses");
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        flyout.Opening += (_, _) => flyout.Content = new CloudUsageView(Document.Project);
        button.Flyout = flyout;
        cloudUsage = button;
        cloudUsageIcon = icon;
        RefreshCloudUsageButton();
        return button;
    }

    void RefreshCloudUsageButton()
    {
        if (cloudUsage is not null) cloudUsage.IsVisible = CloudInUse.Any(Document.Project);
        if (cloudUsageIcon is not null) cloudUsageIcon.Fill = Look.Brush(Look.Ink); // light or dark
    }

    /// Checks and snapshots: the popover as it would open now.
    public CloudUsageView CloudUsagePopover() => new(Document.Project);
    public bool CloudUsageButtonShown => cloudUsage?.IsVisible == true;
}
