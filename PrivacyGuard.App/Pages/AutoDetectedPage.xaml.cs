using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using PrivacyGuard.App.Services;
using PrivacyGuard.Core.Discovery;
using PrivacyGuard.Core.Models;

namespace PrivacyGuard.App.Pages;

public sealed partial class DiscoveredRow : ObservableObject
{
    public DiscoveredRow(DiscoveredValue value, bool alreadyAdded)
    {
        Category = value.Category;
        Value = value.Value;
        AlreadyAdded = alreadyAdded;
        _selected = !alreadyAdded;
    }

    public string Category { get; }
    public string Value { get; }
    public bool AlreadyAdded { get; }
    public bool CanSelect => !AlreadyAdded;
    public Visibility AlreadyAddedVisibility => AlreadyAdded ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    private bool _selected;
}

public partial class AutoDetectedPage : Page
{
    private readonly RuleBook _book = AppServices.Current.Rules;
    private bool _scanned;

    public ObservableCollection<DiscoveredRow> Items { get; } = [];

    public AutoDetectedPage()
    {
        InitializeComponent();
        DataContext = this;

        Loaded += async (_, _) =>
        {
            if (!_scanned) await ScanAsync();
            else RefreshAddedState();
        };
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        _scanned = true;
        StatusText.Text = "Scanning...";
        AddButton.IsEnabled = false;

        IReadOnlyList<DiscoveredValue> found;
        try
        {
            // Looks up the Wi-Fi name by running a system command, so keep it off the UI thread.
            found = await Task.Run(AutoDiscovery.Discover);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "Could not read this PC's details.";
            AddButton.IsEnabled = true;
            return;
        }

        Items.Clear();
        foreach (var v in found)
            Items.Add(new DiscoveredRow(v, _book.ContainsText(v.Value)));

        StatusText.Text = found.Count == 0 ? "Nothing found." : $"{found.Count} values found.";
        AddButton.IsEnabled = true;
    }

    private void RefreshAddedState()
    {
        var current = Items.Select(i => new DiscoveredValue(i.Category, i.Value)).ToList();
        Items.Clear();
        foreach (var v in current)
            Items.Add(new DiscoveredRow(v, _book.ContainsText(v.Value)));
    }

    private void AddSelected_Click(object sender, RoutedEventArgs e)
    {
        var chosen = Items.Where(i => i.Selected && !i.AlreadyAdded).ToList();
        if (chosen.Count == 0)
        {
            StatusText.Text = "Select at least one value first.";
            return;
        }

        var rules = chosen.Select(i => new Rule
        {
            Kind = RuleKind.Text,
            Label = i.Category,
            Pattern = i.Value,
            WholeWord = true,
            // Short values would match too many unrelated words once fuzzy.
            Fuzzy = i.Value.Length >= 5 && i.Category != "Local IP address",
            FuzzyThreshold = 85,
            IsAutoDiscovered = true,
        });

        _book.AddRange(rules);
        RefreshAddedState();
        StatusText.Text = $"{chosen.Count} added to your rules.";
    }
}
