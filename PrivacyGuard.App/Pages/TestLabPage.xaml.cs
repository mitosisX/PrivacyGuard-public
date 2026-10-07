using System.Windows;
using System.Windows.Controls;
using PrivacyGuard.App.Helpers;
using PrivacyGuard.App.Services;
using PrivacyGuard.Core.Matching;

namespace PrivacyGuard.App.Pages;

public partial class TestLabPage : Page
{
    private const string Sample =
        "Hi, I'm Jane. You can reach me at jane.doe@example.com or +1 555 010 7788.\n" +
        "Wi-Fi: HomeNetwork_5G   Router: 192.168.1.1\n" +
        "Card on file: 4111 1111 1111 1111\n" +
        "api_key = abcd1234efgh5678ijkl";

    public TestLabPage()
    {
        InitializeComponent();
        InputBox.Text = Sample;
        Loaded += (_, _) => Update();
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e) => Update();

    private void RedactedSwitch_Changed(object sender, RoutedEventArgs e) => Update();

    private void Update()
    {
        // Loaded and TextChanged can fire before every control exists.
        if (!IsLoaded && PreviewBox is null) return;

        var text = InputBox.Text;
        var rules = AppServices.Current.Rules.Rules;
        var matches = RuleMatcher.FindMatches(text, rules);

        MatchHighlighter.Render(PreviewBox, text, matches, RedactedSwitch.IsChecked == true);

        var enabled = rules.Count(r => r.Enabled);
        if (enabled == 0)
        {
            SummaryText.Text = "No rules are enabled yet. Add some on the Rules page to see them work here.";
            return;
        }

        if (matches.Count == 0)
        {
            SummaryText.Text = $"No matches. {enabled} rules checked.";
            return;
        }

        // Rule names only, never the matched text, so this line is safe to leave on screen.
        var perRule = matches
            .GroupBy(m => m.Rule.Id)
            .Select(g => $"{(string.IsNullOrWhiteSpace(g.First().Rule.Label) ? "Unnamed rule" : g.First().Rule.Label)}: {g.Count()}")
            .ToList();

        SummaryText.Text = $"{matches.Count} matches from {perRule.Count} rules.   " + string.Join("   ·   ", perRule);
    }
}
