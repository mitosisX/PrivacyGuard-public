using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PrivacyGuard.App.Helpers;
using PrivacyGuard.App.Services;
using PrivacyGuard.App.ViewModels;
using PrivacyGuard.App.Views;
using PrivacyGuard.Core.Models;
using Wpf.Ui.Controls;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using MenuItem = System.Windows.Controls.MenuItem;

namespace PrivacyGuard.App.Pages;

public partial class RulesPage : Page
{
    private readonly RuleBook _book = AppServices.Current.Rules;
    private bool _hideValues;

    public ObservableCollection<RuleRow> Rows { get; } = [];

    public RulesPage()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        Rows.Clear();
        foreach (var rule in _book.Rules)
            Rows.Add(new RuleRow(rule, _hideValues, _book.SetEnabled));

        var active = _book.Rules.Count(r => r.Enabled);
        EmptyPanel.Visibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SubtitleText.Text = Rows.Count == 0
            ? "Tell PrivacyGuard what to hide."
            : $"{active} of {Rows.Count} rules active. While live protection is on, anything that matches is covered on screen.";
    }

    // ---- add ----------------------------------------------------------------

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void AddKind_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse<RuleKind>(tag, out var kind)) return;

        var rule = new Rule { Kind = kind, WholeWord = kind is RuleKind.Regex or RuleKind.BuiltIn, BuiltIn = BuiltInPattern.CryptoAddress };
        OpenEditor(rule, isNew: true);
    }

    private void AddRecommended_Click(object sender, RoutedEventArgs e)
    {
        var missing = new[]
            {
                BuiltInPattern.CryptoAddress, BuiltInPattern.Email, BuiltInPattern.Ipv4, BuiltInPattern.Phone,
                BuiltInPattern.ApiKey, BuiltInPattern.CreditCard, BuiltInPattern.Url,
            }
            .Where(p => !_book.ContainsBuiltIn(p))
            .Select(p => new Rule { Kind = RuleKind.BuiltIn, BuiltIn = p, Label = EditorChoices.BuiltInName(p) })
            .ToList();

        if (missing.Count == 0)
        {
            System.Windows.MessageBox.Show("All the recommended detectors are already in your list.",
                "PrivacyGuard", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _book.AddRange(missing);
        Reload();
    }

    private void ScanPc_Click(object sender, RoutedEventArgs e)
    {
        // Jump to the "Found on this PC" page through the parent navigation view.
        if (Window.GetWindow(this) is MainWindow window)
            window.RootNavigation.Navigate(typeof(AutoDetectedPage));
    }

    // ---- edit / delete --------------------------------------------------------

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RuleRow row })
            OpenEditor(row.Rule.Clone(), isNew: false);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RuleRow row }) return;

        var name = string.IsNullOrWhiteSpace(row.Rule.Label) ? "this rule" : $"\"{row.Rule.Label}\"";
        var answer = System.Windows.MessageBox.Show(
            $"Delete {name}? Text it matched will no longer be hidden.",
            "Delete rule", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        _book.Remove(row.Rule);
        Reload();
    }

    private void OpenEditor(Rule rule, bool isNew)
    {
        var window = new RuleEditorWindow(rule, isNew) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true || window.Result is null) return;

        if (isNew) _book.Add(window.Result);
        else _book.Update(window.Result);

        Reload();
    }

    // ---- hide values ----------------------------------------------------------

    private void ToggleHide_Click(object sender, RoutedEventArgs e)
    {
        _hideValues = !_hideValues;
        foreach (var row in Rows) row.HideValue = _hideValues;

        HideButton.Content = _hideValues ? "Show values" : "Hide values";
        HideButton.Icon = new SymbolIcon(_hideValues ? SymbolRegular.Eye24 : SymbolRegular.EyeOff24);
    }
}
