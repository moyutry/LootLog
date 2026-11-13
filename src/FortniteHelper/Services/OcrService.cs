using System.Runtime.InteropServices.WindowsRuntime;
using FortniteHelper.Infrastructure;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace FortniteHelper.Services;

/// <summary>Windows' built-in OCR engine (no internet, no extra downloads).</summary>
public sealed class OcrService
{
    readonly Lazy<OcrEngine?> _lazy = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    // The engine (and its language model) is only loaded the first time it is needed, to keep the
    // background footprint small for players who never use automatic detection.
    static OcrEngine? Create()
    {
        try
        {
            var engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
                         ?? OcrEngine.TryCreateFromLanguage(new Language("en-GB"))
                         ?? OcrEngine.TryCreateFromUserProfileLanguages();
            Log.Info(engine is null ? "OCR: no engine available" : $"OCR engine: {engine.RecognizerLanguage.LanguageTag}");
            return engine;
        }
        catch (Exception ex)
        {
            Log.Error("OCR init failed", ex);
            return null;
        }
    }

    /// <summary>Loads the engine in the background (called when a match starts).</summary>
    public void Warm() => Task.Run(() => _lazy.Value);

    /// <summary>Cheap check that does not load the engine.</summary>
    public bool IsAvailable => _lazy.IsValueCreated ? _lazy.Value is not null : IsLanguageInstalled();
    public string Language => _lazy.IsValueCreated ? _lazy.Value?.RecognizerLanguage.DisplayName ?? "Not available" : "English";

    static bool IsLanguageInstalled()
    {
        try { return OcrEngine.AvailableRecognizerLanguages.Any(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase)) || OcrEngine.AvailableRecognizerLanguages.Count > 0; }
        catch { return false; }
    }

    public async Task<string> RecognizeAsync(CapturedFrame frame)
    {
        var _engine = _lazy.Value;
        if (_engine is null) return "";
        int maxDim = (int)OcrEngine.MaxImageDimension;
        if (frame.Width > maxDim || frame.Height > maxDim) frame = Downscale(frame, maxDim);
        using var bmp = SoftwareBitmap.CreateCopyFromBuffer(frame.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8,
            frame.Width, frame.Height, BitmapAlphaMode.Ignore);
        var result = await _engine.RecognizeAsync(bmp);
        return string.Join("\n", result.Lines.Select(l => l.Text));
    }

    /// <summary>
    /// Keeps only bright, low-saturation pixels (Fortnite's white HUD text) as black on white.
    /// Helps OCR when the prompt sits on a busy or bright background.
    /// </summary>
    public static CapturedFrame HighContrast(CapturedFrame f)
    {
        var src = f.Pixels;
        var dst = new byte[src.Length];
        for (int i = 0; i < src.Length; i += 4)
        {
            int b = src[i], g = src[i + 1], r = src[i + 2];
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            bool text = max > 205 && max - min < 60;
            byte v = text ? (byte)0 : (byte)255;
            dst[i] = dst[i + 1] = dst[i + 2] = v;
            dst[i + 3] = 255;
        }
        return new CapturedFrame(dst, f.Width, f.Height);
    }

    static CapturedFrame Downscale(CapturedFrame f, int maxDim)
    {
        double s = (double)maxDim / Math.Max(f.Width, f.Height);
        int w = Math.Max(1, (int)(f.Width * s)), h = Math.Max(1, (int)(f.Height * s));
        var dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int sy = Math.Min(f.Height - 1, (int)(y / s));
            for (int x = 0; x < w; x++)
            {
                int sx = Math.Min(f.Width - 1, (int)(x / s));
                Buffer.BlockCopy(f.Pixels, (sy * f.Width + sx) * 4, dst, (y * w + x) * 4, 4);
            }
        }
        return new CapturedFrame(dst, w, h);
    }
}
