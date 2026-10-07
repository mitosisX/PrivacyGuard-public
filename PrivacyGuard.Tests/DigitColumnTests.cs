using PrivacyGuard.Core.Matching;
using PrivacyGuard.Core.Models;
using PrivacyGuard.Engine.Ocr;

namespace PrivacyGuard.Tests;

/// <summary>Columns of small numbers (editor line numbers, list counters) must never be redacted.</summary>
public class DigitColumnTests
{
    private static Rule Builtin(BuiltInPattern p) => new() { Kind = RuleKind.BuiltIn, BuiltIn = p };

    [Theory]
    [InlineData(BuiltInPattern.Phone)]
    [InlineData(BuiltInPattern.CreditCard)]
    [InlineData(BuiltInPattern.CryptoAddress)]
    [InlineData(BuiltInPattern.Ipv4)]
    public void Line_number_gutter_is_not_a_secret(BuiltInPattern pattern)
    {
        // What OCR returns for an editor gutter: one short number per line.
        var lines = Enumerable.Range(1, 9)   // nine single digits: exactly a "phone number" if lines were glued
            .Select(i => new OcrLineBox([new OcrWordBox(i.ToString(), 4, i * 20, 8 * i.ToString().Length, 14)]))
            .ToList();
        Assert.Empty(DetectionMapper.Map(lines, [Builtin(pattern)], 1, 0, 0));
    }

    [Fact]
    public void Phone_number_split_across_two_lines_is_not_glued_from_separate_lines()
    {
        Assert.Empty(RuleMatcher.FindMatches("Total 1234\n5678 items", Builtin(BuiltInPattern.Phone)));
    }

    [Fact]
    public void Real_phone_number_on_one_line_is_still_found()
    {
        Assert.Single(RuleMatcher.FindMatches("Call +265 991 234 567 today", Builtin(BuiltInPattern.Phone)));
    }
}

public class DigitRowTests
{
    private static readonly Rule Phone = new() { Kind = RuleKind.BuiltIn, BuiltIn = BuiltInPattern.Phone };

    [Theory]
    [InlineData("Page 1 2 3 4 5 6 7 8 9 10")]
    [InlineData("Mo Tu We: 1 2 3 4 5 6 7 8")]
    [InlineData("Score 3 - 1 - 0 - 2 - 5 - 7 - 4 - 9")]
    public void Rows_of_single_digits_are_not_phone_numbers(string text) =>
        Assert.Empty(RuleMatcher.FindMatches(text, Phone));

    [Theory]
    [InlineData("+1 555 010 7788")]
    [InlineData("0991 234 567")]
    [InlineData("+265-991-234-567")]
    [InlineData("(555) 010-7788")]
    public void Real_phone_numbers_are_still_found(string number) =>
        Assert.Single(RuleMatcher.FindMatches($"call {number} now", Phone));
}
