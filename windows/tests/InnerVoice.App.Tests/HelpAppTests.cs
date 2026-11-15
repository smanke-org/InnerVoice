using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;
using InnerVoice.App.Help;

namespace InnerVoice.App.Tests;

/// The Help window (the Mac's Help.swift).
public class HelpAppTests
{
    /// Ctrl+F (the Mac's ⌘F) puts the cursor in Search Help with its text selected, from
    /// anywhere in the window.
    [AvaloniaFact]
    public void CtrlFFocusesSearch()
    {
        HelpWindow.Show();
        var w = HelpWindow.Current!;
        try
        {
            Dispatcher.UIThread.RunJobs();
            var search = w.GetLogicalDescendants().OfType<TextBox>().Single(t => t.PlaceholderText == "Search Help");
            search.Text = "voices";
            w.GetLogicalDescendants().OfType<ListBox>().First().Focus();
            Assert.False(search.IsFocused);
            w.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.True(search.IsFocused);
            Assert.Equal(0, search.SelectionStart);
            Assert.Equal("voices".Length, search.SelectionEnd);
        }
        finally { w.Hide(); }
    }
}

public class AIReviewSearchTests
{
    [Xunit.Fact]
    public void SearchMatchesEveryWordAnyCase()
    {
        Xunit.Assert.True(InnerVoice.App.Windows.AIReviewWindow.Matches("Gray I'll take the trade. Ch 2 ¶73", "gray TRADE"));
        Xunit.Assert.False(InnerVoice.App.Windows.AIReviewWindow.Matches("Gray Good idea", "gray trade"));
    }
}
