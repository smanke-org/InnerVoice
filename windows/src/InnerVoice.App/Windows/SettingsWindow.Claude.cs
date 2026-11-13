using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.AI;
using InnerVoice.App.UI;
using InnerVoice.Core.AI;

namespace InnerVoice.App.Windows;

/// Claude in Settings › AI: marked Cloud, with Set Up… until it has a key and the narrator's
/// yes, then a model menu. The Mac's ClaudeModelRow and ClaudeSetupSheet.
public sealed partial class SettingsWindow
{
    bool refreshedClaudeModels;

    /// The orange Cloud mark used wherever Claude appears.
    public static Border CloudBadge()
    {
        var pill = Ui.Pill("Cloud", Look.Retake);
        ToolTip.SetTip(pill, "Runs on Anthropic's computers, not this PC");
        return pill;
    }

    Control ClaudeRow()
    {
        var mm = ModelManager.Shared;
        var account = ClaudeAccount.Shared;
        bool chosen = mm.SelectionIsClaude;
        var title = Ui.Row(Look.Sm, Ui.Text("Claude", 14, FontWeight.SemiBold), CloudBadge(), Ui.Caption("Anthropic"));
        if (chosen) title.Children.Add(Ui.Pill("In use", Look.Gray));
        var col = Ui.Column(Look.Xs, title,
            Ui.Text("Anthropic's models, run in the cloud. The most accurate choice and nothing to download, but each passage of the book is sent to Anthropic, it needs an API key, and Anthropic charges for what you use. Each book asks before anything is sent.",
                    12, wrap: TextWrapping.Wrap));
        var actions = Ui.Row(Look.Sm);
        if (account.IsSetUp)
        {
            var models = account.Models;
            string current = chosen ? mm.Selection[Claude.SelectionPrefix.Length..] : Claude.DefaultModelID;
            var picker = new ComboBox
            {
                ItemsSource = models.Select(m => m.Name).ToList(),
                SelectedIndex = Math.Max(0, models.ToList().FindIndex(m => m.Id == current)),
                MinWidth = 200,
            };
            ToolTip.SetTip(picker, "Which Claude model reads the book");
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedIndex < 0 || picker.SelectedIndex >= models.Count) return;
                var id = models[picker.SelectedIndex].Id;
                if (mm.SelectionIsClaude && mm.Selection != Claude.SelectionPrefix + id) mm.Selection = Claude.SelectionPrefix + id;
            };
            actions.Children.Add(picker);
            if (!chosen)
                actions.Children.Add(Ui.Button("Use Claude", () =>
                {
                    int i = picker.SelectedIndex;
                    mm.Selection = Claude.SelectionPrefix + (i >= 0 && i < models.Count ? models[i].Id : Claude.DefaultModelID);
                }, primary: true));
            actions.Children.Add(Ui.Button("Disconnect", account.Disconnect, tip: "Forget the key; nothing more is sent"));
            if (!refreshedClaudeModels)
            {
                refreshedClaudeModels = true;
                _ = RefreshClaudeModels();
            }
        }
        else actions.Children.Add(Ui.Button("Set Up…", () => _ = SetUpClaude()));
        col.Children.Add(actions);
        return Ui.Card(col, Look.Md);
    }

    static async Task RefreshClaudeModels()
    {
        try { await ClaudeAccount.Shared.RefreshModels(); } catch { } // keep the list we have
    }

    /// The cloud warning, the key, and the narrator's yes. Checking the key lists models only;
    /// no book text is sent.
    Task SetUpClaude() => ClaudeSetupDialog().ShowDialog(this);

    public static Window ClaudeSetupDialog()
    {
        var dialog = new Window
        {
            Title = "Set Up Claude", Width = 500, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            FontFamily = Look.UIFamily, Background = Look.Brush(Look.Paper),
        };
        var howToCheck = new Button { Content = "How to check", Classes = { "link" }, Padding = new Thickness(0), Background = Brushes.Transparent };
        howToCheck.Click += (_, _) => _ = dialog.Launcher.LaunchUriAsync(new Uri(Claude.PrivacyHelp));
        var warning = new Border
        {
            Background = Look.Brush(Look.Retake.WithAlpha(Look.IsDark ? 0.18 : 0.08)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(Look.Md),
            Child = Ui.Column(Look.Xs, Ui.Text(Claude.Warning, 13, wrap: TextWrapping.Wrap), howToCheck),
        };
        var key = new TextBox { PasswordChar = '•', PlaceholderText = "sk-ant-…" };
        var getKey = new Button { Content = "Get a key in the Claude Console", Classes = { "link" }, Padding = new Thickness(0), Background = Brushes.Transparent };
        getKey.Click += (_, _) => _ = dialog.Launcher.LaunchUriAsync(new Uri(Claude.KeysPage));
        var agree = new CheckBox { Content = "I understand that book text I send to Claude goes to Anthropic." };
        var problem = Ui.Text("", 12, brush: Look.Brush(Look.Attention), wrap: TextWrapping.Wrap);
        problem.IsVisible = false;
        var connect = new Button { Content = "Connect", IsDefault = true, IsEnabled = false };
        connect.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => dialog.Close();
        void Update() => connect.IsEnabled = !string.IsNullOrWhiteSpace(key.Text) && agree.IsChecked == true;
        key.TextChanged += (_, _) => Update();
        agree.IsCheckedChanged += (_, _) => Update();
        connect.Click += async (_, _) =>
        {
            connect.IsEnabled = false;
            connect.Content = "Checking…";
            problem.IsVisible = false;
            var account = ClaudeAccount.Shared;
            try
            {
                account.Save(key.Text ?? "", consent: true);
                await account.RefreshModels();
                if (!ModelManager.Shared.SelectionIsClaude) ModelManager.Shared.Selection = Claude.SelectionPrefix + Claude.DefaultModelID;
                dialog.Close();
            }
            catch (Exception e)
            {
                account.Save("", consent: false);
                problem.Text = e.Message;
                problem.IsVisible = true;
                connect.Content = "Connect";
                Update();
            }
        };
        var buttons = Ui.Row(Look.Sm, cancel, connect);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(Look.Xl), Spacing = Look.Md,
            Children =
            {
                Ui.Row(Look.Sm, Ui.Title("Set Up Claude"), CloudBadge()),
                warning,
                Ui.Column(Look.Xs, Ui.Headline("API key"), key, getKey),
                agree, problem, buttons,
            },
        };
        return dialog;
    }
}
