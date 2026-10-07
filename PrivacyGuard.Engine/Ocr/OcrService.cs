using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace PrivacyGuard.Engine.Ocr;

/// <summary>Wraps the built-in Windows OCR engine. Runs fully offline; nothing leaves the PC.</summary>
internal sealed class OcrService
{
    private readonly OcrEngine _engine;

    private OcrService(OcrEngine engine) => _engine = engine;

    public string Language => _engine.RecognizerLanguage.LanguageTag;

    public static OcrService? TryCreate()
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        return engine is null ? null : new OcrService(engine);
    }

    public async Task<IReadOnlyList<OcrLineBox>> RecognizeAsync(byte[] gray, int width, int height)
    {
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(gray.AsBuffer(0, width * height), BitmapPixelFormat.Gray8, width, height);
        var result = await _engine.RecognizeAsync(bitmap);

        var lines = new List<OcrLineBox>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var words = new List<OcrWordBox>(line.Words.Count);
            foreach (var w in line.Words)
                words.Add(new OcrWordBox(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height));
            lines.Add(new OcrLineBox(words));
        }
        return lines;
    }
}
