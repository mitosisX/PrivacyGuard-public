using PrivacyGuard.Core.Matching;
using PrivacyGuard.Core.Models;

namespace PrivacyGuard.Tests;

/// <summary>Real-world check: hiding the Wi-Fi name "Orion 4G WIFI".</summary>
public class WifiNameScenarioTests
{
    private static Rule Exact() => new() { Kind = RuleKind.Text, Pattern = "Orion 4G WIFI" };
    private static Rule Fuzzy() => new() { Kind = RuleKind.Text, Pattern = "Orion 4G WIFI", Fuzzy = true, FuzzyThreshold = 85 };

    private static string[] Found(string input, Rule rule) =>
        RuleMatcher.FindMatches(input, rule).Select(m => m.Text).ToArray();

    [Theory]
    [InlineData("Wi-Fi: Orion 4G WIFI", "Orion 4G WIFI")]
    [InlineData("connected to orion 4g wifi now", "orion 4g wifi")]
    [InlineData("Orion 4G WIFI", "Orion 4G WIFI")]
    public void Exact_rule_finds_the_name_in_any_case(string input, string expected) =>
        Assert.Equal([expected], Found(input, Exact()));

    [Fact]
    public void Exact_rule_misses_ocr_misreads_which_is_why_fuzzy_exists()
    {
        Assert.Empty(Found("Wi-Fi: 0rion 4G WIFl", Exact()));
    }

    [Theory]
    [InlineData("Wi-Fi: Orion 4G WIFI")]            // clean
    [InlineData("Wi-Fi: 0rion 4G WIFI")]            // zero for O
    [InlineData("Wi-Fi: Orion 4G WIFl")]            // lowercase L for I
    [InlineData("Wi-Fi: Orion 4G WlFI")]            // l for I in the middle
    [InlineData("Wi-Fi: Orion  4G   WIFI")]         // uneven spacing
    [InlineData("Wi-Fi: Orion 4G WIFI,")]           // trailing punctuation
    [InlineData("Wi-Fi: ORION 4G WIFI")]            // upper case
    [InlineData("Wi-Fi: Orion 4G\nWIFI")]           // wrapped onto the next line
    public void Fuzzy_rule_catches_realistic_variants(string input)
    {
        var matches = RuleMatcher.FindMatches(input, Fuzzy());
        Assert.Single(matches);
        Assert.Contains("4", matches[0].Text);
    }

    [Theory]
    [InlineData("Wi-Fi: Orion Watch")]
    [InlineData("Wi-Fi: Guest 4G Router")]
    [InlineData("Wi-Fi: HomeNetwork_5G")]
    public void Fuzzy_rule_leaves_other_text_alone(string input)
    {
        Assert.Empty(RuleMatcher.FindMatches(input, Fuzzy()));
    }

    [Fact]
    public void Near_identical_name_is_hidden_at_default_but_not_at_strict_threshold()
    {
        // "Orion 5G WIFI" differs from the rule by one character. Hiding it is the safe
        // default; raising the similarity slider makes the rule strict.
        Assert.Single(RuleMatcher.FindMatches("Orion 5G WIFI", Fuzzy()));

        var strict = Fuzzy();
        strict.FuzzyThreshold = 95;
        Assert.Empty(RuleMatcher.FindMatches("Orion 5G WIFI", strict));
        Assert.Single(RuleMatcher.FindMatches("0rion 4G WIFl", strict));   // OCR misreads still match
    }

    [Fact]
    public void Match_covers_exactly_the_name_so_nothing_else_is_hidden()
    {
        var input = "Wi-Fi: Orion 4G WIFI (connected)";
        var m = RuleMatcher.FindMatches(input, Fuzzy()).Single();
        Assert.Equal("Orion 4G WIFI", input.Substring(m.Start, m.Length));
    }

    [Fact]
    public void Merged_words_from_ocr_still_match()
    {
        // OCR sometimes drops the space: "Orion4G WIFI".
        var matches = RuleMatcher.FindMatches("Wi-Fi: Orion4G WIFI", Fuzzy());
        Assert.Single(matches);
    }

    [Fact]
    public void Name_inside_a_longer_sentence_is_found_once()
    {
        var input = "Your PC is connected to Orion 4G WIFI over Wi-Fi.";
        Assert.Single(RuleMatcher.FindMatches(input, Fuzzy()));
    }
}
