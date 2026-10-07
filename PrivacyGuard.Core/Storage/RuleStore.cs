using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PrivacyGuard.Core.Models;

namespace PrivacyGuard.Core.Storage;

/// <summary>
/// Saves the rule list encrypted with Windows DPAPI (current user scope). Rules are
/// literally the user's secrets, so they are never written to disk as plain text.
/// </summary>
public sealed class RuleStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PrivacyGuard.Rules.v1");

    public string FilePath { get; }

    public RuleStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(AppPaths.DataDirectory, "rules.dat");
    }

    public List<Rule> Load()
    {
        if (!File.Exists(FilePath)) return [];

        try
        {
            var encrypted = File.ReadAllBytes(FilePath);
            var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<Rule>>(plain, Json) ?? [];
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            // Corrupt or from another Windows account: keep the file, start empty.
            var backup = FilePath + ".unreadable";
            try { File.Copy(FilePath, backup, overwrite: true); } catch (IOException) { }
            return [];
        }
    }

    public void Save(IEnumerable<Rule> rules)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        var plain = JsonSerializer.SerializeToUtf8Bytes(rules.ToList(), Json);
        var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

        // Write to a temp file first so a crash never leaves a half-written rules file.
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, FilePath, overwrite: true);
    }
}
