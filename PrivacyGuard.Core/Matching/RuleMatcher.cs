using System.Text;
using System.Text.RegularExpressions;
using FuzzySharp;
using PrivacyGuard.Core.Models;

namespace PrivacyGuard.Core.Matching;

/// <summary>A span of text that a rule flagged.</summary>
public sealed record TextMatch(int Start, int Length, string Text, Rule Rule)
{
    public int End => Start + Length;
}

/// <summary>Finds sensitive spans in text. Pure logic, no UI and no I/O.</summary>
public static class RuleMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Returns the first problem with a rule, or null if it is usable.</summary>
    public static string? Validate(Rule rule)
    {
        if (rule.Kind == RuleKind.BuiltIn) return null;

        if (string.IsNullOrWhiteSpace(rule.Pattern))
            return rule.Kind == RuleKind.AppWindow
                ? "Enter part of the window title or app name, for example MetaMask."
                : "Enter the text or pattern to match.";

        if (rule.Kind == RuleKind.AppWindow && rule.Pattern.Trim().Length < 3)
            return "Use at least 3 characters so the rule does not hide unrelated windows.";

        if (rule.Kind == RuleKind.Regex)
        {
            try
            {
                // Compile the user's own text first so errors never mention the internal
                // whole-word wrapper that BuildRegex adds around it.
                _ = new Regex(rule.Pattern, RegexOptions.CultureInvariant, RegexTimeout);
                var rx = BuildRegex(rule);
                // An empty match everywhere would redact the whole screen.
                if (rx.IsMatch(string.Empty))
                    return "This pattern matches empty text, so it would match everywhere.";
            }
            catch (ArgumentException ex)
            {
                return "Invalid regular expression: " + ex.Message;
            }
            catch (RegexMatchTimeoutException)
            {
                return "This pattern is too slow to evaluate.";
            }
        }

        if (rule.Kind == RuleKind.Text && rule.Fuzzy && rule.FuzzyThreshold is < 50 or > 100)
            return "Fuzzy threshold must be between 50 and 100.";

        return null;
    }

    /// <summary>Finds all matches for all enabled rules, sorted by position.</summary>
    public static IReadOnlyList<TextMatch> FindMatches(string input, IEnumerable<Rule> rules)
    {
        var results = new List<TextMatch>();
        if (string.IsNullOrEmpty(input)) return results;

        foreach (var rule in rules)
        {
            if (!rule.Enabled || Validate(rule) is not null) continue;
            results.AddRange(FindMatches(input, rule));
        }

        results.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
        return results;
    }

    public static IReadOnlyList<TextMatch> FindMatches(string input, Rule rule)
    {
        if (string.IsNullOrEmpty(input)) return [];

        try
        {
            return rule.Kind switch
            {
                RuleKind.BuiltIn => FindBuiltIn(input, rule),
                RuleKind.Regex => FindRegex(input, rule),
                // App rules hide whole windows; they never match text on screen.
                RuleKind.AppWindow => [],
                _ => rule.Fuzzy ? FindFuzzy(input, rule) : FindLiteral(input, rule),
            };
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }
    }

    /// <summary>Merges overlapping matches into the smallest set of spans to cover.</summary>
    public static IReadOnlyList<(int Start, int Length)> MergeSpans(IEnumerable<TextMatch> matches)
    {
        var spans = new List<(int Start, int Length)>();
        foreach (var m in matches.OrderBy(m => m.Start))
        {
            if (spans.Count > 0 && m.Start <= spans[^1].Start + spans[^1].Length)
            {
                var last = spans[^1];
                var end = Math.Max(last.Start + last.Length, m.End);
                spans[^1] = (last.Start, end - last.Start);
            }
            else
            {
                spans.Add((m.Start, m.Length));
            }
        }
        return spans;
    }

    // ---- literal ----------------------------------------------------------

    private static IReadOnlyList<TextMatch> FindLiteral(string input, Rule rule)
    {
        var results = new List<TextMatch>();
        var cmp = rule.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var needle = rule.Pattern.Trim();
        if (needle.Length == 0) return results;

        var from = 0;
        while (from <= input.Length - needle.Length)
        {
            var i = input.IndexOf(needle, from, cmp);
            if (i < 0) break;

            if (!rule.WholeWord || IsWholeWord(input, i, needle.Length))
                results.Add(new TextMatch(i, needle.Length, input.Substring(i, needle.Length), rule));

            from = i + 1;
        }
        return results;
    }

    private static bool IsWholeWord(string input, int start, int length)
    {
        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
        var before = start == 0 || !IsWordChar(input[start - 1]);
        var afterIndex = start + length;
        var after = afterIndex >= input.Length || !IsWordChar(input[afterIndex]);
        return before && after;
    }

    // ---- regex ------------------------------------------------------------

    private static Regex BuildRegex(Rule rule)
    {
        var options = RegexOptions.CultureInvariant;
        if (!rule.CaseSensitive) options |= RegexOptions.IgnoreCase;
        var pattern = rule.WholeWord ? $@"(?<![\w])(?:{rule.Pattern})(?![\w])" : rule.Pattern;
        return new Regex(pattern, options, RegexTimeout);
    }

    private static IReadOnlyList<TextMatch> FindRegex(string input, Rule rule)
    {
        var results = new List<TextMatch>();
        foreach (Match m in BuildRegex(rule).Matches(input))
            if (m.Length > 0)
                results.Add(new TextMatch(m.Index, m.Length, m.Value, rule));
        return results;
    }

    // ---- built-in ---------------------------------------------------------

    private static IReadOnlyList<TextMatch> FindBuiltIn(string input, Rule rule)
    {
        var results = new List<TextMatch>();
        foreach (Match m in BuiltInPatterns.Get(rule.BuiltIn).Matches(input))
        {
            var value = m.Value.TrimEnd('.', ',', ';', ')');
            if (value.Length > 0 && BuiltInPatterns.IsValid(rule.BuiltIn, value))
                results.Add(new TextMatch(m.Index, value.Length, value, rule));
        }
        return results;
    }

    // ---- fuzzy ------------------------------------------------------------

    /// <summary>
    /// Fuzzy matching is aimed at OCR output. Words in the input are compared, in
    /// groups the size of the pattern's word count, against the pattern after both are
    /// normalised so common misreads (0/O, 1/l/I, rn/m) look identical.
    /// </summary>
    private static IReadOnlyList<TextMatch> FindFuzzy(string input, Rule rule)
    {
        var results = new List<TextMatch>();
        var target = Normalize(rule.Pattern);
        if (target.Length == 0) return results;

        var wordCount = Math.Max(1, rule.Pattern.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
        var tokens = Regex.Matches(input, @"\S+", RegexOptions.CultureInvariant);

        // OCR can drop a space ("Orion4G WIFI") or add one, so try one word fewer and one more too.
        var sizes = new[] { wordCount, wordCount - 1, wordCount + 1 }.Where(s => s >= 1).Distinct().ToArray();
        var candidates = new List<(int Start, int Length, int Score)>();

        for (var i = 0; i < tokens.Count; i++)
        {
            foreach (var size in sizes)
            {
                if (i + size > tokens.Count) continue;

                var first = tokens[i];
                var last = tokens[i + size - 1];
                var start = first.Index;
                var span = input.Substring(start, last.Index + last.Length - start);

                // Ignore trailing punctuation so "OmegaHome_5G," still matches.
                var trimmed = span.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '"', '\'');
                if (trimmed.Length == 0) continue;

                var candidate = Normalize(trimmed);
                if (candidate.Length == 0) continue;

                // Quick length filter keeps this cheap on big OCR dumps.
                if (Math.Abs(candidate.Length - target.Length) > Math.Max(2, target.Length / 3)) continue;

                var score = Fuzz.Ratio(target, candidate);
                if (score >= rule.FuzzyThreshold)
                    candidates.Add((start, trimmed.Length, score));
            }
        }

        // Best score first, and never report two overlapping windows for the same text.
        var accepted = new List<(int Start, int Length, int Score)>();
        foreach (var c in candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Start))
        {
            if (accepted.Any(a => c.Start < a.Start + a.Length && a.Start < c.Start + c.Length)) continue;
            accepted.Add(c);
        }

        foreach (var a in accepted.OrderBy(a => a.Start))
            results.Add(new TextMatch(a.Start, a.Length, input.Substring(a.Start, a.Length), rule));

        return results;
    }

    /// <summary>Collapses case, separators and look-alike characters.</summary>
    public static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var raw in value)
        {
            var c = char.ToLowerInvariant(raw);
            if (char.IsWhiteSpace(c) || c is '_' or '-' or '.') continue;
            sb.Append(c switch
            {
                '0' => 'o',
                '1' or 'i' or '|' or '!' => 'l',
                '5' => 's',
                '8' => 'b',
                _ => c,
            });
        }
        return sb.ToString().Replace("rn", "m", StringComparison.Ordinal);
    }
}
