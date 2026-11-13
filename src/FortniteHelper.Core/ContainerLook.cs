namespace FortniteHelper.Core;

/// <summary>
/// Tells a chest from an ammo box by how the screen looks when you press Interact.
/// Fortnite shows the same "Search" prompt for both, but a chest is gold and glows, an ammo box is a dull green crate.
/// The model starts with a simple "how much glowing gold is in front of you" rule and then learns from your own
/// replays (which say exactly what every search opened), so it adapts to your graphics settings.
/// </summary>
public static class ContainerLook
{
    public const int HueBins = 18; // 20 degrees each
    /// <summary>Feature vector: 18 hue bins of colourful pixels, dark share, grey share, vivid-gold share.</summary>
    public const int Length = HueBins + 3;
    public const int GoldIndex = HueBins + 2;
    /// <summary>Share of vivid gold pixels above which the untrained rule says "chest" (low on purpose: missing a chest is worse).</summary>
    public const double DefaultGoldThreshold = 0.012;

    /// <summary>Colour features of the middle of a BGRA image (where the container is).</summary>
    public static float[] Features(byte[] bgra, int width, int height) => Features(bgra, width, height, 0.2, 0.12, 0.8, 0.88);

    /// <summary>Colour features of part of a BGRA image, given as fractions of its width / height.</summary>
    public static float[] Features(byte[] bgra, int width, int height, double fx0, double fy0, double fx1, double fy1)
    {
        var f = new float[Length];
        int x0 = Math.Clamp((int)(width * fx0), 0, width), x1 = Math.Clamp((int)(width * fx1), 0, width);
        int y0 = Math.Clamp((int)(height * fy0), 0, height), y1 = Math.Clamp((int)(height * fy1), 0, height);
        int n = 0;
        for (int y = y0; y < y1; y += 2)
        {
            int row = y * width * 4;
            for (int x = x0; x < x1; x += 2)
            {
                int i = row + x * 4;
                if (i + 2 >= bgra.Length) break;
                float b = bgra[i] / 255f, g = bgra[i + 1] / 255f, r = bgra[i + 2] / 255f;
                float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
                float v = max, s = max <= 0 ? 0 : (max - min) / max;
                n++;
                if (v < 0.18f) { f[HueBins]++; continue; }
                if (s < 0.28f) { f[HueBins + 1]++; continue; }
                float h;
                float d = max - min;
                if (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * ((b - r) / d + 2);
                else h = 60 * ((r - g) / d + 4);
                if (h < 0) h += 360;
                f[Math.Min(HueBins - 1, (int)(h / (360f / HueBins)))]++;
                if (h is >= 36 and <= 60 && s >= 0.5f && v >= 0.62f) f[GoldIndex]++;
            }
        }
        if (n > 0) for (int k = 0; k < f.Length; k++) f[k] /= n;
        return f;
    }

    public static bool LooksLikeChestByRule(float[] f, double threshold = DefaultGoldThreshold) =>
        f.Length == Length && f[GoldIndex] >= threshold;
}

/// <summary>One labelled example: what the screen looked like and what the replay said it was.</summary>
public sealed class LookSample
{
    public float[] F { get; set; } = [];
    public bool Chest { get; set; }
}

/// <summary>A small nearest-neighbour model over <see cref="ContainerLook"/> features, trained from replays.</summary>
public sealed class LookModel
{
    readonly List<LookSample> _samples;

    public LookModel(IEnumerable<LookSample> samples) =>
        _samples = samples.Where(s => s.F.Length == ContainerLook.Length).ToList();

    public int Chests => _samples.Count(s => s.Chest);
    public int Others => _samples.Count(s => !s.Chest);
    /// <summary>Enough examples of both kinds to trust the learned model over the gold rule.</summary>
    public bool Trained => Chests >= 6 && Others >= 4;

    public bool IsChest(float[] f)
    {
        if (!Trained) return ContainerLook.LooksLikeChestByRule(f);
        int k = Math.Min(7, _samples.Count | 1);
        var nearest = _samples.Select(s => (s, d: Distance(s.F, f))).OrderBy(x => x.d).Take(k).ToList();
        double chest = 0, other = 0;
        foreach (var (s, d) in nearest)
        {
            double w = 1 / (d + 1e-3);
            if (s.Chest) chest += w; else other += w;
        }
        // Lean towards "chest": a missed chest hurts more than a counted ammo box (the replay fixes both).
        return chest >= other * 0.8;
    }

    /// <summary>Leave-one-out accuracy on the stored examples (how often the model would have been right).</summary>
    public double? Accuracy()
    {
        if (!Trained) return null;
        int right = 0;
        for (int i = 0; i < _samples.Count; i++)
        {
            var rest = new LookModel(_samples.Where((_, j) => j != i));
            if (rest.IsChest(_samples[i].F) == _samples[i].Chest) right++;
        }
        return right / (double)_samples.Count;
    }

    /// <summary>Hellinger-style distance (square roots make small shares like the gold glow count).</summary>
    static double Distance(float[] a, float[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double w = i == ContainerLook.GoldIndex ? 3 : 1;
            double d = Math.Sqrt(a[i]) - Math.Sqrt(b[i]);
            sum += w * d * d;
        }
        return Math.Sqrt(sum);
    }
}
