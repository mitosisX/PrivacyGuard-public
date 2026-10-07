using System.Text;
using PrivacyGuard.Core.Matching;
using PrivacyGuard.Core.Models;
using PrivacyGuard.Engine.Geometry;

namespace PrivacyGuard.Engine.Ocr;

/// <summary>One recognised word, in the coordinates of the image given to OCR.</summary>
public sealed record OcrWordBox(string Text, double X, double Y, double Width, double Height);

public sealed record OcrLineBox(IReadOnlyList<OcrWordBox> Words);

/// <summary>A piece of screen text that a rule wants hidden, in window frame pixels.</summary>
public sealed record Detection(RectI Rect, Guid RuleId);

/// <summary>Runs the rules over OCR output and turns matches back into rectangles.</summary>
public static class DetectionMapper
{
    /// <summary>
    /// Words closer than this fraction of the text height are treated as one token in the
    /// second pass. OCR sometimes splits a long address into pieces with no real gap.
    /// </summary>
    private const double TightGap = 0.22;

    /// <param name="scale">OCR image pixels per frame pixel (2 when the crop was enlarged).</param>
    /// <param name="offsetX">Left of the OCR crop, in frame pixels.</param>
    /// <param name="offsetY">Top of the OCR crop, in frame pixels.</param>
    public static List<Detection> Map(
        IReadOnlyList<OcrLineBox> lines, IReadOnlyList<Rule> rules, double scale, int offsetX, int offsetY)
    {
        var detections = new List<Detection>();
        if (lines.Count == 0 || rules.Count == 0) return detections;

        // Pass 1: words as OCR split them. Pass 2: tightly spaced words glued back together.
        foreach (var joinTight in new[] { false, true })
        {
            if (joinTight && !HasTightGaps(lines)) break;
            foreach (var d in MapPass(lines, rules, scale, offsetX, offsetY, joinTight))
            {
                // Overlapping hits for the same rule (a fragment and the whole token) become one box.
                var i = detections.FindIndex(e => e.RuleId == d.RuleId && e.Rect.IntersectsWith(d.Rect));
                if (i >= 0) detections[i] = detections[i] with { Rect = detections[i].Rect.Union(d.Rect) };
                else detections.Add(d);
            }
        }

        return detections;
    }

    private static bool HasTightGaps(IReadOnlyList<OcrLineBox> lines) =>
        lines.Any(l => Enumerable.Range(1, Math.Max(0, l.Words.Count - 1)).Any(i => IsTight(l.Words[i - 1], l.Words[i])));

    private static bool IsTight(OcrWordBox left, OcrWordBox right)
    {
        var gap = right.X - (left.X + left.Width);
        var height = Math.Max(left.Height, right.Height);
        return gap < height * TightGap;
    }

    private static List<Detection> MapPass(
        IReadOnlyList<OcrLineBox> lines, IReadOnlyList<Rule> rules, double scale, int offsetX, int offsetY, bool joinTight)
    {
        var detections = new List<Detection>();

        // One text for the whole crop, lines joined by newlines, so names that wrap still match.
        var text = new StringBuilder();
        var spans = new List<(int Start, int End, int Line, OcrWordBox Word)>();
        for (var li = 0; li < lines.Count; li++)
        {
            if (li > 0) text.Append('\n');
            var words = lines[li].Words;
            for (var wi = 0; wi < words.Count; wi++)
            {
                if (wi > 0 && !(joinTight && IsTight(words[wi - 1], words[wi]))) text.Append(' ');
                var start = text.Length;
                text.Append(words[wi].Text);
                spans.Add((start, text.Length, li, words[wi]));
            }
        }

        var full = text.ToString();
        foreach (var match in RuleMatcher.FindMatches(full, rules))
        {
            // One rectangle per line the match touches.
            var perLine = new Dictionary<int, (double L, double T, double R, double B)>();
            foreach (var (start, end, line, word) in spans)
            {
                var from = Math.Max(start, match.Start);
                var to = Math.Min(end, match.End);
                if (to <= from) continue;

                // Partial word: estimate the slice from character positions.
                var len = Math.Max(1, end - start);
                var x0 = word.X + word.Width * (from - start) / len;
                var x1 = word.X + word.Width * (to - start) / len;

                perLine[line] = perLine.TryGetValue(line, out var acc)
                    ? (Math.Min(acc.L, x0), Math.Min(acc.T, word.Y), Math.Max(acc.R, x1), Math.Max(acc.B, word.Y + word.Height))
                    : (x0, word.Y, x1, word.Y + word.Height);
            }

            foreach (var (l, t, r, b) in perLine.Values)
            {
                var rect = RectI.FromLTRB(
                    (int)Math.Floor(l / scale) + offsetX,
                    (int)Math.Floor(t / scale) + offsetY,
                    (int)Math.Ceiling(r / scale) + offsetX,
                    (int)Math.Ceiling(b / scale) + offsetY);
                if (!rect.IsEmpty) detections.Add(new Detection(rect, match.Rule.Id));
            }
        }

        return detections;
    }
}
