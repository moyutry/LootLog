using System.Globalization;
using System.Windows.Media;
using FortniteHelper.Core;
using FortniteHelper.Services;

namespace FortniteHelper.Views.Pages;

/// <summary>Display wrapper for a match in lists.</summary>
public sealed class MatchRow(MatchRecord m)
{
    static readonly Brush Gold = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x3F)));
    static readonly Brush Cyan = Freeze(new SolidColorBrush(Color.FromRgb(0x2C, 0xE6, 0xFF)));
    static readonly Brush Purple = Freeze(new SolidColorBrush(Color.FromRgb(0xB0, 0x8C, 0xFF)));
    static readonly Brush Plain = Freeze(new SolidColorBrush(Color.FromRgb(0xAE, 0xB4, 0xE8)));

    static Brush Freeze(Brush b) { b.Freeze(); return b; }

    public MatchRecord Match { get; } = m;
    public bool Victory => Match.IsVictory;
    public string Placement => Match.Placement is { } p ? "#" + p : Match.Won ? "#1" : "-";
    public string PlayersText => Match.TotalPlayers is { } t ? $"of {t}" : "";
    public Brush PlacementBrush => Victory ? Gold : Match.Placement is <= 5 ? Purple : Match.Placement is <= 10 ? Cyan : Plain;
    public string Mode => Match.ModeName.ToUpperInvariant();
    public string Badge => PlaylistNames.TeamSize(Match.Playlist);
    public string When => FormatWhen(Match.StartUtc);
    public string Kills => Match.Kills?.ToString(CultureInfo.CurrentCulture) ?? "-";
    public string Damage => Match.DamageDealt?.ToString("N0", CultureInfo.CurrentCulture) ?? "-";
    public string Chests => Match.HasChestCount ? Match.Chests.ToString(CultureInfo.CurrentCulture) : "-";
    public bool Exact => Match.ChestsVerified;
    public string Landed => Match.LandedAt is { } l ? "  ·  " + l : "";
    public string Duration => Format.Duration(Match.Duration);
    public string Accuracy => Match.Accuracy is { } a ? $"{a * 100:0}%" : "-";
    public string Storm => Match.StormReached > 0 ? $"Storm {Match.StormReached}" : "";
    public string Result => Victory ? "VICTORY ROYALE" : Match.Placement is { } p ? $"{Format.Ordinal(p)} PLACE" : "MATCH";

    public static string FormatWhen(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var today = DateTime.Now.Date;
        if (local.Date == today) return "Today " + local.ToString("HH:mm");
        if (local.Date == today.AddDays(-1)) return "Yesterday " + local.ToString("HH:mm");
        if ((today - local.Date).TotalDays < 7) return local.ToString("dddd HH:mm", CultureInfo.InvariantCulture);
        return local.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);
    }
}

public sealed record StatTile(string Label, string Value, string? Sub = null);
