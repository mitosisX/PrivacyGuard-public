using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivacyGuard.Core.Storage;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RedactionStyle
{
    /// <summary>Opaque fill. The only style that cannot be reversed.</summary>
    Solid,
    Blur,
    Pixelate,
}

/// <summary>Non-secret preferences, stored as plain JSON.</summary>
public sealed class AppSettings
{
    public RedactionStyle Style { get; set; } = RedactionStyle.Solid;

    public string Hotkey { get; set; } = "Ctrl+Shift+PrintScreen";

    public bool StartWithWindows { get; set; }

    public bool StartMinimized { get; set; } = true;

    public bool ProtectClipboardScreenshots { get; set; } = true;

    public bool ShowToastAfterRedaction { get; set; } = true;

    /// <summary>"Dark", "Light" or "System".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>The live on-screen shield is running.</summary>
    public bool LiveProtection { get; set; }

    /// <summary>Closing the window keeps protection running in the tray.</summary>
    public bool CloseToTray { get; set; } = true;

    public const string DefaultBoxColor = "#111111";

    /// <summary>Fill colour of the on-screen boxes, as "#RRGGBB".</summary>
    public string BoxColor { get; set; } = DefaultBoxColor;

    /// <summary>The box colour as 0xRRGGBB, falling back to the default if the saved value is invalid.</summary>
    [JsonIgnore]
    public uint BoxColorRgb => TryParseColor(BoxColor, out var rgb) ? rgb : 0x111111;

    /// <summary>Reads "#RRGGBB" or "RRGGBB".</summary>
    public static bool TryParseColor(string? text, out uint rgb)
    {
        rgb = 0;
        var hex = text?.Trim().TrimStart('#');
        return hex is { Length: 6 }
            && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out rgb);
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load(string? path = null)
    {
        path ??= Path.Combine(AppPaths.DataDirectory, "settings.json");
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new();
        }
    }

    public void Save(string? path = null)
    {
        path ??= Path.Combine(AppPaths.DataDirectory, "settings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}
