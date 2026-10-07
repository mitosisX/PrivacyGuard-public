using PrivacyGuard.Core.Matching;
using PrivacyGuard.Core.Models;

namespace PrivacyGuard.Tests;

public class CryptoAddressTests
{
    private static readonly Rule Crypto = new() { Kind = RuleKind.BuiltIn, BuiltIn = BuiltInPattern.CryptoAddress };

    private static string[] Found(string text) => RuleMatcher.FindMatches(text, Crypto).Select(m => m.Text).ToArray();

    [Theory]
    [InlineData("0x71C7656EC7ab88b098defB751B7401B5f6d8976F")]           // Ethereum
    [InlineData("0x71c7656ec7ab88b098defb751b7401b5f6d8976f")]           // lowercase
    [InlineData("Ox71C7656EC7ab88b098defB751B7401B5f6d8976F")]           // OCR read 0 as O
    [InlineData("0x71C7...976F")]                                        // wallet's short form
    [InlineData("0x71C7…976F")]                                          // with an ellipsis character
    [InlineData("bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq")]           // Bitcoin bech32
    [InlineData("1BvBMSEYstWetqTFn5Au4m4GFg7xJaNVN2")]                   // Bitcoin legacy
    [InlineData("3J98t1WpEZ73CNmQviecrnyiWrnqRhWNLy")]                   // Bitcoin script
    [InlineData("7EcDhSYGxXyscszYEp35KHN8vvw3svAuLKTzXwCFLtV")]          // Solana
    [InlineData("TNPeeaaFB7K9cmo4uQpcU32zGK8G1NYqeL")]                   // TRON
    [InlineData("7EcD...FLtV8")]                                         // shortened non-EVM
    public void Finds_wallet_addresses(string address)
    {
        Assert.Equal([address], Found($"Send to {address} please"));
    }

    [Theory]
    [InlineData("internationalization and localization")]
    [InlineData("Loading...")]
    [InlineData("wait...what happened")]
    [InlineData("commit e83c5163316f89bfbde7d9ab23ca2e25604af290")]       // lowercase hex hash, not an address
    [InlineData("ThisIsAVeryLongCamelCaseIdentifierName")]                // no digits
    [InlineData("id 123e4567-e89b-12d3-a456-426614174000")]
    [InlineData("Total: 1,234,567.89")]
    public void Leaves_ordinary_text_alone(string text)
    {
        Assert.Empty(Found(text));
    }

    [Fact]
    public void Address_next_to_punctuation_is_found_exactly()
    {
        Assert.Equal(["0x71C7656EC7ab88b098defB751B7401B5f6d8976F"],
            Found("(wallet: 0x71C7656EC7ab88b098defB751B7401B5f6d8976F)"));
    }
}

public class AppRuleTests
{
    private static Rule App(string pattern, bool enabled = true) =>
        new() { Kind = RuleKind.AppWindow, Pattern = pattern, Enabled = enabled };

    [Theory]
    [InlineData("MetaMask Notification", "chrome", true)]
    [InlineData("Exodus", "Exodus", true)]
    [InlineData("Untitled", "exodus", true)]
    [InlineData("YouTube - Google Chrome", "chrome", false)]
    public void Matches_title_or_app_name_case_insensitively(string title, string process, bool expected)
    {
        var rules = new[] { App("metamask"), App("Exodus") };
        Assert.Equal(expected, AppRuleMatcher.MatchesAny(rules, title, process));
    }

    [Fact]
    public void Disabled_or_too_short_rules_never_match()
    {
        Assert.False(AppRuleMatcher.Matches(App("MetaMask", enabled: false), "MetaMask", ""));
        Assert.False(AppRuleMatcher.Matches(App("ch"), "chrome", "chrome"));
        Assert.NotNull(RuleMatcher.Validate(App("ch")));
        Assert.Null(RuleMatcher.Validate(App("MetaMask")));
    }
}
