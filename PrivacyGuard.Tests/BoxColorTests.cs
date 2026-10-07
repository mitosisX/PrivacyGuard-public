using PrivacyGuard.Core.Storage;

namespace PrivacyGuard.Tests;

public class BoxColorTests
{
    [Theory]
    [InlineData("#1E90FF", 0x1E90FFu)]
    [InlineData("1e90ff", 0x1E90FFu)]
    [InlineData("  #000000 ", 0x000000u)]
    public void Valid_hex_colours_parse(string text, uint expected)
    {
        Assert.True(AppSettings.TryParseColor(text, out var rgb));
        Assert.Equal(expected, rgb);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#FFF")]
    [InlineData("#GGGGGG")]
    [InlineData("#1E90FF00")]
    [InlineData("+1E90F")]
    public void Invalid_colours_are_rejected(string? text) =>
        Assert.False(AppSettings.TryParseColor(text, out _));

    [Fact]
    public void Bad_saved_colour_falls_back_to_default()
    {
        Assert.Equal(0x111111u, new AppSettings { BoxColor = "nonsense" }.BoxColorRgb);
        Assert.Equal(0xC42B1Cu, new AppSettings { BoxColor = "#C42B1C" }.BoxColorRgb);
    }

    [Fact]
    public void Box_colour_survives_save_and_load()
    {
        var path = Path.Combine(Path.GetTempPath(), "pg-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new AppSettings { BoxColor = "#0F6CBD" }.Save(path);
            Assert.Equal("#0F6CBD", AppSettings.Load(path).BoxColor);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
