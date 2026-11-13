using System.Text.Json.Serialization;

namespace FortniteHelper.Core;

public enum ContainerKind
{
    Chest,
    AmmoBox,
    /// <summary>A "Search" prompt: some container, not yet known which kind.</summary>
    Container,
}

/// <summary>One Battle Royale (or other mode) match, assembled from the log, the live chest counter and the replay.</summary>
public sealed class MatchRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public DateTime? DropUtc { get; set; }
    public string? Playlist { get; set; }
    public string? Map { get; set; }

    // Chests. Live matches get an estimate while playing; the replay replaces it with the exact count.
    public int Chests { get; set; }
    public int AmmoBoxes { get; set; }
    /// <summary>The chest count means something (live estimate or verified).</summary>
    public bool ChestsTracked { get; set; }
    /// <summary>Exact count, read from the replay (who opened every container).</summary>
    public bool ChestsVerified { get; set; }
    /// <summary>What the live counter said before the replay corrected it.</summary>
    public int? LiveChests { get; set; }
    public int ChestsSearched { get; set; }
    public int ChestsBroken { get; set; }
    /// <summary>Chests opened by your whole squad, you included (Pirate Shop Access is shared).</summary>
    public int? SquadChests { get; set; }
    /// <summary>Players in your team, you included (1 = solo). From the replay.</summary>
    public int? TeamSize { get; set; }

    // From the replay analysis
    public int AnalysisVersion { get; set; }
    public float? LandX { get; set; }
    public float? LandY { get; set; }
    public string? LandedAt { get; set; }
    public int? EnemiesNearLanding { get; set; }
    public double? SecondsInStorm { get; set; }
    public double? SecondsAlive { get; set; }
    /// <summary>Seconds from landing to the first chest.</summary>
    public double? FirstChestSec { get; set; }
    public int? Knocks { get; set; }
    public string? KilledBy { get; set; }
    public bool KilledByBot { get; set; }
    public string? DeathWeapon { get; set; }
    public double? KilledFromM { get; set; }

    // Result
    public bool Won { get; set; }
    public int? Placement { get; set; }
    public int? TotalPlayers { get; set; }

    // Replay stats
    public int? Kills { get; set; }
    public int? Assists { get; set; }
    public double? Accuracy { get; set; }
    public int? DamageDealt { get; set; }
    public int? DamageTaken { get; set; }
    public int? DamageToStructures { get; set; }
    public int? Revives { get; set; }
    public int? MaterialsGathered { get; set; }
    public int? MaterialsUsed { get; set; }
    public int? DistanceMeters { get; set; }

    // Network (from the log's end-of-phase metrics)
    public double? PingMs { get; set; }
    public double? PacketLossIn { get; set; }
    public double? PacketLossOut { get; set; }

    public int StormReached { get; set; }
    public string? ReplayFile { get; set; }
    public bool ReplayParsed { get; set; }

    /// <summary>live | log | replay</summary>
    public string Source { get; set; } = "live";

    [JsonIgnore] public TimeSpan? Duration => EndUtc is { } e ? e - StartUtc : null;
    [JsonIgnore] public string ModeName => PlaylistNames.Pretty(Playlist);
    [JsonIgnore] public bool IsVictory => Won || Placement == 1;
    [JsonIgnore] public bool HasChestCount => ChestsVerified || ChestsTracked;
    [JsonIgnore] public bool IsTeamMatch => TeamSize > 1;
    /// <summary>Chests opened by the team (just yours in solo, or before the replay was read).</summary>
    [JsonIgnore] public int TeamChests => Math.Max(Chests, SquadChests ?? 0);
}

public static class PlaylistNames
{
    static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Playlist_DefaultSolo"] = "Solo",
        ["Playlist_DefaultDuo"] = "Duos",
        ["Playlist_Trios"] = "Trios",
        ["Playlist_DefaultTrio"] = "Trios",
        ["Playlist_DefaultSquad"] = "Squads",
        ["Playlist_NoBuildBR_Solo"] = "Zero Build Solo",
        ["Playlist_NoBuildBR_Duo"] = "Zero Build Duos",
        ["Playlist_NoBuildBR_Trio"] = "Zero Build Trios",
        ["Playlist_NoBuildBR_Squad"] = "Zero Build Squads",
        ["Playlist_HabaneroSolo"] = "Ranked Solo",
        ["Playlist_HabaneroDuo"] = "Ranked Duos",
        ["Playlist_HabaneroTrio"] = "Ranked Trios",
        ["Playlist_HabaneroSquad"] = "Ranked Squads",
    };

    static readonly (string Code, string Name)[] Codenames =
    [
        ("NoBuildBR", "Zero Build"), ("NoBuild", "Zero Build"), ("Habanero", "Ranked"), ("ShowdownAlt", "Ranked"),
        ("Blastberry", "Reload"), ("Figment", "OG"), ("Juno", "LEGO"), ("Sparks", "Festival"),
        ("DelMar", "Rocket Racing"), ("Playground", "Creative"), ("Default", ""),
    ];

    public static string Pretty(string? playlist)
    {
        if (string.IsNullOrWhiteSpace(playlist)) return "Battle Royale";
        if (Known.TryGetValue(playlist, out var known)) return known;

        var s = playlist.StartsWith("Playlist_", StringComparison.OrdinalIgnoreCase) ? playlist[9..] : playlist;
        foreach (var (code, name) in Codenames)
            s = s.Replace(code, name.Length > 0 ? " " + name + " " : " ", StringComparison.OrdinalIgnoreCase);

        // Split CamelCase / underscores into words
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '_') { sb.Append(' '); continue; }
            if (i > 0 && char.IsUpper(c) && char.IsLower(s[i - 1])) sb.Append(' ');
            sb.Append(c);
        }
        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant() switch
            {
                "duo" => "Duos",
                "trio" => "Trios",
                "squad" => "Squads",
                "solo" => "Solo",
                _ => char.ToUpperInvariant(w[0]) + w[1..],
            });
        var result = string.Join(' ', words.Distinct());
        return result.Length == 0 ? "Battle Royale" : result;
    }

    /// <summary>Short badge text: SOLO / DUOS / TRIOS / SQUADS / RANKED etc.</summary>
    public static string TeamSize(string? playlist)
    {
        var p = playlist ?? "";
        if (p.Contains("Solo", StringComparison.OrdinalIgnoreCase)) return "SOLO";
        if (p.Contains("Duo", StringComparison.OrdinalIgnoreCase)) return "DUOS";
        if (p.Contains("Trio", StringComparison.OrdinalIgnoreCase)) return "TRIOS";
        if (p.Contains("Squad", StringComparison.OrdinalIgnoreCase)) return "SQUADS";
        return "BR";
    }
}
