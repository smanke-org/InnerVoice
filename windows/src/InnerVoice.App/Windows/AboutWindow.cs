using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using InnerVoice.App.UI;

namespace InnerVoice.App.Windows;

/// The Windows releases, newest first: the About window lists the last ten.
public static class ReleaseNotes
{
    public sealed record Release(string Version, string Date, string[] Notes);

    public static readonly Release[] All =
    [
        new("0.1.0", "2026-11-10", [
            "First Windows version: the Narration Tools from InnerVoice for Mac.",
            "Opens Word, PDF, RTF, text, Markdown, OpenDocument, EPUB and HTML manuscripts.",
            "Tag Speakers and Build Summaries with a local AI model; nothing leaves your PC.",
            "Projects open on both Windows and the Mac.",
        ]),
    ];
}

public sealed class AboutWindow : Window
{
    public static void Show(Window owner) => new AboutWindow().ShowDialog(owner);

    AboutWindow()
    {
        Title = "About InnerVoice";
        Width = 520;
        Height = 560;
        CanResize = false;
        FontFamily = Look.UIFamily;
        Icon = Look.WindowIcon;
        Background = Look.Brush(Look.Paper);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var col = new StackPanel { Spacing = Look.Md, Margin = new Thickness(Look.Xxl) };
        try
        {
            var image = new Image { Source = new Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://InnerVoice/Assets/AppIcon-512.png"))), Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Center };
            col.Children.Add(image);
        }
        catch { }
        col.Children.Add(Ui.Text("InnerVoice", 24, FontWeight.Bold).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center));
        col.Children.Add(Ui.Caption($"Version {App.Version} for Windows").Also(t => t.HorizontalAlignment = HorizontalAlignment.Center));
        col.Children.Add(Ui.Caption("© 2026 Steve Manke. All rights reserved.").Also(t => t.HorizontalAlignment = HorizontalAlignment.Center));
        col.Children.Add(Ui.Headline("What's New"));
        foreach (var r in ReleaseNotes.All.Take(10))
        {
            col.Children.Add(Ui.Text($"{r.Version} · {r.Date}", 13, FontWeight.SemiBold));
            foreach (var n in r.Notes) col.Children.Add(Ui.Text("• " + n, 12, wrap: TextWrapping.Wrap));
        }
        col.Children.Add(Ui.Caption("Uses llama.cpp (MIT), Avalonia (MIT), PdfPig (Apache 2.0), AngleSharp (MIT), RtfPipe (MIT), QuestPDF (Community license) and Open XML SDK (MIT).", TextWrapping.Wrap));
        Content = Ui.Scroll(col);
    }
}
