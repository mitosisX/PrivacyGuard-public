using System.Text.Json.Serialization;

namespace PrivacyGuard.Core.Models;

/// <summary>How a rule's <see cref="Rule.Pattern"/> is interpreted.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RuleKind
{
    /// <summary>Literal text, optionally fuzzy so OCR misreads still match.</summary>
    Text,
    /// <summary>A .NET regular expression.</summary>
    Regex,
    /// <summary>One of the ready-made detectors in <see cref="BuiltInPattern"/>.</summary>
    BuiltIn,
    /// <summary>
    /// Hides a whole window when its title or app name contains <see cref="Rule.Pattern"/>.
    /// Works the moment the window appears, before any text is read.
    /// </summary>
    AppWindow,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BuiltInPattern
{
    Email,
    Ipv4,
    Ipv6,
    Phone,
    ApiKey,
    CreditCard,
    Url,
    CryptoAddress,
}

/// <summary>A single privacy rule: something the user never wants visible.</summary>
public sealed class Rule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Friendly name shown in the UI and history. Never contains the matched secret.</summary>
    public string Label { get; set; } = "";

    public RuleKind Kind { get; set; } = RuleKind.Text;

    /// <summary>The literal text or regex. Ignored for <see cref="RuleKind.BuiltIn"/>.</summary>
    public string Pattern { get; set; } = "";

    public BuiltInPattern BuiltIn { get; set; } = BuiltInPattern.Email;

    public bool Enabled { get; set; } = true;

    public bool CaseSensitive { get; set; }

    public bool WholeWord { get; set; }

    /// <summary>Text rules only: tolerate OCR misreads such as 0/O and 1/l.</summary>
    public bool Fuzzy { get; set; }

    /// <summary>Similarity needed for a fuzzy match, 50 to 100.</summary>
    public int FuzzyThreshold { get; set; } = 85;

    /// <summary>Set for rules created from auto-discovered machine values.</summary>
    public bool IsAutoDiscovered { get; set; }

    public Rule Clone() => (Rule)MemberwiseClone();

    /// <summary>Text shown in lists when the label is empty.</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Label) ? Label
        : Kind == RuleKind.BuiltIn ? BuiltIn.ToString()
        : Pattern;
}
