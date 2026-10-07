using System.IO;
using PrivacyGuard.Core.Storage;
using PrivacyGuard.Engine;

namespace PrivacyGuard.App.Services;

/// <summary>The few objects every page shares. Kept deliberately simple.</summary>
public sealed class AppServices
{
    public static AppServices Current { get; } = new();

    public AppSettings Settings { get; }

    public RuleBook Rules { get; }

    /// <summary>The live on-screen shield.</summary>
    public LiveEngine Engine { get; }

    public event EventHandler? LiveProtectionChanged;

    private AppServices()
    {
        Settings = AppSettings.Load();
        Rules = new RuleBook(new RuleStore());

        // PrivacyGuard's own windows are not shielded: you need to see your rules to edit them.
        // The Rules page hides values on demand for when you are on stream.
        // Streamer mode (curtaining unread text) is not offered: during fast scrolling it could
        // cover most of the window, which was too disruptive. The engine keeps the option.
        Engine = new LiveEngine(new EngineOptions
        {
            ProtectOwnWindows = false,
            StrictMode = false,
            BoxColor = Settings.BoxColorRgb,
        });
        Engine.UpdateRules(Rules.Rules);
        Rules.Changed += (_, _) => Engine.UpdateRules(Rules.Rules);
    }

    /// <summary>Turns the shield on or off. Returns an error message, or null on success.</summary>
    public string? SetLiveProtection(bool on)
    {
        string? error = null;
        try
        {
            if (on) Engine.Start();
            else Engine.Stop();
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            on = false;
        }

        Settings.LiveProtection = on;
        SaveSettings();
        LiveProtectionChanged?.Invoke(this, EventArgs.Empty);
        return error;
    }

    /// <summary>Changes the box colour ("#RRGGBB"), live if protection is running. False if the value is invalid.</summary>
    public bool SetBoxColor(string hex)
    {
        if (!AppSettings.TryParseColor(hex, out var rgb)) return false;
        Settings.BoxColor = $"#{rgb:X6}";
        Engine.SetBoxColor(rgb);
        SaveSettings();
        return true;
    }

    public void SaveSettings()
    {
        try
        {
            Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; protection itself keeps working.
        }
    }
}
