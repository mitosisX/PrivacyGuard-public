using PrivacyGuard.Core.Models;
using PrivacyGuard.Core.Storage;

namespace PrivacyGuard.App.Helpers;

public sealed record Choice<T>(T Value, string Name);

/// <summary>Friendly names for the enum values shown in combo boxes.</summary>
public static class EditorChoices
{
    public static IReadOnlyList<Choice<RuleKind>> Kinds { get; } =
    [
        new(RuleKind.Text, "Text (exact, or fuzzy for OCR)"),
        new(RuleKind.Regex, "Regular expression"),
        new(RuleKind.BuiltIn, "Built-in detector"),
        new(RuleKind.AppWindow, "App or window (hide the whole window)"),
    ];

    public static IReadOnlyList<Choice<BuiltInPattern>> BuiltIns { get; } =
    [
        new(BuiltInPattern.CryptoAddress, "Crypto wallet addresses"),
        new(BuiltInPattern.Email, "Email addresses"),
        new(BuiltInPattern.Ipv4, "IPv4 addresses"),
        new(BuiltInPattern.Ipv6, "IPv6 addresses"),
        new(BuiltInPattern.Phone, "Phone numbers"),
        new(BuiltInPattern.ApiKey, "API keys and tokens"),
        new(BuiltInPattern.CreditCard, "Credit-card numbers"),
        new(BuiltInPattern.Url, "Web addresses (URLs)"),
    ];

    public static IReadOnlyList<Choice<RedactionStyle>> Styles { get; } =
    [
        new(RedactionStyle.Solid, "Solid fill (recommended)"),
        new(RedactionStyle.Blur, "Blur"),
        new(RedactionStyle.Pixelate, "Pixelate"),
    ];

    public static string BuiltInName(BuiltInPattern pattern) =>
        BuiltIns.First(b => b.Value == pattern).Name;

    public static string BuiltInDescription(BuiltInPattern pattern) => pattern switch
    {
        BuiltInPattern.Email => "Anything shaped like name@domain.com.",
        BuiltInPattern.Ipv4 => "Addresses like 192.168.1.24. Invalid numbers such as 300.1.1.1 are ignored.",
        BuiltInPattern.Ipv6 => "Addresses like fe80:0:0:0:1c2d:3e4f:5a6b:7c8d.",
        BuiltInPattern.Phone => "Numbers with 8 to 15 digits, with or without spaces, dashes and a leading +. Dates are ignored.",
        BuiltInPattern.ApiKey => "AWS, GitHub, Stripe, Google, Slack and JWT tokens, plus lines like api_key = ...",
        BuiltInPattern.CreditCard => "13 to 19 digit numbers that pass the card checksum, so random numbers are ignored.",
        BuiltInPattern.Url => "Anything starting with http://, https:// or www.",
        BuiltInPattern.CryptoAddress => "Bitcoin, Ethereum and other chains (Solana, TRON, Litecoin...), including shortened forms like 0x71C7...976F. Tolerates OCR misreads such as O for 0.",
        _ => "",
    };
}
