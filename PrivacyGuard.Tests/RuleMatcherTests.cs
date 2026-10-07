using PrivacyGuard.Core.Matching;
using PrivacyGuard.Core.Models;

namespace PrivacyGuard.Tests;

public class RuleMatcherTests
{
    private static Rule Text(string pattern, bool caseSensitive = false, bool whole = false, bool fuzzy = false, int threshold = 85) =>
        new() { Kind = RuleKind.Text, Pattern = pattern, CaseSensitive = caseSensitive, WholeWord = whole, Fuzzy = fuzzy, FuzzyThreshold = threshold };

    private static Rule Rx(string pattern, bool caseSensitive = false, bool whole = false) =>
        new() { Kind = RuleKind.Regex, Pattern = pattern, CaseSensitive = caseSensitive, WholeWord = whole };

    private static Rule Builtin(BuiltInPattern p) => new() { Kind = RuleKind.BuiltIn, BuiltIn = p };

    private static string[] Found(string input, Rule rule) =>
        RuleMatcher.FindMatches(input, rule).Select(m => m.Text).ToArray();

    // ---- literal text ----------------------------------------------------

    [Fact]
    public void Literal_matches_ignoring_case_by_default()
    {
        Assert.Equal(["Omega Msiska"], Found("User: Omega Msiska here", Text("omega msiska")));
    }

    [Fact]
    public void Literal_respects_case_sensitive()
    {
        Assert.Empty(Found("User: Omega Msiska", Text("omega msiska", caseSensitive: true)));
        Assert.Single(Found("User: Omega Msiska", Text("Omega Msiska", caseSensitive: true)));
    }

    [Fact]
    public void Literal_finds_every_occurrence_and_reports_positions()
    {
        var matches = RuleMatcher.FindMatches("abc abc", Text("abc"));
        Assert.Equal([0, 4], matches.Select(m => m.Start).ToArray());
    }

    [Fact]
    public void Whole_word_rejects_substrings()
    {
        Assert.Empty(Found("catalog", Text("cat", whole: true)));
        Assert.Single(Found("my cat sat", Text("cat", whole: true)));
    }

    // ---- regex -----------------------------------------------------------

    [Fact]
    public void Regex_matches_pattern()
    {
        Assert.Equal(["ACC-1234", "ACC-9876"], Found("ids ACC-1234 and ACC-9876", Rx(@"ACC-\d{4}")));
    }

    [Fact]
    public void Regex_is_case_insensitive_unless_asked()
    {
        Assert.Single(Found("acc-1", Rx(@"ACC-\d")));
        Assert.Empty(Found("acc-1", Rx(@"ACC-\d", caseSensitive: true)));
    }

    [Fact]
    public void Regex_whole_word_blocks_partial_matches()
    {
        Assert.Empty(Found("xACC-1", Rx(@"ACC-\d", whole: true)));
    }

    [Fact]
    public void Invalid_regex_is_reported_not_thrown()
    {
        Assert.NotNull(RuleMatcher.Validate(Rx("(unclosed")));
        Assert.Empty(RuleMatcher.FindMatches("text", [Rx("(unclosed")]));
    }

    [Fact]
    public void Regex_that_matches_empty_text_is_rejected()
    {
        Assert.NotNull(RuleMatcher.Validate(Rx(".*")));
        Assert.NotNull(RuleMatcher.Validate(Rx("a?")));
    }

    [Fact]
    public void Empty_pattern_is_rejected()
    {
        Assert.NotNull(RuleMatcher.Validate(Text("  ")));
    }

    [Fact]
    public void Catastrophic_regex_times_out_instead_of_hanging()
    {
        var evil = Rx(@"^(a+)+$");
        var input = new string('a', 40) + "!";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = RuleMatcher.FindMatches(input, evil);
        sw.Stop();
        Assert.Empty(result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
    }

    // ---- built-in --------------------------------------------------------

    [Theory]
    [InlineData("mail omega@example.com now", "omega@example.com")]
    [InlineData("a.b+tag@sub.domain.co.uk.", "a.b+tag@sub.domain.co.uk")]
    public void Email_detector(string input, string expected) =>
        Assert.Equal([expected], Found(input, Builtin(BuiltInPattern.Email)));

    [Theory]
    [InlineData("IP: 192.168.1.24", "192.168.1.24")]
    [InlineData("host 10.0.0.1, ok", "10.0.0.1")]
    public void Ipv4_detector(string input, string expected) =>
        Assert.Equal([expected], Found(input, Builtin(BuiltInPattern.Ipv4)));

    [Fact]
    public void Ipv4_ignores_invalid_octets_and_version_numbers()
    {
        Assert.Empty(Found("300.1.1.1", Builtin(BuiltInPattern.Ipv4)));
        Assert.Empty(Found("version 1.2.3.4.5", Builtin(BuiltInPattern.Ipv4)));
    }

    [Fact]
    public void Phone_detector_needs_plausible_digit_count_and_skips_dates()
    {
        var rule = Builtin(BuiltInPattern.Phone);
        Assert.Single(Found("call +265 991 234 567 today", rule));
        Assert.Empty(Found("12345", rule));
        Assert.Empty(Found("2026-10-06", rule));
    }

    [Fact]
    public void CreditCard_detector_uses_luhn()
    {
        var rule = Builtin(BuiltInPattern.CreditCard);
        Assert.Single(Found("card 4111 1111 1111 1111 ok", rule));   // valid test number
        Assert.Empty(Found("card 4111 1111 1111 1112 ok", rule));    // fails Luhn
    }

    [Theory]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("sk_live_abcdefghij0123456789")]
    [InlineData("api_key = abcdef1234567890xyz")]
    public void ApiKey_detector(string secret) =>
        Assert.Single(Found("key: " + secret + " end", Builtin(BuiltInPattern.ApiKey)));

    [Fact]
    public void Url_detector()
    {
        Assert.Equal(["https://example.com/a?b=1"], Found("see https://example.com/a?b=1 now", Builtin(BuiltInPattern.Url)));
    }

    // ---- fuzzy -----------------------------------------------------------

    [Theory]
    [InlineData("Wi-Fi: 0megaHome_5G", "0megaHome_5G")]     // zero read for O
    [InlineData("Wi-Fi: OmegaHome 5G", "OmegaHome")]        // underscore read as space (first token still close)
    [InlineData("Wi-Fi: OmegaH0me_SG", "OmegaH0me_SG")]     // 0 and S/5 confusion
    public void Fuzzy_tolerates_common_ocr_misreads(string input, string expectedStart)
    {
        var matches = RuleMatcher.FindMatches(input, Text("OmegaHome_5G", fuzzy: true, threshold: 80));
        Assert.NotEmpty(matches);
        Assert.StartsWith(expectedStart[..4], matches[0].Text);
    }

    [Fact]
    public void Fuzzy_does_not_match_unrelated_words()
    {
        Assert.Empty(Found("Network: GuestWifi", Text("OmegaHome_5G", fuzzy: true)));
    }

    [Fact]
    public void Fuzzy_multi_word_names_match_across_words()
    {
        var m = RuleMatcher.FindMatches("User: 0mega Msiska logged in", Text("Omega Msiska", fuzzy: true));
        Assert.Single(m);
        Assert.Equal("0mega Msiska", m[0].Text);
    }

    [Fact]
    public void Fuzzy_ignores_trailing_punctuation()
    {
        var m = RuleMatcher.FindMatches("name: Omega,", Text("Omega", fuzzy: true));
        Assert.Single(m);
        Assert.Equal("Omega", m[0].Text);
    }

    // ---- combining -------------------------------------------------------

    [Fact]
    public void Disabled_rules_are_skipped()
    {
        var rule = Text("secret");
        rule.Enabled = false;
        Assert.Empty(RuleMatcher.FindMatches("a secret", [rule]));
    }

    [Fact]
    public void Merge_spans_joins_overlaps_and_keeps_gaps()
    {
        var r = Text("x");
        var spans = RuleMatcher.MergeSpans(
        [
            new TextMatch(0, 5, "", r), new TextMatch(3, 5, "", r), new TextMatch(20, 2, "", r),
        ]);
        Assert.Equal([(0, 8), (20, 2)], spans.ToArray());
    }
}
