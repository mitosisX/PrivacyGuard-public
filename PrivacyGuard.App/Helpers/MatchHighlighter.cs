using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PrivacyGuard.Core.Matching;

namespace PrivacyGuard.App.Helpers;

/// <summary>Draws text into a RichTextBox with the matched spans highlighted or blocked out.</summary>
public static class MatchHighlighter
{
    private static readonly Brush HighlightBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0xE5, 0x48, 0x4D)));

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <param name="redacted">True to replace matches with block characters, like a real redaction.</param>
    public static void Render(RichTextBox box, string text, IEnumerable<TextMatch> matches, bool redacted = false)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var pos = 0;

        foreach (var (start, length) in RuleMatcher.MergeSpans(matches))
        {
            if (start > pos) AddText(paragraph, text[pos..start], highlight: false);

            var shown = redacted ? MaskKeepingBreaks(text.Substring(start, length)) : text.Substring(start, length);
            AddText(paragraph, shown, highlight: !redacted);
            pos = start + length;
        }

        if (pos < text.Length) AddText(paragraph, text[pos..], highlight: false);

        box.Document = new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0),
            FontFamily = box.FontFamily,
            FontSize = box.FontSize,
            // WPF documents justify text by default, which spreads words unevenly in a preview.
            TextAlignment = TextAlignment.Left,
        };
    }

    private static string MaskKeepingBreaks(string value) =>
        string.Concat(value.Select(c => c is '\n' or '\r' or ' ' or '\t' ? c : '█'));

    private static void AddText(Paragraph paragraph, string text, bool highlight)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) paragraph.Inlines.Add(new LineBreak());
            if (lines[i].Length == 0) continue;

            var run = new Run(lines[i]);
            if (highlight) run.Background = HighlightBrush;
            paragraph.Inlines.Add(run);
        }
    }
}
