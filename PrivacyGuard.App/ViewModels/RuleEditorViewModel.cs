using CommunityToolkit.Mvvm.ComponentModel;
using PrivacyGuard.App.Helpers;
using PrivacyGuard.Core.Matching;
using PrivacyGuard.Core.Models;

namespace PrivacyGuard.App.ViewModels;

/// <summary>Drives the add/edit rule window, including the live "try it" preview.</summary>
public sealed partial class RuleEditorViewModel : ObservableObject
{
    private const string DefaultSample =
        "Contact: jane.doe@example.com\nWi-Fi: HomeNetwork_5G\nServer: 192.168.1.24\nCard: 4111 1111 1111 1111\nKey: AKIAIOSFODNN7EXAMPLE";

    private readonly Rule _original;

    public RuleEditorViewModel(Rule rule, bool isNew)
    {
        _original = rule;
        IsNew = isNew;

        _label = rule.Label;
        _kind = rule.Kind;
        _pattern = rule.Pattern;
        _builtIn = rule.BuiltIn;
        _caseSensitive = rule.CaseSensitive;
        _wholeWord = rule.WholeWord;
        _fuzzy = rule.Fuzzy;
        _fuzzyThreshold = rule.FuzzyThreshold;
        _sampleText = DefaultSample;

        PropertyChanged += (_, e) =>
        {
            // Preview outputs must not retrigger the preview.
            if (e.PropertyName is nameof(Error) or nameof(HasError) or nameof(Summary) or nameof(CanSave)) return;
            RefreshPreview();
        };
    }

    public bool IsNew { get; }

    public string WindowTitle => IsNew ? "Add rule" : "Edit rule";

    [ObservableProperty] private string _label;
    [ObservableProperty] private RuleKind _kind;
    [ObservableProperty] private string _pattern;
    [ObservableProperty] private BuiltInPattern _builtIn;
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private bool _wholeWord;
    [ObservableProperty] private bool _fuzzy;
    [ObservableProperty] private int _fuzzyThreshold;
    [ObservableProperty] private string _sampleText;

    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _summary = "";

    public IReadOnlyList<TextMatch> Matches { get; private set; } = [];

    /// <summary>Raised after every preview refresh so the window can redraw the highlighted sample.</summary>
    public event EventHandler? PreviewChanged;

    public bool IsText => Kind == RuleKind.Text;
    public bool IsRegex => Kind == RuleKind.Regex;
    public bool IsBuiltIn => Kind == RuleKind.BuiltIn;
    public bool IsAppWindow => Kind == RuleKind.AppWindow;
    public bool ShowPattern => Kind != RuleKind.BuiltIn;
    public bool ShowTextOptions => Kind is RuleKind.Text or RuleKind.Regex;
    public bool ShowTryIt => Kind != RuleKind.AppWindow;
    public bool ShowThreshold => IsText && Fuzzy;
    public bool HasError => Error is not null;
    public bool CanSave => Error is null;
    public string BuiltInDescription => EditorChoices.BuiltInDescription(BuiltIn);

    public string PatternHint => Kind switch
    {
        RuleKind.Regex => @"Examples:  ACC-\d{4}   matches account numbers.   \b(?:admin|root)\w*   matches usernames.",
        RuleKind.AppWindow => "Part of the window title or app name, for example MetaMask, Exodus or Ledger Live. Not case sensitive.",
        _ => "The exact words to hide, such as a name or Wi-Fi network.",
    };

    partial void OnKindChanged(RuleKind value)
    {
        OnPropertyChanged(nameof(IsText));
        OnPropertyChanged(nameof(IsRegex));
        OnPropertyChanged(nameof(IsBuiltIn));
        OnPropertyChanged(nameof(IsAppWindow));
        OnPropertyChanged(nameof(ShowPattern));
        OnPropertyChanged(nameof(ShowTextOptions));
        OnPropertyChanged(nameof(ShowTryIt));
        OnPropertyChanged(nameof(ShowThreshold));
        OnPropertyChanged(nameof(PatternHint));
    }

    partial void OnFuzzyChanged(bool value) => OnPropertyChanged(nameof(ShowThreshold));

    partial void OnBuiltInChanged(BuiltInPattern value) => OnPropertyChanged(nameof(BuiltInDescription));

    partial void OnErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(CanSave));
    }

    public void RefreshPreview()
    {
        var rule = ToRule();
        Error = RuleMatcher.Validate(rule);

        if (Error is null && rule.Kind == RuleKind.AppWindow)
        {
            // Show which open windows the rule would cover right now.
            Matches = [];
            var hits = PrivacyGuard.Engine.Native.DesktopWindows.ListOpenWindows()
                .Where(w => AppRuleMatcher.Matches(rule, w.Title, w.AppName))
                .Select(w => string.IsNullOrWhiteSpace(w.Title) ? w.AppName : w.Title)
                .Distinct()
                .Take(5)
                .ToList();
            Summary = hits.Count == 0
                ? "No open window matches right now. The rule applies the moment one opens."
                : "Would cover now: " + string.Join(",  ", hits);
        }
        else if (Error is null)
        {
            Matches = RuleMatcher.FindMatches(SampleText, rule);
            Summary = Matches.Count switch
            {
                0 => "No matches in the sample text.",
                1 => "1 match in the sample text.",
                var n => $"{n} matches in the sample text.",
            };
        }
        else
        {
            Matches = [];
            Summary = "";
        }

        PreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    public Rule ToRule()
    {
        var rule = _original.Clone();
        rule.Label = Label.Trim();
        rule.Kind = Kind;
        rule.Pattern = Kind == RuleKind.BuiltIn ? "" : Pattern;
        rule.BuiltIn = BuiltIn;
        rule.CaseSensitive = Kind != RuleKind.AppWindow && CaseSensitive;
        rule.WholeWord = Kind != RuleKind.AppWindow && WholeWord;
        rule.Fuzzy = Kind == RuleKind.Text && Fuzzy;
        rule.FuzzyThreshold = Math.Clamp(FuzzyThreshold, 50, 100);
        return rule;
    }
}
