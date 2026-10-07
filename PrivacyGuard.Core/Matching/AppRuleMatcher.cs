using PrivacyGuard.Core.Models;

namespace PrivacyGuard.Core.Matching;

/// <summary>Decides whether an app-window rule applies to a window.</summary>
public static class AppRuleMatcher
{
    /// <param name="title">The window title, for example "MetaMask Notification".</param>
    /// <param name="processName">The app's executable name without ".exe", for example "Exodus".</param>
    public static bool Matches(Rule rule, string? title, string? processName)
    {
        if (!rule.Enabled || rule.Kind != RuleKind.AppWindow) return false;

        var needle = rule.Pattern.Trim();
        if (needle.Length < 3) return false;

        return Contains(title, needle) || Contains(processName, needle);
    }

    public static bool MatchesAny(IEnumerable<Rule> rules, string? title, string? processName) =>
        rules.Any(r => Matches(r, title, processName));

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
