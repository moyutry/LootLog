using System.Globalization;
using System.Text;

namespace FortniteHelper.Core;

public sealed class CrosshairProfile : ObservableObject
{
    string _name = "Default", _color = "#00FFFF";
    bool _outlines = true, _centerDot, _innerLines = true, _outerLines, _tStyle, _circle;
    double _outlineOpacity = 0.5, _dotOpacity = 1.0, _innerOpacity = 0.8, _outerOpacity = 0.35, _circleThickness = 1.5, _circleOpacity = 0.9;
    int _outlineThickness = 1, _dotThickness = 2, _innerLength = 6, _innerThickness = 2, _innerOffset = 3,
        _outerLength = 2, _outerThickness = 2, _outerOffset = 10, _circleRadius = 10;

    public string Name { get => _name; set => Set(ref _name, value); }
    /// <summary>#RRGGBB</summary>
    public string Color { get => _color; set => Set(ref _color, value); }

    public bool Outlines { get => _outlines; set => Set(ref _outlines, value); }
    public double OutlineOpacity { get => _outlineOpacity; set => Set(ref _outlineOpacity, value); }
    public int OutlineThickness { get => _outlineThickness; set => Set(ref _outlineThickness, value); }

    public bool CenterDot { get => _centerDot; set => Set(ref _centerDot, value); }
    public double DotOpacity { get => _dotOpacity; set => Set(ref _dotOpacity, value); }
    public int DotThickness { get => _dotThickness; set => Set(ref _dotThickness, value); }

    public bool InnerLines { get => _innerLines; set => Set(ref _innerLines, value); }
    public double InnerOpacity { get => _innerOpacity; set => Set(ref _innerOpacity, value); }
    public int InnerLength { get => _innerLength; set => Set(ref _innerLength, value); }
    public int InnerThickness { get => _innerThickness; set => Set(ref _innerThickness, value); }
    public int InnerOffset { get => _innerOffset; set => Set(ref _innerOffset, value); }

    public bool OuterLines { get => _outerLines; set => Set(ref _outerLines, value); }
    public double OuterOpacity { get => _outerOpacity; set => Set(ref _outerOpacity, value); }
    public int OuterLength { get => _outerLength; set => Set(ref _outerLength, value); }
    public int OuterThickness { get => _outerThickness; set => Set(ref _outerThickness, value); }
    public int OuterOffset { get => _outerOffset; set => Set(ref _outerOffset, value); }

    // Extras that Valorant codes do not have
    public bool TStyle { get => _tStyle; set => Set(ref _tStyle, value); }
    public bool Circle { get => _circle; set => Set(ref _circle, value); }
    public int CircleRadius { get => _circleRadius; set => Set(ref _circleRadius, value); }
    public double CircleThickness { get => _circleThickness; set => Set(ref _circleThickness, value); }
    public double CircleOpacity { get => _circleOpacity; set => Set(ref _circleOpacity, value); }

    public CrosshairProfile Clone(string? name = null)
    {
        var c = new CrosshairProfile();
        c.CopyFrom(this);
        c.Name = name ?? Name;
        return c;
    }

    /// <summary>Copies every look setting (not the name) from another profile.</summary>
    public void CopyFrom(CrosshairProfile o)
    {
        Color = o.Color;
        Outlines = o.Outlines; OutlineOpacity = o.OutlineOpacity; OutlineThickness = o.OutlineThickness;
        CenterDot = o.CenterDot; DotOpacity = o.DotOpacity; DotThickness = o.DotThickness;
        InnerLines = o.InnerLines; InnerOpacity = o.InnerOpacity; InnerLength = o.InnerLength; InnerThickness = o.InnerThickness; InnerOffset = o.InnerOffset;
        OuterLines = o.OuterLines; OuterOpacity = o.OuterOpacity; OuterLength = o.OuterLength; OuterThickness = o.OuterThickness; OuterOffset = o.OuterOffset;
        TStyle = o.TStyle; Circle = o.Circle; CircleRadius = o.CircleRadius; CircleThickness = o.CircleThickness; CircleOpacity = o.CircleOpacity;
    }

    public static CrosshairProfile[] Presets() =>
    [
        new() { Name = "Classic Cyan", Color = "#00FFFF" },
        new() { Name = "Pro Green", Color = "#00FF00", OutlineOpacity = 1, InnerOpacity = 1, InnerLength = 4, InnerThickness = 2, InnerOffset = 2 },
        new() { Name = "Tiny Dot", Color = "#FFFF00", CenterDot = true, DotThickness = 3, InnerLines = false, OutlineOpacity = 0.8 },
        new() { Name = "Storm Pink", Color = "#FF00FF", InnerLength = 5, InnerThickness = 2, InnerOffset = 4, InnerOpacity = 1, OuterLines = true, OuterOpacity = 0.6, OuterLength = 3, OuterOffset = 11 },
        new() { Name = "Sniper Plus", Color = "#FF3B3B", CenterDot = true, DotThickness = 2, InnerLength = 8, InnerThickness = 1, InnerOffset = 4, InnerOpacity = 1, Outlines = true, OutlineOpacity = 1 },
        new() { Name = "Ring", Color = "#FFFFFF", InnerLines = false, CenterDot = true, DotThickness = 2, Circle = true, CircleRadius = 9, CircleThickness = 1.5 },
        new() { Name = "T-Shape", Color = "#00FF88", TStyle = true, InnerLength = 6, InnerThickness = 2, InnerOffset = 3, InnerOpacity = 1 },
        new() { Name = "Big Box Gap", Color = "#00E5FF", InnerLength = 6, InnerThickness = 2, InnerOffset = 8, InnerOpacity = 1, OutlineOpacity = 0.9 },
    ];
}

/// <summary>Imports and exports Valorant crosshair codes (e.g. "0;P;c;5;h;0;0l;4;0o;2;0a;1;0f;0;1b;0").</summary>
public static class ValorantCode
{
    static readonly string[] Colors = ["#FFFFFF", "#00FF00", "#7FFF00", "#DFFF00", "#FFFF00", "#00FFFF", "#FF00FF", "#FF0000"];

    /// <summary>Applies a code onto a fresh profile that starts from Valorant's own defaults.</summary>
    public static CrosshairProfile Parse(string code, string name = "Imported")
    {
        if (string.IsNullOrWhiteSpace(code)) throw new FormatException("The code is empty.");
        var p = new CrosshairProfile
        {
            Name = name,
            Color = Colors[0],
            Outlines = true, OutlineOpacity = 0.5, OutlineThickness = 1,
            CenterDot = false, DotOpacity = 1, DotThickness = 2,
            InnerLines = true, InnerOpacity = 0.8, InnerLength = 6, InnerThickness = 2, InnerOffset = 3,
            OuterLines = true, OuterOpacity = 0.35, OuterLength = 2, OuterThickness = 2, OuterOffset = 10,
        };

        var parts = code.Trim().Split(';', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts[0] != "0") throw new FormatException("That does not look like a Valorant crosshair code.");

        // Only the primary section ("P") is used. It ends where the ADS ("A") or sniper ("S") section begins.
        int start = Array.IndexOf(parts, "P");
        int i = start >= 0 ? start + 1 : 1;
        int colorIndex = 0;
        string? custom = null;
        bool known = false;

        while (i + 1 < parts.Length)
        {
            string key = parts[i], val = parts[i + 1];
            if (key is "A" or "S") break;
            known |= ApplyKey(p, key, val, ref colorIndex, ref custom);
            i += 2;
        }

        if (colorIndex == 8 && custom is { Length: >= 6 })
            p.Color = "#" + custom[..6].ToUpperInvariant();
        else if (colorIndex is >= 0 and < 8)
            p.Color = Colors[colorIndex];

        if (!known && start < 0) throw new FormatException("No crosshair settings found in the code.");
        return p;
    }

    static bool ApplyKey(CrosshairProfile p, string key, string val, ref int colorIndex, ref string? custom)
    {
        static double D(string v) => double.Parse(v, CultureInfo.InvariantCulture);
        static int I(string v) => (int)Math.Round(double.Parse(v, CultureInfo.InvariantCulture));
        try
        {
            switch (key)
            {
                case "c": colorIndex = I(val); return true;
                case "u": custom = val; colorIndex = 8; return true;
                case "h": p.Outlines = val == "1"; return true;
                case "t": p.OutlineThickness = Math.Clamp(I(val), 1, 6); return true;
                case "o": p.OutlineOpacity = Math.Clamp(D(val), 0, 1); return true;
                case "d": p.CenterDot = val == "1"; return true;
                case "z": p.DotThickness = Math.Clamp(I(val), 1, 6); return true;
                case "a": p.DotOpacity = Math.Clamp(D(val), 0, 1); return true;
                case "0b": p.InnerLines = val == "1"; return true;
                case "0t": p.InnerThickness = Math.Clamp(I(val), 0, 10); return true;
                case "0l": p.InnerLength = Math.Clamp(I(val), 0, 20); return true;
                case "0o": p.InnerOffset = Math.Clamp(I(val), 0, 20); return true;
                case "0a": p.InnerOpacity = Math.Clamp(D(val), 0, 1); return true;
                case "1b": p.OuterLines = val == "1"; return true;
                case "1t": p.OuterThickness = Math.Clamp(I(val), 0, 10); return true;
                case "1l": p.OuterLength = Math.Clamp(I(val), 0, 20); return true;
                case "1o": p.OuterOffset = Math.Clamp(I(val), 0, 40); return true;
                case "1a": p.OuterOpacity = Math.Clamp(D(val), 0, 1); return true;
                default: return false; // movement/firing error, ADS settings etc. have no Fortnite equivalent
            }
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Builds a Valorant code for the profile (only values that differ from Valorant defaults).</summary>
    public static string Export(CrosshairProfile p)
    {
        var sb = new StringBuilder("0;P");
        void Add(string k, string v) => sb.Append(';').Append(k).Append(';').Append(v);
        static string F(double d) => Math.Round(d, 3).ToString(CultureInfo.InvariantCulture);

        int idx = Array.FindIndex(Colors, c => c.Equals(p.Color, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) { Add("c", "8"); Add("u", p.Color.TrimStart('#').ToUpperInvariant() + "FF"); }
        else if (idx != 0) Add("c", idx.ToString(CultureInfo.InvariantCulture));

        if (!p.Outlines) Add("h", "0");
        if (p.OutlineThickness != 1) Add("t", p.OutlineThickness.ToString(CultureInfo.InvariantCulture));
        if (Math.Abs(p.OutlineOpacity - 0.5) > 0.001) Add("o", F(p.OutlineOpacity));
        if (p.CenterDot) Add("d", "1");
        if (p.DotThickness != 2) Add("z", p.DotThickness.ToString(CultureInfo.InvariantCulture));
        if (Math.Abs(p.DotOpacity - 1) > 0.001) Add("a", F(p.DotOpacity));
        if (!p.InnerLines) Add("0b", "0");
        if (p.InnerThickness != 2) Add("0t", p.InnerThickness.ToString(CultureInfo.InvariantCulture));
        if (p.InnerLength != 6) Add("0l", p.InnerLength.ToString(CultureInfo.InvariantCulture));
        if (p.InnerOffset != 3) Add("0o", p.InnerOffset.ToString(CultureInfo.InvariantCulture));
        if (Math.Abs(p.InnerOpacity - 0.8) > 0.001) Add("0a", F(p.InnerOpacity));
        if (!p.OuterLines) Add("1b", "0");
        if (p.OuterThickness != 2) Add("1t", p.OuterThickness.ToString(CultureInfo.InvariantCulture));
        if (p.OuterLength != 2) Add("1l", p.OuterLength.ToString(CultureInfo.InvariantCulture));
        if (p.OuterOffset != 10) Add("1o", p.OuterOffset.ToString(CultureInfo.InvariantCulture));
        if (Math.Abs(p.OuterOpacity - 0.35) > 0.001) Add("1a", F(p.OuterOpacity));
        return sb.ToString();
    }
}
