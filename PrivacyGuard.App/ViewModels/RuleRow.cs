using CommunityToolkit.Mvvm.ComponentModel;
using PrivacyGuard.App.Helpers;
using PrivacyGuard.Core.Models;

namespace PrivacyGuard.App.ViewModels;

/// <summary>One line in the rules list. Wraps a rule and respects the "hide values" toggle.</summary>
public sealed partial class RuleRow : ObservableObject
{
    private readonly Action<Rule, bool> _setEnabled;

    public Rule Rule { get; }

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private bool _hideValue;

    public RuleRow(Rule rule, bool hideValue, Action<Rule, bool> setEnabled)
    {
        Rule = rule;
        _enabled = rule.Enabled;
        _hideValue = hideValue;
        _setEnabled = setEnabled;
    }

    partial void OnEnabledChanged(bool value) => _setEnabled(Rule, value);

    partial void OnHideValueChanged(bool value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
    }

    public string KindLabel => Rule.Kind switch
    {
        RuleKind.Text => Rule.Fuzzy ? "FUZZY TEXT" : "TEXT",
        RuleKind.Regex => "REGEX",
        RuleKind.AppWindow => "APP",
        _ => "BUILT-IN",
    };

    public string Title
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Rule.Label)) return Rule.Label;
            if (Rule.Kind == RuleKind.BuiltIn) return EditorChoices.BuiltInName(Rule.BuiltIn);
            return HideValue ? "Untitled rule" : Rule.Pattern;
        }
    }

    /// <summary>True when the subtitle shows an actual pattern (shown in a code font) rather than a description.</summary>
    public bool SubtitleIsPattern => Rule.Kind is RuleKind.Text or RuleKind.Regex;

    public string Subtitle
    {
        get
        {
            if (Rule.Kind == RuleKind.BuiltIn) return EditorChoices.BuiltInDescription(Rule.BuiltIn);

            var value = HideValue ? new string('•', Math.Min(Rule.Pattern.Length, 12)) : Rule.Pattern;
            if (Rule.Kind == RuleKind.AppWindow) return $"Hides the whole window when its title or app name contains \"{value}\"";

            var options = new List<string>();
            if (Rule.CaseSensitive) options.Add("case sensitive");
            if (Rule.WholeWord) options.Add("whole word");
            if (Rule.Fuzzy) options.Add($"fuzzy {Rule.FuzzyThreshold}%");
            if (Rule.IsAutoDiscovered) options.Add("found on this PC");

            return options.Count == 0 ? value : $"{value}   ·   {string.Join(", ", options)}";
        }
    }
}
