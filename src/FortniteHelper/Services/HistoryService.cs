using System.Windows.Threading;
using FortniteHelper.Core;
using FortniteHelper.Infrastructure;
using FortniteHelper.Replays;

namespace FortniteHelper.Services;

public sealed class HistoryData
{
    public List<MatchRecord> Matches { get; set; } = [];
}

public sealed record SessionSummary(int Matches, int Wins, int Kills, int Chests, double? AvgPlacement, TimeSpan PlayTime, int Top10);

/// <summary>Match history: live matches, Fortnite's logs and the replay analysis (exact chests).</summary>
public sealed class HistoryService
{
    const string FileName = "matches.json";
    readonly StatsService _stats;
    readonly HistoryData _data;
    readonly DispatcherTimer _saveTimer;
    bool _importing;

    public HistoryService(StatsService stats)
    {
        _stats = stats;
        _data = JsonStore.Load<HistoryData>(FileName);
        _data.Matches.Sort((a, b) => b.StartUtc.CompareTo(a.StartUtc));
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<MatchRecord> Matches => _data.Matches;
    public bool IsImporting => _importing;
    public event Action? Changed;

    /// <summary>Names the place a world position (meters) is in, e.g. "Lantern Landing".</summary>
    public Func<double, double, string?>? NamePlace { get; set; }

    public void AddLive(MatchRecord m)
    {
        var existing = HistoryImporter.FindExisting(_data.Matches, m.StartUtc);
        if (existing is not null && !ReferenceEquals(existing, m))
        {
            if (!existing.ChestsVerified)
            {
                existing.Chests = Math.Max(existing.Chests, m.Chests);
                existing.AmmoBoxes = Math.Max(existing.AmmoBoxes, m.AmmoBoxes);
            }
            else existing.LiveChests ??= m.Chests;
            existing.ChestsTracked |= m.ChestsTracked;
            existing.Won |= m.Won;
            existing.EndUtc ??= m.EndUtc;
            existing.DropUtc ??= m.DropUtc;
            existing.Playlist ??= m.Playlist;
            existing.PingMs ??= m.PingMs;
            existing.StormReached = Math.Max(existing.StormReached, m.StormReached);
        }
        else if (existing is null)
        {
            _data.Matches.Insert(0, m);
            _data.Matches.Sort((a, b) => b.StartUtc.CompareTo(a.StartUtc));
        }
        QueueSave();
        Changed?.Invoke();
    }

    /// <summary>Copies a replay analysis onto its match (creating the match if the log is gone). The replay's chest count is exact.</summary>
    public MatchRecord? ApplyAnalysis(MatchAnalysis a, ReplayInfo? info)
    {
        var m = _data.Matches.FirstOrDefault(x => string.Equals(x.ReplayFile, a.ReplayFile, StringComparison.OrdinalIgnoreCase))
                ?? _data.Matches.Where(x => Math.Abs((x.StartUtc - a.RecordedUtc).TotalSeconds) < 120)
                                .OrderBy(x => Math.Abs((x.StartUtc - a.RecordedUtc).TotalSeconds)).FirstOrDefault();
        if (m is null)
        {
            if (a.LengthSec < 60 || a.BusJumpT is null) return null; // never left the warm-up island
            m = new MatchRecord { StartUtc = a.RecordedUtc, EndUtc = a.EndUtc, Source = "replay" };
            _data.Matches.Add(m);
            _data.Matches.Sort((x, y) => y.StartUtc.CompareTo(x.StartUtc));
        }

        if (info is not null) ReplayParser.Apply(info, m);
        m.ReplayFile = a.ReplayFile;
        m.ReplayParsed = true;
        m.EndUtc ??= a.EndUtc;

        // Chests: the live counter's estimate is kept for comparison, the replay's count replaces it.
        if (!m.ChestsVerified && m.ChestsTracked) m.LiveChests ??= m.Chests;
        m.Chests = a.Chests;
        m.AmmoBoxes = a.AmmoBoxes;
        m.ChestsSearched = a.ChestsSearched;
        m.ChestsBroken = a.ChestsBroken;
        m.SquadChests = a.SquadChests;
        m.TeamSize = a.Squad.Count + 1;
        m.ChestsVerified = true;

        m.AnalysisVersion = a.Version;
        if (a.Land is { } land)
        {
            m.LandX = land.X;
            m.LandY = land.Y;
            m.LandedAt = NamePlace?.Invoke(land.X, land.Y);
        }
        m.EnemiesNearLanding = a.Land is null ? null : a.EnemiesNearLanding;
        m.SecondsInStorm = a.SecondsInStorm;
        m.SecondsAlive = a.SecondsAlive;
        m.FirstChestSec = a.FirstChestT is { } fc && a.LandT is { } lt ? Math.Max(0, fc - lt) : null;
        m.Knocks = a.Knocks;
        m.Kills ??= a.Elims;
        m.KilledBy = a.KilledBy;
        m.KilledByBot = a.KilledByBot;
        m.DeathWeapon = a.DeathWeapon;
        m.KilledFromM = a.KilledFromM;
        if (a.Won) m.Won = true;
        if (a.Placement is > 0) m.Placement = a.Placement;
        if (a.TotalPlayers is > 0) m.TotalPlayers = a.TotalPlayers;

        QueueSave();
        Changed?.Invoke();
        return m;
    }

    /// <summary>Re-names landing spots (after the map loaded).</summary>
    public void RenamePlaces()
    {
        if (NamePlace is null) return;
        bool any = false;
        foreach (var m in _data.Matches)
        {
            if (m.LandX is not { } x || m.LandY is not { } y) continue;
            var name = NamePlace(x, y);
            if (name != m.LandedAt) { m.LandedAt = name; any = true; }
        }
        if (any) { QueueSave(); Changed?.Invoke(); }
    }

    /// <summary>Imports all matches Fortnite still has logs / replays for. Safe to run repeatedly.</summary>
    public async Task<int> ImportAsync()
    {
        if (_importing) return 0;
        _importing = true;
        Changed?.Invoke();
        try
        {
            var known = new HashSet<string>(_data.Matches.Where(m => m.ReplayParsed && m.ReplayFile is not null).Select(m => m.ReplayFile!),
                StringComparer.OrdinalIgnoreCase);
            string? player = null;
            var (logs, replays) = await Task.Run(() =>
            {
                var l = HistoryImporter.ImportLogs(HistoryImporter.LogFilesOldestFirst(), null, n => player = n);
                var r = HistoryImporter.ScanReplays(FortnitePaths.DemosDir, known);
                return (l, r);
            });
            int added = HistoryImporter.Merge(_data.Matches, logs, replays);
            if (player is not null) _stats.SetPlayerName(player);
            Save();
            Log.Info($"History import: {logs.Count} log matches, {replays.Count} new replays, {added} added.");
            return added;
        }
        catch (Exception ex)
        {
            Log.Error("History import failed", ex);
            return 0;
        }
        finally
        {
            _importing = false;
            Changed?.Invoke();
        }
    }

    public void Delete(MatchRecord m)
    {
        if (_data.Matches.Remove(m)) { QueueSave(); Changed?.Invoke(); }
    }

    public void ClearAll()
    {
        _data.Matches.Clear();
        Save();
        Changed?.Invoke();
    }

    public SessionSummary Summarize(IEnumerable<MatchRecord> matches)
    {
        int n = 0, wins = 0, kills = 0, chests = 0, top10 = 0;
        double placeSum = 0; int placeCount = 0;
        var time = TimeSpan.Zero;
        foreach (var m in matches)
        {
            n++;
            if (m.IsVictory) wins++;
            kills += m.Kills ?? 0;
            if (m.HasChestCount) chests += m.Chests;
            if (m.Placement is { } p) { placeSum += p; placeCount++; if (p <= 10) top10++; }
            if (m.Duration is { } d && d < TimeSpan.FromHours(1)) time += d;
        }
        return new SessionSummary(n, wins, kills, chests, placeCount > 0 ? placeSum / placeCount : null, time, top10);
    }

    public IEnumerable<MatchRecord> Today() => _data.Matches.Where(m => _stats.DayKey(m.StartUtc) == _stats.TodayKey);

    public string ExportCsv()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder("Start,Mode,Placement,Players,Won,Kills,Chests,ChestsExact,ChestsBroken,AmmoBoxes,LandedAt,Damage,DamageTaken,Accuracy,MaterialsGathered,MaterialsUsed,SecondsInStorm,Minutes\n");
        foreach (var m in _data.Matches)
        {
            sb.Append($"{m.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm},{m.ModeName},{m.Placement},{m.TotalPlayers},{m.IsVictory},{m.Kills},");
            sb.Append($"{(m.HasChestCount ? m.Chests : null)},{m.ChestsVerified},{(m.ChestsVerified ? m.ChestsBroken : null)},{(m.HasChestCount ? m.AmmoBoxes : null)},");
            sb.Append($"\"{m.LandedAt}\",{m.DamageDealt},{m.DamageTaken},");
            sb.Append($"{(m.Accuracy is { } a ? (a * 100).ToString("F1", inv) : "")},");
            sb.Append($"{m.MaterialsGathered},{m.MaterialsUsed},{m.SecondsInStorm?.ToString("F0", inv)},{m.Duration?.TotalMinutes.ToString("F1", inv)}\n");
        }
        return sb.ToString();
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
            _saveTimer.Stop();
            if (_data.Matches.Count > 5000) _data.Matches.RemoveRange(5000, _data.Matches.Count - 5000);
            JsonStore.Save(FileName, _data);
        }
        catch (Exception ex) { Log.Error("Saving history failed", ex); }
    }
}
