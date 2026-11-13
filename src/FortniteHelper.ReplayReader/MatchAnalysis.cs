using System.Text.Json.Serialization;

namespace FortniteHelper.Replays;

public enum ContainerType { Chest, AmmoBox, Other }

/// <summary>A point on the island. Units: seconds since the replay started and meters (world units / 100).</summary>
public sealed class MapPoint
{
    public float T { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public MapPoint() { }
    public MapPoint(float t, double xCm, double yCm, double zCm)
    {
        T = MathF.Round(t, 1);
        X = (float)Math.Round(xCm / 100, 1);
        Y = (float)Math.Round(yCm / 100, 1);
        Z = (float)Math.Round(zCm / 100, 1);
    }

    public double DistanceTo(MapPoint o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y));
}

/// <summary>A chest / ammo box / other container that the player opened.</summary>
public sealed class OpenedContainer
{
    /// <summary>Stable id of the placed actor (the same chest has the same id in every match).</summary>
    public string Id { get; set; } = "";
    public string Class { get; set; } = "";
    public ContainerType Type { get; set; }
    public bool Rare { get; set; }
    /// <summary>Seconds since the replay started.</summary>
    public float T { get; set; }
    public MapPoint? Pos { get; set; }
    /// <summary>True when the player searched it (Interact); false when it was broken open.</summary>
    public bool Searched { get; set; }
    public List<string> Loot { get; set; } = [];
}

/// <summary>A chest location learned from the replay (opened by anyone near the player).</summary>
public sealed class ChestSpot
{
    public string Id { get; set; } = "";
    public ContainerType Type { get; set; }
    public bool Rare { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

public sealed class SquadMember
{
    public string Name { get; set; } = "";
    public bool IsMe { get; set; }
    public int Chests { get; set; }
}

public sealed class FightEvent
{
    public float T { get; set; }
    /// <summary>Knock | Elim | KnockedBy | ElimBy</summary>
    public string Kind { get; set; } = "";
    public string? Other { get; set; }
    public bool OtherIsBot { get; set; }
    public float? DistanceM { get; set; }
    public string? Weapon { get; set; }
    public MapPoint? Pos { get; set; }
    [JsonIgnore] public float? Raw { get; set; }
}

public sealed class StormCircle
{
    public int Phase { get; set; }
    /// <summary>When the circle starts / finishes shrinking (seconds since replay start).</summary>
    public float ShrinkStart { get; set; }
    public float ShrinkEnd { get; set; }
    /// <summary>The safe zone this phase shrinks to (meters).</summary>
    public float X { get; set; }
    public float Y { get; set; }
    public float Radius { get; set; }
}

/// <summary>Everything we learn about one match from its replay file.</summary>
public sealed class MatchAnalysis
{
    public int Version { get; set; }
    public string ReplayFile { get; set; } = "";
    public DateTime RecordedUtc { get; set; }
    public float LengthSec { get; set; }

    public string? PlayerName { get; set; }
    public int? TeamIndex { get; set; }
    public List<string> Squad { get; set; } = [];

    public float? BusJumpT { get; set; }
    public float? LandT { get; set; }
    public MapPoint? Land { get; set; }
    /// <summary>Enemies within 150 m when you landed (a hot drop when this is high).</summary>
    public int EnemiesNearLanding { get; set; }

    public List<OpenedContainer> Opened { get; set; } = [];
    public int Chests { get; set; }
    public int ChestsSearched { get; set; }
    public int ChestsBroken { get; set; }
    public int AmmoBoxes { get; set; }
    public int OtherContainers { get; set; }
    /// <summary>Chests opened by you and your squad together (Pirate Shop Access is shared with the squad).</summary>
    public int SquadChests { get; set; }
    /// <summary>Who in the squad opened how many chests (you included).</summary>
    public List<SquadMember> SquadMembers { get; set; } = [];
    public List<ChestSpot> ChestSpots { get; set; } = [];

    public float? FirstChestT { get; set; }
    public float SecondsInStorm { get; set; }
    public float SecondsAlive { get; set; }

    public int Knocks { get; set; }
    public int Elims { get; set; }
    public List<FightEvent> Fights { get; set; } = [];
    public float? DeathT { get; set; }
    public MapPoint? DeathPos { get; set; }
    public string? KilledBy { get; set; }
    public bool KilledByBot { get; set; }
    public float? KilledFromM { get; set; }
    public string? DeathWeapon { get; set; }
    public bool Won { get; set; }

    public int? Placement { get; set; }
    public int? TotalPlayers { get; set; }

    public List<MapPoint> Path { get; set; } = [];
    public List<StormCircle> Storm { get; set; } = [];

    /// <summary>Container classes seen in this replay that the analyzer did not know yet.</summary>
    [JsonIgnore] public List<string> NewContainerClasses { get; set; } = [];

    [JsonIgnore] public DateTime EndUtc => RecordedUtc.AddSeconds(LengthSec);
    public DateTime WallClock(float t) => RecordedUtc.AddSeconds(t);
}
