using System.Text.RegularExpressions;
using PrivacyGuard.Core.Models;

namespace PrivacyGuard.Core.Matching;

/// <summary>Ready-made detectors for common sensitive values.</summary>
public static class BuiltInPatterns
{
    private const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    private static readonly Dictionary<BuiltInPattern, Regex> Patterns = new()
    {
        [BuiltInPattern.Email] = new(
            @"[A-Z0-9._%+\-]+@[A-Z0-9\-]+(?:\.[A-Z0-9\-]+)*\.[A-Z]{2,}",
            Opts, Timeout),

        [BuiltInPattern.Ipv4] = new(
            @"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?![\d.])",
            Opts, Timeout),

        [BuiltInPattern.Ipv6] = new(
            @"(?<![\w:])(?:[0-9A-F]{1,4}:){2,7}[0-9A-F]{1,4}(?![\w:])",
            Opts, Timeout),

        // Candidate phone numbers; digit count and grouping are checked in IsValid.
        // Spaces and tabs only, never line breaks: OCR joins screen lines with line breaks, and
        // a column of line numbers (1, 2, 3 ... 9) must not read as one nine-digit number.
        [BuiltInPattern.Phone] = new(
            @"(?<![\w])\+?\d[\d \t().\-]{6,}\d(?![\w])",
            Opts, Timeout),

        [BuiltInPattern.ApiKey] = new(
            string.Join("|",
                @"AKIA[0-9A-Z]{16}",                                   // AWS access key id
                @"gh[pousr]_[A-Za-z0-9]{30,}",                         // GitHub tokens
                @"github_pat_[A-Za-z0-9_]{40,}",                       // GitHub fine-grained
                @"sk_(?:live|test)_[A-Za-z0-9]{16,}",                  // Stripe
                @"sk-[A-Za-z0-9_\-]{20,}",                             // OpenAI / Anthropic style
                @"AIza[0-9A-Za-z_\-]{35}",                             // Google API key
                @"xox[abprs]-[A-Za-z0-9\-]{10,}",                      // Slack
                @"eyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", // JWT
                @"(?:api[_\-]?key|secret|token|passwd|password)[ \t]*[:=][ \t]*['""]?[A-Za-z0-9_\-./+]{12,}"),
            Opts, Timeout),

        // Candidate card numbers; Luhn is checked in IsValid.
        [BuiltInPattern.CreditCard] = new(
            @"(?<![\d])(?:\d[ \-]?){13,19}(?![\d])",
            Opts, Timeout),

        [BuiltInPattern.Url] = new(
            @"\b(?:https?://|www\.)[^\s<>""']+",
            Opts, Timeout),

        // Wallet addresses. Lenient on purpose: OCR often reads 0 as O, and wallets show
        // shortened forms like 0x71C7...976F. Generic candidates are checked in IsValid.
        [BuiltInPattern.CryptoAddress] = new(
            $@"(?<![0-9A-Za-z])(?:{KnownCryptoShapes}|{GenericCryptoShapes})(?![0-9A-Za-z])",
            Opts, Timeout),
    };

    /// <summary>Address shapes specific enough to trust without further checks.</summary>
    private const string KnownCryptoShapes =
        @"[0O]x[0-9a-fo]{38,42}" +                                       // Ethereum and EVM chains
        @"|[0O]x[0-9a-f]{2,10}[ \t]?(?:\.{2,4}|…)[ \t]?[0-9a-f]{2,10}" + // shortened 0x address
        @"|(?:bc|tb|ltc|cosmos|osmo|terra|bnb|addr)1[0-9a-z]{20,90}";     // bech32 family

    /// <summary>Broad shapes that IsValid narrows down: base58 (BTC, SOL, TRON, XRP) and shortened forms.</summary>
    private const string GenericCryptoShapes =
        @"[0-9a-z]{3,10}[ \t]?(?:\.{2,4}|…)[ \t]?[0-9a-z]{3,10}" +
        @"|[0-9a-z]{26,64}";

    private static readonly Regex KnownCryptoShape = new($"^(?:{KnownCryptoShapes})$", Opts, Timeout);

    public static Regex Get(BuiltInPattern pattern) => Patterns[pattern];

    /// <summary>Second-stage check that removes false positives the regex alone can't.</summary>
    public static bool IsValid(BuiltInPattern pattern, string value) => pattern switch
    {
        BuiltInPattern.Phone => IsPlausiblePhone(value),
        BuiltInPattern.CreditCard => PassesLuhn(value),
        BuiltInPattern.CryptoAddress => IsPlausibleCryptoAddress(value),
        _ => true,
    };

    private static bool IsPlausibleCryptoAddress(string value)
    {
        if (KnownCryptoShape.IsMatch(value)) return true;

        var hasDigit = value.Any(char.IsDigit);
        var shortened = value.Contains("..", StringComparison.Ordinal) || value.Contains('…');

        // "0x71C7...976F" is caught above; other shortened forms need a digit so that
        // ordinary text such as "wait...what" is left alone.
        if (shortened) return hasDigit;

        // Long base58 style tokens mix digits, upper and lower case. Plain words never do.
        return hasDigit && value.Any(char.IsUpper) && value.Any(char.IsLower);
    }

    private static bool IsPlausiblePhone(string value)
    {
        var digits = value.Count(char.IsDigit);
        if (digits is < 8 or > 15) return false;

        // Dates and plain decimals should not count as phone numbers.
        if (Regex.IsMatch(value, @"^\d{1,4}[-/.]\d{1,2}[-/.]\d{1,4}$", RegexOptions.CultureInvariant)) return false;

        // Phone numbers are written in groups of several digits ("+265 991 234 567"). A run of
        // single digits ("1 2 3 4 5 6 7 8 9" in a pagination bar or calendar) is not one.
        var groups = Regex.Split(value, @"\D+").Where(g => g.Length > 0).ToList();
        if (groups.Count(g => g.Length == 1) >= 3) return false;
        return true;
    }

    public static bool PassesLuhn(string value)
    {
        var digits = value.Where(char.IsDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19) return false;

        var sum = 0;
        var alternate = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i];
            if (alternate)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            alternate = !alternate;
        }
        return sum % 10 == 0;
    }
}
