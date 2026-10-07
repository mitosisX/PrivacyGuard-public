using PrivacyGuard.Core.Models;
using PrivacyGuard.Core.Storage;

namespace PrivacyGuard.Tests;

public class RuleStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pg-tests-" + Guid.NewGuid().ToString("N"));

    public RuleStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Rules_round_trip_through_encrypted_file()
    {
        var store = new RuleStore(Path.Combine(_dir, "rules.dat"));
        var rules = new List<Rule>
        {
            new() { Label = "Home Wi-Fi", Kind = RuleKind.Text, Pattern = "OmegaHome_5G", Fuzzy = true },
            new() { Label = "Account", Kind = RuleKind.Regex, Pattern = @"ACC-\d{4}", CaseSensitive = true },
            new() { Kind = RuleKind.BuiltIn, BuiltIn = BuiltInPattern.Email, Enabled = false },
        };

        store.Save(rules);
        var loaded = store.Load();

        Assert.Equal(3, loaded.Count);
        Assert.Equal("OmegaHome_5G", loaded[0].Pattern);
        Assert.True(loaded[0].Fuzzy);
        Assert.Equal(RuleKind.Regex, loaded[1].Kind);
        Assert.False(loaded[2].Enabled);
        Assert.Equal(BuiltInPattern.Email, loaded[2].BuiltIn);
        Assert.Equal(rules[0].Id, loaded[0].Id);
    }

    [Fact]
    public void Saved_file_does_not_contain_plain_text_secrets()
    {
        var path = Path.Combine(_dir, "rules.dat");
        new RuleStore(path).Save([new Rule { Pattern = "SuperSecretWifiName" }]);

        var raw = File.ReadAllBytes(path);
        var asText = System.Text.Encoding.UTF8.GetString(raw);
        Assert.DoesNotContain("SuperSecretWifiName", asText);
        Assert.DoesNotContain("S\0u\0p\0e\0r", System.Text.Encoding.Unicode.GetString(raw));
    }

    [Fact]
    public void Missing_file_loads_as_empty()
    {
        Assert.Empty(new RuleStore(Path.Combine(_dir, "nope.dat")).Load());
    }

    [Fact]
    public void Corrupt_file_loads_as_empty_and_is_kept_for_inspection()
    {
        var path = Path.Combine(_dir, "rules.dat");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);

        Assert.Empty(new RuleStore(path).Load());
        Assert.True(File.Exists(path + ".unreadable"));
    }
}
