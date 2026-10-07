using System.IO;
using PrivacyGuard.Core.Models;
using PrivacyGuard.Core.Storage;

namespace PrivacyGuard.App.Services;

/// <summary>The user's live rule list. Every change is saved straight away, encrypted.</summary>
public sealed class RuleBook
{
    private readonly RuleStore _store;
    private readonly List<Rule> _rules;

    public RuleBook(RuleStore store)
    {
        _store = store;
        _rules = store.Load();
    }

    public IReadOnlyList<Rule> Rules => _rules;

    public event EventHandler? Changed;

    /// <summary>Raised when saving fails, so the UI can tell the user instead of losing data silently.</summary>
    public event Action<string>? SaveFailed;

    public void Add(Rule rule)
    {
        _rules.Add(rule);
        Commit();
    }

    public void AddRange(IEnumerable<Rule> rules)
    {
        _rules.AddRange(rules);
        Commit();
    }

    public void Update(Rule rule)
    {
        var i = _rules.FindIndex(r => r.Id == rule.Id);
        if (i < 0) return;
        _rules[i] = rule;
        Commit();
    }

    public void Remove(Rule rule)
    {
        if (_rules.RemoveAll(r => r.Id == rule.Id) > 0) Commit();
    }

    public void SetEnabled(Rule rule, bool enabled)
    {
        var target = _rules.Find(r => r.Id == rule.Id);
        if (target is null || target.Enabled == enabled) return;
        target.Enabled = enabled;
        Commit();
    }

    public bool ContainsText(string pattern) =>
        _rules.Any(r => r.Kind == RuleKind.Text &&
                        string.Equals(r.Pattern.Trim(), pattern.Trim(), StringComparison.OrdinalIgnoreCase));

    public bool ContainsBuiltIn(BuiltInPattern pattern) =>
        _rules.Any(r => r.Kind == RuleKind.BuiltIn && r.BuiltIn == pattern);

    private void Commit()
    {
        try
        {
            _store.Save(_rules);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveFailed?.Invoke("Your rules could not be saved to disk. Check free space and folder permissions.");
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
