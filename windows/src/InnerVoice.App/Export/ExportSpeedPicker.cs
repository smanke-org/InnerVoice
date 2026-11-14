// Ported from ExportSpeedPicker in the Mac's Sources/InnerVoice/Speech/ExportSpeed.swift: one
// picker per engine, in Settings › Voices and in Export Audio for the book's engines.

using Avalonia.Controls;
using InnerVoice.App.Speech;
using InnerVoice.Core;
using InnerVoice.Core.Speech;

namespace InnerVoice.App.Export;

public static class ExportSpeedPicker
{
    /// The engine's Export Speed as a drop-down; choosing saves it at once.
    public static ComboBox Make(VoiceEngine engine)
    {
        var current = VoiceSetup.SpeedFor(engine);
        var box = new ComboBox
        {
            ItemsSource = ExportSpeeds.All.Select(s => s.Title()).ToList(),
            SelectedIndex = Array.IndexOf(ExportSpeeds.All, current),
            MinWidth = 300,
        };
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            var speed = ExportSpeeds.All[box.SelectedIndex];
            if (speed != VoiceSetup.SpeedFor(engine)) VoiceSetup.SetExportSpeed(engine, speed);
        };
        ToolTip.SetTip(box, $"How many lines Export Audio makes at once with {engine.Title()}. Slowest makes one at a time.");
        return box;
    }
}
