using System.Windows.Threading;
using FortniteHelper.Core;
using FortniteHelper.Infrastructure;

namespace FortniteHelper.Services;

public sealed class StatsData
{
    public StormTimings Storm { get; set; } = new();
    public string? PlayerName { get; set; }
    public DateTime TrackingSinceUtc { get; set; } = DateTime.UtcNow;

    // The match in progress, so a restart of the app mid-match keeps its live count.
    public DateTime? LiveMatchStartUtc { get; set; }
    public int LiveMatchChests { get; set; }
    public int LiveMatchAmmo { get; set; }
}

/// <summary>
/// Chest numbers: this match (live estimate), today, all time, chests per match and the Pirate token odds.
/// Every total is computed from the match history, where the replay's exact count replaces the live estimate.
/// </summary>
public sealed class StatsService : ObservableObject
{
    const string FileName = "stats.json";
    readonly SettingsService _settings;
    readonly DispatcherTimer _saveTimer;
    HistoryService? _history;
    MatchRecord? _live;

    public StatsData Data { get; }

    public StatsService(SettingsService settings)
    {
        _settings = settings;
        Data = JsonStore.Load<StatsData>(FileName);
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        settings.Settings.General.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GeneralSettings.DayStartHour)) Recompute();
        };
    }

    /// <summary>Raised when the live counter changes: kind, delta, counted automatically.</summary>
    public event Action<ContainerKind, int, bool>? Counted;

    // ---- live match ----
    public int MatchChests => _live?.Chests ?? 0;
    public int MatchAmmo => _live?.AmmoBoxes ?? 0;
    public bool HasLiveMatch => _live is not null;

    // ---- totals (history + live) ----
    int _today, _todayTeam, _allTime, _best, _counted, _seasonChests, _seasonSquad, _pending, _teamMatches, _boosted;
    double? _average, _recentAverage, _teamAverage;
    double _pirate, _squadPirate;

    public int TodayChests { get => _today; private set => Set(ref _today, value); }
    /// <summary>Chests opened today by you and your teammates.</summary>
    public int TodayTeamChests { get => _todayTeam; private set => Set(ref _todayTeam, value); }
    public int AllTimeChests { get => _allTime; private set => Set(ref _allTime, value); }
    public int BestMatchChests { get => _best; private set => Set(ref _best, value); }
    /// <summary>Matches with a chest count (verified or live).</summary>
    public int CountedMatches { get => _counted; private set => Set(ref _counted, value); }
    /// <summary>Average chests per match (CPM), all matches with a count.</summary>
    public double? AverageChests { get => _average; private set => Set(ref _average, value); }
    /// <summary>Your CPM over the last 20 matches.</summary>
    public double? RecentAverageChests { get => _recentAverage; private set => Set(ref _recentAverage, value); }
    /// <summary>Team CPM (you + teammates) over your last 20 duo/trio/squad matches.</summary>
    public double? TeamAverageChests { get => _teamAverage; private set => Set(ref _teamAverage, value); }
    public int TeamMatches { get => _teamMatches; private set => Set(ref _teamMatches, value); }
    /// <summary>Chests opened since the Treasure Hunters mini season started (they count for the Pirate token).</summary>
    public int SeasonChests { get => _seasonChests; private set => Set(ref _seasonChests, value); }
    public int SeasonSquadChests { get => _seasonSquad; private set => Set(ref _seasonSquad, value); }
    /// <summary>Season chests opened during a Treasure Hour (boosted odds).</summary>
    public int BoostedChests { get => _boosted; private set => Set(ref _boosted, value); }
    /// <summary>Finished matches still waiting for their replay to be read.</summary>
    public int PendingMatches { get => _pending; private set => Set(ref _pending, value); }

    /// <summary>Chance you've found the Pirate token this season (Treasure Hour boosts included).</summary>
    public double PirateChance { get => _pirate; private set => Set(ref _pirate, value); }
    /// <summary>The same, counting every chest your team opened (access is shared).</summary>
    public double SquadPirateChance { get => _squadPirate; private set => Set(ref _squadPirate, value); }

    public string TodayKey => DayKey(DateTime.UtcNow);

    public string DayKey(DateTime utc) =>
        utc.ToLocalTime().AddHours(-_settings.Settings.General.DayStartHour).ToString("yyyy-MM-dd");

    public void Attach(HistoryService history)
    {
        _history = history;
        history.Changed += Recompute;
        Recompute();
    }

    public void Recompute()
    {
        int today = 0, todayTeam = 0, all = 0, best = 0, counted = 0, season = 0, squad = 0, pending = 0, boosted = 0;
        double logMiss = 0, squadLogMiss = 0;
        var todayKey = TodayKey;
        var recent = new List<int>(20);
        var recentTeam = new List<int>(20);
        if (_history is not null)
        {
            foreach (var m in _history.Matches) // newest first
            {
                if (ReferenceEquals(m, _live) || !m.HasChestCount) continue;
                if (!m.ChestsVerified) pending++;
                all += m.Chests;
                counted++;
                best = Math.Max(best, m.Chests);
                if (DayKey(m.StartUtc) == todayKey) { today += m.Chests; todayTeam += m.TeamChests; }
                if (PirateToken.IsActive(m.StartUtc))
                {
                    double boost = PirateToken.BoostAt(m.StartUtc);
                    season += m.Chests;
                    squad += m.TeamChests;
                    if (boost > 1) boosted += m.Chests;
                    logMiss += PirateToken.LogMiss(m.Chests, boost);
                    squadLogMiss += PirateToken.LogMiss(m.TeamChests, boost);
                }
                if (recent.Count < 20) recent.Add(m.Chests);
                if (m.IsTeamMatch && m.ChestsVerified && recentTeam.Count < 20) recentTeam.Add(m.TeamChests);
            }
        }
        if (_live is not null)
        {
            double boost = PirateToken.BoostAt(DateTime.UtcNow);
            today += _live.Chests;
            todayTeam += _live.Chests;
            all += _live.Chests;
            season += _live.Chests;
            squad += _live.Chests;
            if (boost > 1) boosted += _live.Chests;
            logMiss += PirateToken.LogMiss(_live.Chests, boost);
            squadLogMiss += PirateToken.LogMiss(_live.Chests, boost);
            best = Math.Max(best, _live.Chests);
        }

        TodayChests = today;
        TodayTeamChests = todayTeam;
        AllTimeChests = all;
        BestMatchChests = best;
        CountedMatches = counted;
        AverageChests = counted > 0 ? (all - (_live?.Chests ?? 0)) / (double)counted : null;
        RecentAverageChests = recent.Count > 0 ? recent.Average() : null;
        TeamAverageChests = recentTeam.Count > 0 ? recentTeam.Average() : null;
        TeamMatches = recentTeam.Count;
        SeasonChests = season;
        SeasonSquadChests = squad;
        BoostedChests = boosted;
        PendingMatches = pending;
        PirateChance = PirateToken.ChanceFromLogMiss(logMiss);
        SquadPirateChance = PirateToken.ChanceFromLogMiss(squadLogMiss);
        OnPropertyChanged(nameof(MatchChests));
        OnPropertyChanged(nameof(MatchAmmo));
        OnPropertyChanged(nameof(HasLiveMatch));
    }

    /// <summary>Chests per day for the chart (oldest first).</summary>
    public List<(DateTime Day, int Chests)> ChestsPerDay(int days)
    {
        var result = new List<(DateTime, int)>();
        var today = DateTime.Parse(TodayKey, System.Globalization.CultureInfo.InvariantCulture);
        var sums = new Dictionary<string, int>();
        if (_history is not null)
            foreach (var m in _history.Matches)
                if (m.HasChestCount && !ReferenceEquals(m, _live))
                    sums[DayKey(m.StartUtc)] = sums.GetValueOrDefault(DayKey(m.StartUtc)) + m.Chests;
        if (_live is not null) sums[TodayKey] = sums.GetValueOrDefault(TodayKey) + _live.Chests;
        for (int i = days - 1; i >= 0; i--)
        {
            var d = today.AddDays(-i);
            result.Add((d, sums.GetValueOrDefault(d.ToString("yyyy-MM-dd"))));
        }
        return result;
    }

    /// <summary>Live counter (the detector). Only while a match is running.</summary>
    public void Add(ContainerKind kind, int delta, bool auto)
    {
        if (_live is null) return;
        if (kind == ContainerKind.AmmoBox)
        {
            _live.AmmoBoxes = Math.Max(0, _live.AmmoBoxes + delta);
            Data.LiveMatchAmmo = _live.AmmoBoxes;
        }
        else
        {
            _live.Chests = Math.Max(0, _live.Chests + delta);
            Data.LiveMatchChests = _live.Chests;
        }
        QueueSave();
        Recompute();
        Counted?.Invoke(kind, delta, auto);
    }

    public void BeginLiveMatch(MatchRecord m)
    {
        _live = m;
        Data.LiveMatchStartUtc = m.StartUtc;
        Data.LiveMatchChests = 0;
        Data.LiveMatchAmmo = 0;
        QueueSave();
        Recompute();
    }

    public void RestoreLiveMatch(MatchRecord m)
    {
        _live = m;
        if (Data.LiveMatchStartUtc is { } s && Math.Abs((s - m.StartUtc).TotalSeconds) < 5)
        {
            m.Chests = Data.LiveMatchChests;
            m.AmmoBoxes = Data.LiveMatchAmmo;
        }
        else
        {
            Data.LiveMatchStartUtc = m.StartUtc;
            Data.LiveMatchChests = Data.LiveMatchAmmo = 0;
        }
        Recompute();
    }

    public void ClearLiveMatch()
    {
        _live = null;
        Data.LiveMatchStartUtc = null;
        Data.LiveMatchChests = Data.LiveMatchAmmo = 0;
        QueueSave();
        Recompute();
    }

    public void LearnStorm(StormObservation o)
    {
        Data.Storm.Learn(o.Step, o.Index, o.Seconds);
        QueueSave();
    }

    public void SetPlayerName(string name)
    {
        if (Data.PlayerName == name) return;
        Data.PlayerName = name;
        QueueSave();
    }

    void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Save()
    {
        try { JsonStore.Save(FileName, Data); }
        catch (Exception ex) { Log.Error("Saving stats failed", ex); }
    }
}
