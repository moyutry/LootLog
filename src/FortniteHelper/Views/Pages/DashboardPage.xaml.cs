using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FortniteHelper.Core;
using FortniteHelper.Services;
using FortniteHelper.Views.Controls;

namespace FortniteHelper.Views.Pages;

public partial class DashboardPage : UserControl, IDisposable
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    readonly DispatcherTimer _refresh = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
    MatchRecord? _lastShown;

    public DashboardPage()
    {
        InitializeComponent();
        _refresh.Tick += (_, _) => { _refresh.Stop(); RefreshAll(); };
        _clock.Tick += (_, _) => RefreshPirate(); // Treasure Hour countdown
        AppServices.Stats.PropertyChanged += OnChanged;
        AppServices.Session.PropertyChanged += OnChanged;
        AppServices.Analysis.PropertyChanged += OnChanged;
        AppServices.History.Changed += Queue;
        AppServices.Map.Changed += Queue;
        Loaded += (_, _) => { RefreshAll(); Float(); _clock.Start(); };
        Unloaded += (_, _) => _clock.Stop();
    }

    void OnChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e) => Queue();

    void Queue()
    {
        if (!_refresh.IsEnabled) _refresh.Start();
    }

    void RefreshAll()
    {
        RefreshHero();
        RefreshPirate();
        RefreshLastMatch();
        RefreshStatus();
        RefreshChart();
        RefreshCareer();
    }

    static string N(double? v) => v is { } x ? x.ToString("0.0", Inv) : "-";

    // ------------------------------------------------------------------ hero
    void RefreshHero()
    {
        var st = AppServices.Stats;
        var last = AppServices.History.Matches.FirstOrDefault(m => m.HasChestCount);
        bool live = st.HasLiveMatch;
        FixPanel.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        TeamPanel.Visibility = Visibility.Collapsed;
        if (live)
        {
            HeroLabel.Text = "THIS MATCH";
            HeroNumber.Value = st.MatchChests;
            ExactBadge.Visibility = Visibility.Collapsed;
            LiveBadge.Visibility = Visibility.Visible;
            HeroSub.Text = "Counting while you play. The exact number (with broken chests and your team) shows up when you leave the match.";
        }
        else if (last is not null)
        {
            HeroLabel.Text = "LAST MATCH";
            HeroNumber.Value = last.Chests;
            ExactBadge.Visibility = last.ChestsVerified ? Visibility.Visible : Visibility.Collapsed;
            LiveBadge.Visibility = last.ChestsVerified ? Visibility.Collapsed : Visibility.Visible;
            if (last.ChestsVerified && last.IsTeamMatch)
            {
                TeamPanel.Visibility = Visibility.Visible;
                TeamNumber.Value = last.TeamChests;
            }
            HeroSub.Text = last.ChestsVerified
                ? $"{last.ChestsSearched} opened · {last.ChestsBroken} broken open" + (last.LiveChests is { } lc && lc != last.Chests ? $"  (during the match we counted {lc})" : "")
                : "The exact number appears a few seconds after you leave the match.";
        }
        else
        {
            HeroLabel.Text = "CHESTS";
            HeroNumber.Value = 0;
            ExactBadge.Visibility = LiveBadge.Visibility = Visibility.Collapsed;
            HeroSub.Text = "Play a match - every chest you open or break is counted.";
        }

        bool team = st.TeamMatches > 0;
        TodayText.Text = team && st.TodayTeamChests > st.TodayChests ? $"{st.TodayChests} · team {st.TodayTeamChests}" : st.TodayChests.ToString("N0");
        double? cpm = st.RecentAverageChests ?? st.AverageChests;
        CpmText.Text = team && st.TeamAverageChests is { } t ? $"{N(cpm)} · team {N(t)}" : N(cpm);
        AllTimeText.Text = st.AllTimeChests.ToString("N0");
    }

    void Add_Click(object s, RoutedEventArgs e) => AppServices.Stats.Add(ContainerKind.Chest, +1, auto: false);
    void Remove_Click(object s, RoutedEventArgs e) => AppServices.Stats.Add(ContainerKind.Chest, -1, auto: false);

    // ------------------------------------------------------------------ pirate
    void RefreshPirate()
    {
        var st = AppServices.Stats;
        var now = DateTime.UtcNow;
        PirateNumber.Text = PirateToken.Percent(st.PirateChance);
        PirateSub.Text = st.SeasonChests > 0
            ? $"Your chance so far, from {st.SeasonChests:N0} chests this season" + (st.BoostedChests > 0 ? $" ({st.BoostedChests:N0} in Treasure Hours)" : "")
            : "Open a chest to start rolling";

        // Treasure Hour: now, or when the next one starts (local time)
        if (PirateToken.CurrentOrNextTreasureHour(now) is { } th && PirateToken.IsActive(now))
        {
            TreasureBox.Visibility = Visibility.Visible;
            if (th.StartUtc <= now)
                TreasureText.Text = $"TREASURE HOUR NOW - chests count {th.Boost:0.#}× until {th.EndUtc.ToLocalTime():HH:mm}. Open everything!";
            else
            {
                var left = th.StartUtc - now;
                string at = th.StartUtc.ToLocalTime().ToString("ddd HH:mm", Inv);
                TreasureText.Text = left.TotalHours < 24
                    ? $"Next Treasure Hour in {Format.Long(left)} ({at}): {th.Boost:0.#}× odds for 2 hours."
                    : $"Next Treasure Hour: {at}, {th.Boost:0.#}× odds for 2 hours.";
            }
        }
        else TreasureBox.Visibility = Visibility.Collapsed;

        var days = st.ChestsPerDay(7);
        double perDay = days.Sum(d => d.Chests) / 7.0;
        double daysLeft = Math.Max(0, (PirateToken.SeasonEndUtc - now).TotalDays);
        PirateDays.Text = PirateToken.IsActive(now) ? $"{Math.Ceiling(daysLeft):0} DAYS LEFT" : "SEASON OVER";
        double projected = PirateToken.ChanceFromLogMiss(Math.Log(1 - Math.Max(0, st.PirateChance)) + PirateToken.LogMiss((long)(perDay * daysLeft)));
        PirateProjected.Text = perDay > 0 ? PirateToken.Percent(projected) : "-";
        bool squad = st.SeasonSquadChests > st.SeasonChests;
        PirateSquadRow.Visibility = squad ? Visibility.Visible : Visibility.Collapsed;
        PirateSquad.Text = PirateToken.Percent(st.SquadPirateChance);
    }

    // ------------------------------------------------------------------ last match
    void RefreshLastMatch()
    {
        var m = AppServices.History.Matches.FirstOrDefault(x => x.Source != "live" || x.EndUtc is not null);
        _lastShown = m;
        bool any = m is not null;
        NoMatches.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ReportBtn.Visibility = LastHeadline.Visibility = LastSub.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (m is null) { LastTips.ItemsSource = null; return; }

        var row = new MatchRow(m);
        LastHeadline.Text = row.Result;
        var parts = new List<string> { row.When, m.ModeName };
        if (m.LandedAt is { } l) parts.Add("landed at " + l);
        if (m.HasChestCount) parts.Add($"{m.Chests} chests" + (m.IsTeamMatch && m.ChestsVerified ? $" (team {m.TeamChests})" : ""));
        if (m.Kills is { } k) parts.Add($"{k} elim{(k == 1 ? "" : "s")}");
        LastSub.Text = string.Join("  ·  ", parts);
        var tips = Coach.ForMatch(m, null, Coach.Baseline(AppServices.History.Matches, m));
        LastTips.ItemsSource = tips.Take(3).ToList();
    }

    void Report_Click(object s, RoutedEventArgs e)
    {
        if (_lastShown is not null) MatchesPage.PendingSelection = _lastShown;
        AppServices.RequestNavigate("matches");
    }

    void Map_Click(object s, RoutedEventArgs e) => AppServices.RequestNavigate("map");

    // ------------------------------------------------------------------ status
    void RefreshStatus()
    {
        var session = AppServices.Session;
        var settings = AppServices.Settings;
        PhaseText.Text = session.InMatch ? "Counting your chests" : session.IsRunning ? "Ready" : "Waiting for Fortnite";
        PhaseHint.Text = session.InMatch
            ? "Good luck! The exact count comes when you leave the match."
            : session.IsRunning ? "Jump into a match - everything is automatic." : "Start Fortnite and play. Nothing to set up.";
        StatusDot.Fill = (Brush)FindResource(session.InMatch ? "Green" : session.IsRunning ? "Accent" : "TextMuted");

        var an = AppServices.Analysis;
        AnalysisRow.Visibility = an.Pending > 0 ? Visibility.Visible : Visibility.Collapsed;
        AnalysisText.Text = an.Status;

        // Warn when matches end without a replay: then only the live estimate exists.
        string? warn = null;
        var recent = AppServices.History.Matches
            .Where(m => m.Source == "live" && m.Duration is { TotalMinutes: > 3 } && m.EndUtc < DateTime.UtcNow.AddMinutes(-12))
            .Take(2).ToList();
        if (recent.Count > 0 && recent.All(m => !m.ChestsVerified) && an.Pending == 0)
            warn = "Your last match wasn't saved as a replay, so we can't give the exact count. In Fortnite: Settings → Game → Replays → turn on \"Record Replays\".";
        else if (ExclusiveFullscreen())
            warn = "Fortnite is set to Fullscreen. That can hide the on-screen counter and make the live count miss chests. Switch to Windowed Fullscreen in Fortnite (Settings → Display → Window Mode). The exact count after the match works either way.";
        else if (!AppServices.Ocr.IsAvailable && settings.Detection.Mode == DetectionMode.Auto)
            warn = "Counting during the match needs the English language pack in Windows (Settings → Time & language → Language). The exact count after each match works anyway.";
        if (AppServices.CleanSnapshots) warn = null;
        WarnBox.Visibility = warn is null ? Visibility.Collapsed : Visibility.Visible;
        WarnText.Text = warn ?? "";

        HudToggle.IsChecked = settings.Hud.Enabled;
        CrosshairToggle.IsChecked = settings.Crosshair.Mode != CrosshairMode.Off;
    }

    DateTime _modeCheckedAt;
    bool _exclusive;

    /// <summary>Reads Fortnite's window mode at most every 30 seconds.</summary>
    bool ExclusiveFullscreen()
    {
        if (DateTime.UtcNow - _modeCheckedAt > TimeSpan.FromSeconds(30))
        {
            _modeCheckedAt = DateTime.UtcNow;
            _exclusive = SettingsPage.DescribeWindowMode().Contains("exclusive", StringComparison.OrdinalIgnoreCase);
        }
        return _exclusive;
    }

    void CrosshairToggle_Click(object s, RoutedEventArgs e)
    {
        var c = AppServices.Settings.Crosshair;
        c.Mode = CrosshairToggle.IsChecked == true ? (c.LastOnMode == CrosshairMode.Off ? CrosshairMode.FortniteOnly : c.LastOnMode) : CrosshairMode.Off;
    }

    void HudToggle_Click(object s, RoutedEventArgs e) => AppServices.Settings.Hud.Enabled = HudToggle.IsChecked == true;

    // ------------------------------------------------------------------ chart + career
    void RefreshChart()
    {
        var days = AppServices.Stats.ChestsPerDay(14);
        ChestChart.Items = days.Select(d => new BarPoint(d.Day.ToString("dd/MM", Inv), d.Chests)).ToList();
        int week = days.Skip(7).Sum(d => d.Chests);
        WeekText.Text = $"{week:N0} this week";
    }

    void RefreshCareer()
    {
        var tips = Coach.Career(AppServices.History.Matches, AppServices.Map.Places);
        CareerTips.ItemsSource = tips.Take(3).ToList();
        NoCareerTips.Visibility = tips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Float()
    {
        if (ChestArt.RenderTransform is not TranslateTransform tt) return;
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-5, 5, TimeSpan.FromSeconds(2.4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
    }

    public void Dispose()
    {
        _refresh.Stop();
        _clock.Stop();
        AppServices.Stats.PropertyChanged -= OnChanged;
        AppServices.Session.PropertyChanged -= OnChanged;
        AppServices.Analysis.PropertyChanged -= OnChanged;
        AppServices.History.Changed -= Queue;
        AppServices.Map.Changed -= Queue;
    }
}
