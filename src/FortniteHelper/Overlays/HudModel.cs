using System.Globalization;
using System.Windows.Media;
using FortniteHelper.Core;
using FortniteHelper.Services;

namespace FortniteHelper.Overlays;

/// <summary>What the in-game HUD shows: chests this match, today, all time, chests per match and the Pirate token odds.</summary>
public sealed class HudModel : ObservableObject
{
    readonly GameSession _session;
    readonly StatsService _stats;
    readonly HistoryService _history;
    readonly SettingsService _settings;

    public HudModel(GameSession session, StatsService stats, HistoryService history, SettingsService settings)
    {
        _session = session;
        _stats = stats;
        _history = history;
        _settings = settings;
        stats.PropertyChanged += (_, _) => Refresh();
        session.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(GameSession.InMatch) or nameof(GameSession.Phase)) Refresh(); };
        history.Changed += Refresh;
        settings.Settings.Hud.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(HudSettings.Accent)) OnPropertyChanged(nameof(AccentBrush)); Refresh(); };
        stats.Counted += (kind, delta, auto) => { if (kind == ContainerKind.Chest) ChestCounted?.Invoke(kind, delta); };
        Refresh();
    }

    public HudSettings Settings => _settings.Settings.Hud;
    public event Action<ContainerKind, int>? ChestCounted;

    /// <summary>Plays the +1 animation without changing any count (settings preview).</summary>
    public void PreviewChest() => ChestCounted?.Invoke(ContainerKind.Chest, 1);

    int _matchChests, _today, _allTime;
    bool _exact, _cpmVisible, _pirateVisible;
    string _matchLabel = "THIS MATCH", _cpm = "-", _pirate = "0%";

    public int MatchChests { get => _matchChests; private set => Set(ref _matchChests, value); }
    public string MatchLabel { get => _matchLabel; private set => Set(ref _matchLabel, value); }
    /// <summary>The number is the replay's exact count (last match), not the live estimate.</summary>
    public bool Exact { get => _exact; private set => Set(ref _exact, value); }
    public int TodayChests { get => _today; private set => Set(ref _today, value); }
    public int AllTimeChests { get => _allTime; private set => Set(ref _allTime, value); }
    public string Cpm { get => _cpm; private set => Set(ref _cpm, value); }
    public bool CpmVisible { get => _cpmVisible; private set => Set(ref _cpmVisible, value); }
    public string Pirate { get => _pirate; private set => Set(ref _pirate, value); }
    public bool PirateVisible { get => _pirateVisible; private set => Set(ref _pirateVisible, value); }

    public Brush AccentBrush
    {
        get
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(Settings.Accent)); }
            catch { return Brushes.Gold; }
        }
    }

    public void Refresh()
    {
        if (_stats.HasLiveMatch)
        {
            MatchChests = _stats.MatchChests;
            MatchLabel = "THIS MATCH";
            Exact = false;
        }
        else if (_history.Matches.FirstOrDefault(m => m.HasChestCount) is { } last && DateTime.UtcNow - (last.EndUtc ?? last.StartUtc) < TimeSpan.FromHours(3))
        {
            MatchChests = last.Chests;
            Exact = last.ChestsVerified;
            MatchLabel = last.ChestsVerified && last.IsTeamMatch ? $"LAST MATCH · TEAM {last.TeamChests}" : "LAST MATCH";
        }
        else
        {
            MatchChests = 0;
            Exact = false;
            MatchLabel = "THIS MATCH";
        }
        TodayChests = _stats.TodayChests;
        AllTimeChests = _stats.AllTimeChests;
        var avg = _stats.RecentAverageChests ?? _stats.AverageChests;
        Cpm = avg is { } a ? a.ToString("0.0", CultureInfo.InvariantCulture) : "-";
        CpmVisible = Settings.ShowAverage;
        double boost = PirateToken.BoostAt(DateTime.UtcNow);
        Pirate = PirateToken.Percent(_stats.PirateChance) + (boost > 1 ? $" ({boost:0.#}×)" : "");
        PirateVisible = Settings.ShowPirate && PirateToken.IsActive(DateTime.UtcNow);
    }
}
