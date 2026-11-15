// The Mac's UI/ProjectWindowFrame.swift: each book's window opens where it was last left, at
// the size it was.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using InnerVoice.Core;

namespace InnerVoice.App.Windows;

/// Where a book's window was: its position (screen pixels), its size (the window's own
/// units) and whether it was maximized. Width and height are the normal size, kept while
/// maximized so un-maximizing goes back to it.
public sealed record ProjectWindowFrame(int X, int Y, double Width, double Height, bool Maximized = false)
{
    /// Still worth restoring: a real size, and on a screen that's there now (a display
    /// unplugged leaves the window where Windows puts it). `scaling` turns the size into
    /// screen pixels.
    public bool Fits(IEnumerable<PixelRect> workingAreas, double scaling = 1) =>
        Width > 100 && Height > 100
        && workingAreas.Any(a => a.Intersects(new PixelRect(X, Y, Math.Max(1, (int)(Width * scaling)), Math.Max(1, (int)(Height * scaling)))));
}

public sealed partial class ProjectWindow
{
    /// The book's key: `Project.UsageKey`, which survives moving or renaming the file.
    string FrameKey => Document.Project.UsageKey();
    bool frameRestored;
    DispatcherTimer? frameSave;

    /// Before the window shows: its saved place and size, if any (not in test runs).
    void RestoreFrame()
    {
        if (AppPaths.IsTestRun) return;
        frameRestored = true;
        if (FrameKey.Length == 0 || !AppSettings.Current.ProjectFrames.TryGetValue(FrameKey, out var f)) return;
        var areas = Screens.All.Select(s => s.WorkingArea).ToList();
        if (!f.Fits(areas, Screens.Primary?.Scaling ?? 1)) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(f.X, f.Y);
        Width = f.Width;
        Height = f.Height;
        if (f.Maximized) WindowState = WindowState.Maximized;
    }

    /// Saved whenever the window moves, resizes or is maximized (after a short pause, so a
    /// drag writes once), never in full screen.
    void WatchFrame()
    {
        if (AppPaths.IsTestRun) return;
        PositionChanged += (_, _) => FrameChanged();
        Resized += (_, _) => FrameChanged();
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) FrameChanged(); };
    }

    void FrameChanged()
    {
        if (!frameRestored || !IsVisible) return;
        frameSave ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => { frameSave!.Stop(); SaveFrame(); });
        frameSave.Stop();
        frameSave.Start();
    }

    void SaveFrame()
    {
        if (!frameRestored || FrameKey.Length == 0 || WindowState is WindowState.FullScreen or WindowState.Minimized) return;
        var all = AppSettings.Current.ProjectFrames;
        bool maximized = WindowState == WindowState.Maximized;
        // While maximized, keep the normal place and size it had.
        var frame = maximized && all.TryGetValue(FrameKey, out var old)
            ? old with { Maximized = true }
            : new ProjectWindowFrame(Position.X, Position.Y, Width, Height, maximized);
        if (all.TryGetValue(FrameKey, out var same) && same == frame) return;
        AppSettings.UpdateQuietly(s => s.ProjectFrames[FrameKey] = frame);
    }
}
