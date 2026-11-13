using System.IO;
using System.Windows.Threading;
using FortniteHelper.Core;
using FortniteHelper.Infrastructure;
using FortniteHelper.Replays;

namespace FortniteHelper.Services;

public sealed class ChestSpotData
{
    public float X { get; set; }
    public float Y { get; set; }
    public bool Rare { get; set; }
    /// <summary>Matches in which this chest was opened (by anyone near you).</summary>
    public int Seen { get; set; }
    /// <summary>Times you opened it yourself.</summary>
    public int Mine { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

public sealed class ChestMapData
{
    public Dictionary<string, ChestSpotData> Chests { get; set; } = [];
    /// <summary>Replays already merged in.</summary>
    public HashSet<string> Included { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>How a landing spot works out for you.</summary>
public sealed class PlaceStats
{
    public required string Name { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public bool IsLandmark { get; init; }
    public int KnownChests { get; set; }
    public int Landings { get; set; }
    public double? AvgChests { get; set; }
    public int BestChests { get; set; }
    public double? AvgEnemies { get; set; }
    /// <summary>Share of landings where you were eliminated within 2 minutes.</summary>
    public double? EarlyDeathRate { get; set; }
    public double? AvgPlacement { get; set; }
}

/// <summary>
/// The island: named places (fortnite-api.com), chest locations learned from your replays and how each
/// landing spot works out for you (chests per match, how hot it is).
/// </summary>
public sealed class MapService : ObservableObject
{
    const string FileName = "chestmap.json";
    /// <summary>Chest locations older than this are dropped from the map (the island changes between seasons).</summary>
    static readonly TimeSpan KeepFor = TimeSpan.FromDays(60);

    readonly FortniteApi _api;
    readonly HistoryService _history;
    readonly AnalysisService _analysis;
    readonly Dispatcher _ui;
    readonly ChestMapData _data;
    readonly DispatcherTimer _saveTimer;
    MapData? _map;
    IReadOnlyList<PlaceStats> _places = [];

    public MapService(FortniteApi api, HistoryService history, AnalysisService analysis, Dispatcher ui)
    {
        _api = api;
        _history = history;
        _analysis = analysis;
        _ui = ui;
        _data = JsonStore.Load<ChestMapData>(FileName);
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        _map = api.CachedMap;
        history.NamePlace = NamePlace;
        analysis.Analyzed += (_, a) => { if (Merge(a)) { QueueSave(); Recompute(); } };
        history.Changed += Recompute;
    }

    public MapData? Map { get => _map; private set => Set(ref _map, value); }
    public IReadOnlyList<PlaceStats> Places { get => _places; private set => Set(ref _places, value); }
    public event Action? Changed;

    /// <summary>Chests seen recently (the current island).</summary>
    public IEnumerable<ChestSpotData> Chests
    {
        get
        {
            var since = DateTime.UtcNow - KeepFor;
            return _data.Chests.Values.Where(c => c.LastSeenUtc >= since);
        }
    }

    public int ChestCount => Chests.Count();

    public async Task LoadAsync()
    {
        var map = await _api.GetMapAsync();
        if (map is not null)
        {
            Map = map;
            _history.RenamePlaces();
        }
        // First run (or the chest map was deleted): rebuild it from the stored analyses.
        if (_data.Included.Count == 0)
        {
            var all = await Task.Run(() => _analysis.All().ToList());
            foreach (var a in all) Merge(a);
            if (all.Count > 0) Save();
        }
        Recompute();
    }

    bool Merge(MatchAnalysis a)
    {
        var name = Path.GetFileName(a.ReplayFile);
        if (!_data.Included.Add(name)) return false;
        var mine = a.Opened.Select(o => o.Id).ToHashSet();
        foreach (var s in a.ChestSpots)
        {
            if (s.Type != ContainerType.Chest) continue;
            if (!_data.Chests.TryGetValue(s.Id, out var c)) _data.Chests[s.Id] = c = new ChestSpotData { X = s.X, Y = s.Y, Rare = s.Rare };
            c.Seen++;
            if (mine.Contains(s.Id)) c.Mine++;
            if (a.RecordedUtc > c.LastSeenUtc) c.LastSeenUtc = a.RecordedUtc;
        }
        return true;
    }

    /// <summary>The named place a position (meters) belongs to: a big location within 300 m, a landmark within 170 m, else "Near ...".</summary>
    public string? NamePlace(double x, double y)
    {
        var pois = Map?.Pois;
        if (pois is null || pois.Count == 0) return null;
        MapPoi? best = null;
        double bestScore = double.MaxValue;
        foreach (var p in pois)
        {
            double d = Math.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));
            double reach = p.IsLandmark ? 170 : 300;
            double score = d / reach;
            if (score < bestScore) { bestScore = score; best = p; }
        }
        if (best is null) return null;
        if (bestScore <= 1) return best.Name;
        return bestScore <= 2.2 ? "Near " + best.Name : null;
    }

    public void Recompute()
    {
        var places = new Dictionary<string, PlaceStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Map?.Pois ?? [])
            places[p.Name] = new PlaceStats { Name = p.Name, X = p.X, Y = p.Y, IsLandmark = p.IsLandmark };

        if (Map is not null)
            foreach (var c in Chests)
                if (NamePlace(c.X, c.Y) is { } n && places.TryGetValue(n, out var ps)) ps.KnownChests++;

        var since = DateTime.UtcNow - KeepFor;
        foreach (var g in _history.Matches.Where(m => m.ChestsVerified && m.LandedAt is not null && m.StartUtc >= since).GroupBy(m => m.LandedAt!))
        {
            if (!places.TryGetValue(g.Key, out var ps))
            {
                // "Near X": position it at the average landing.
                ps = places[g.Key] = new PlaceStats
                {
                    Name = g.Key,
                    X = g.Average(m => (double)(m.LandX ?? 0)),
                    Y = g.Average(m => (double)(m.LandY ?? 0)),
                    IsLandmark = true,
                };
            }
            var list = g.ToList();
            ps.Landings = list.Count;
            ps.AvgChests = list.Average(m => m.Chests);
            ps.BestChests = list.Max(m => m.Chests);
            var withEnemies = list.Where(m => m.EnemiesNearLanding is not null).ToList();
            ps.AvgEnemies = withEnemies.Count > 0 ? withEnemies.Average(m => m.EnemiesNearLanding!.Value) : null;
            var alive = list.Where(m => m.SecondsAlive is not null).ToList();
            ps.EarlyDeathRate = alive.Count > 0 ? alive.Count(m => m.SecondsAlive < 120 && !m.IsVictory) / (double)alive.Count : null;
            var placed = list.Where(m => m.Placement is not null).ToList();
            ps.AvgPlacement = placed.Count > 0 ? placed.Average(m => m.Placement!.Value) : null;
        }
        Places = places.Values.ToList();
        OnPropertyChanged(nameof(ChestCount));
        Changed?.Invoke();
    }

    void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Save()
    {
        try
        {
            // Forget chests from old islands.
            var cutoff = DateTime.UtcNow - KeepFor - TimeSpan.FromDays(30);
            foreach (var id in _data.Chests.Where(kv => kv.Value.LastSeenUtc < cutoff).Select(kv => kv.Key).ToList()) _data.Chests.Remove(id);
            JsonStore.Save(FileName, _data);
        }
        catch (Exception ex) { Log.Warn("Saving chest map: " + ex.Message); }
    }
}
