using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using FortniteHelper.Core;
using FortniteHelper.Replays;
using FortniteHelper.Services;
using FortniteHelper.Views.Controls;

namespace FortniteHelper.Views.Pages;

public partial class MatchesPage : UserControl, IDisposable
{
    const string AllModes = "All modes";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A match another page wants opened when this page shows.</summary>
    public static MatchRecord? PendingSelection { get; set; }

    string _range = "all";
    bool _ready;
    int _reportVersion;
    MatchRecord? _shown;
    string _shownStamp = "";

    static string Stamp(MatchRecord m) => $"{m.ChestsVerified}|{m.AnalysisVersion}|{m.Chests}|{m.Placement}|{m.LandedAt}";

    public MatchesPage()
    {
        InitializeComponent();
        AppServices.History.Changed += OnHistoryChanged;
        Loaded += (_, _) =>
        {
            _ready = true;
            RebuildModes();
            Refresh();
        };
        IsVisibleChanged += (_, _) => { if (IsVisible && _ready) ApplyPending(); };
    }

    void OnHistoryChanged()
    {
        ImportText.Text = AppServices.History.IsImporting ? "Scanning..." : "Scan for new matches";
        ImportBtn.IsEnabled = !AppServices.History.IsImporting;
        if (!_ready) return;
        RebuildModes();
        Refresh();
    }

    void RebuildModes()
    {
        var current = ModeFilter.SelectedItem as string ?? AllModes;
        var modes = new List<string> { AllModes };
        modes.AddRange(AppServices.History.Matches.Select(m => m.ModeName).Distinct().OrderBy(x => x));
        ModeFilter.SelectionChanged -= Filter_Changed;
        ModeFilter.ItemsSource = modes;
        ModeFilter.SelectedItem = modes.Contains(current) ? current : AllModes;
        ModeFilter.SelectionChanged += Filter_Changed;
    }

    IEnumerable<MatchRecord> Filtered()
    {
        IEnumerable<MatchRecord> q = AppServices.History.Matches;
        var stats = AppServices.Stats;
        q = _range switch
        {
            "today" => q.Where(m => stats.DayKey(m.StartUtc) == stats.TodayKey),
            "7" => q.Where(m => m.StartUtc > DateTime.UtcNow.AddDays(-7)),
            "30" => q.Where(m => m.StartUtc > DateTime.UtcNow.AddDays(-30)),
            _ => q,
        };
        if (ModeFilter.SelectedItem is string mode && mode != AllModes) q = q.Where(m => m.ModeName == mode);
        return q;
    }

    void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string t) _range = t;
        if (_ready) Refresh();
    }

    void Refresh()
    {
        var list = Filtered().ToList();
        var selected = (List.SelectedItem as MatchRow)?.Match;
        var rows = list.Select(m => new MatchRow(m)).ToList();
        List.ItemsSource = rows;
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selected is not null) List.SelectedItem = rows.FirstOrDefault(r => r.Match == selected);
        if (!ApplyPending() && List.SelectedItem is null && rows.Count > 0) List.SelectedIndex = 0;
        // The selected match may have been updated (replay read): redraw its report.
        if (List.SelectedItem is MatchRow r && ReferenceEquals(r.Match, _shown) && Stamp(r.Match) != _shownStamp) ShowReport(r.Match);

        // ---- My stats ----
        var s = AppServices.History.Summarize(list);
        var withKills = list.Where(m => m.Kills is not null).ToList();
        var counted = list.Where(m => m.HasChestCount).ToList();
        var alive = list.Where(m => m.SecondsAlive is > 0).ToList();
        var acc = list.Where(m => m.Accuracy is not null && (m.DamageDealt ?? 0) > 0).ToList();
        Summary.Children.Clear();
        AddTile("MATCHES", s.Matches.ToString("N0"));
        AddTile("VICTORY ROYALES", s.Wins.ToString("N0"), "Yellow");
        AddTile("WIN RATE", s.Matches > 0 ? $"{s.Wins * 100.0 / s.Matches:0.#}%" : "-");
        AddTile("CHESTS", counted.Sum(m => m.Chests).ToString("N0"), "Yellow");
        AddTile("YOUR CHESTS / MATCH", counted.Count > 0 ? counted.Average(m => m.Chests).ToString("0.0", Inv) : "-", "Yellow");
        var teamMatches = list.Where(m => m.IsTeamMatch && m.ChestsVerified).ToList();
        if (teamMatches.Count > 0) AddTile("TEAM CHESTS / MATCH", teamMatches.Average(m => m.TeamChests).ToString("0.0", Inv), "Accent");
        AddTile("ELIMS / MATCH", withKills.Count > 0 ? (withKills.Sum(m => m.Kills ?? 0) / (double)withKills.Count).ToString("0.00", Inv) : "-");
        AddTile("AVG PLACE", s.AvgPlacement is { } ap ? "#" + ap.ToString("0", Inv) : "-");
        AddTile("TOP 10", s.Matches > 0 ? $"{s.Top10 * 100.0 / s.Matches:0}%" : "-");
        AddTile("TIME ALIVE", alive.Count > 0 ? Format.Long(TimeSpan.FromSeconds(alive.Average(m => m.SecondsAlive!.Value))) : "-");
        AddTile("ACCURACY", acc.Count > 0 ? $"{acc.Average(m => m.Accuracy!.Value) * 100:0}%" : "-");
        AddTile("TIME PLAYED", Format.Long(s.PlayTime));
    }

    bool ApplyPending()
    {
        if (PendingSelection is not { } want) return false;
        PendingSelection = null;
        if (List.ItemsSource is not IEnumerable<MatchRow> rows) return false;
        var row = rows.FirstOrDefault(r => ReferenceEquals(r.Match, want));
        if (row is null) return false;
        List.SelectedItem = row;
        List.ScrollIntoView(row);
        return true;
    }

    void AddTile(string label, string value, string? brush = null)
    {
        var b = new Border { Style = (Style)FindResource("InsetBorder"), Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(14, 8, 14, 8) };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Caps"), FontSize = 10 });
        var v = new TextBlock { Text = value, Style = (Style)FindResource("H2"), FontSize = 22, HorizontalAlignment = HorizontalAlignment.Left };
        if (brush is not null) v.Foreground = (Brush)FindResource(brush);
        sp.Children.Add(v);
        b.Child = sp;
        Summary.Children.Add(b);
    }

    /// <summary>Snapshot aid: scrolls the report.</summary>
    public void ScrollReportTo(double offset) => ReportScroll.ScrollToVerticalOffset(offset);

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var m = (List.SelectedItem as MatchRow)?.Match;
        if (ReferenceEquals(m, _shown) && m is not null && Stamp(m) == _shownStamp) return;
        if (!ReferenceEquals(m, _shown)) ReportScroll.ScrollToTop();
        ShowReport(m);
    }

    // ======================================================================== report

    T Res<T>(string key) => (T)FindResource(key);

    async void ShowReport(MatchRecord? m)
    {
        int version = ++_reportVersion;
        _shown = m;
        _shownStamp = m is null ? "" : Stamp(m);
        if (m is null)
        {
            Report.Children.Clear();
            Report.Children.Add(new TextBlock { Style = Res<Style>("Muted"), Text = "Select a match to see its full report." });
            return;
        }
        var a = await Task.Run(() => AppServices.Analysis.For(m));
        if (version != _reportVersion) return; // another match was selected meanwhile
        Report.Children.Clear();
        var row = new MatchRow(m);

        // ---- Header ----
        var header = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.Children.Add(new ContentPresenter { Content = row, ContentTemplate = Res<DataTemplate>("PlacementBadge") });
        var hs = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        hs.Children.Add(new TextBlock { Text = row.Result, Style = Res<Style>("H2"), FontSize = 22, HorizontalAlignment = HorizontalAlignment.Left, Foreground = row.Victory ? Res<Brush>("Yellow") : Res<Brush>("Text") });
        var sub = new List<string> { m.ModeName, m.StartUtc.ToLocalTime().ToString("ddd MMM d, HH:mm", Inv) };
        if (m.TotalPlayers is { } tp) sub.Add($"{tp} players");
        if (a?.Squad.Count > 0) sub.Add("with " + string.Join(", ", a.Squad));
        hs.Children.Add(new TextBlock { Text = string.Join("  ·  ", sub), Style = Res<Style>("Muted"), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(hs, 1);
        header.Children.Add(hs);
        Report.Children.Add(header);

        // ---- Chests ----
        Report.Children.Add(ChestBanner(m, a));

        // ---- Coach ----
        var tips = Coach.ForMatch(m, a, Coach.Baseline(AppServices.History.Matches, m));
        if (tips.Count > 0)
        {
            Report.Children.Add(Section("HOW IT WENT"));
            Report.Children.Add(new ItemsControl { ItemsSource = tips, ItemTemplate = Res<DataTemplate>("TipTemplate") });
        }

        // ---- Map ----
        if (a is not null && a.Path.Count > 1)
        {
            Report.Children.Add(Section("YOUR MATCH ON THE MAP"));
            Report.Children.Add(MatchMap(m, a));
        }

        // ---- Stats ----
        Report.Children.Add(Section("STATS"));
        var grid = new UniformGrid { Columns = 4 };
        void Stat(string label, string? value)
        {
            if (value is null) return;
            var b = new Border { Style = Res<Style>("InsetBorder"), Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 8, 12, 8) };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = label, Style = Res<Style>("Caps"), FontSize = 9.5 });
            sp.Children.Add(new TextBlock { Text = value, Style = Res<Style>("H2"), FontSize = 18, HorizontalAlignment = HorizontalAlignment.Left });
            b.Child = sp;
            grid.Children.Add(b);
        }
        Stat("PLACEMENT", m.Placement is { } p ? $"#{p}" + (m.TotalPlayers is { } t ? $" / {t}" : "") : null);
        Stat("ELIMINATIONS", m.Kills?.ToString());
        Stat("KNOCKS", m.Knocks?.ToString());
        Stat("ASSISTS", m.Assists?.ToString());
        Stat("DAMAGE DEALT", m.DamageDealt?.ToString("N0"));
        Stat("DAMAGE TAKEN", m.DamageTaken?.ToString("N0"));
        Stat("ACCURACY", m.Accuracy is { } ac ? $"{ac * 100:0.#}%" : null);
        Stat("TIME ALIVE", m.SecondsAlive is { } al ? Format.Long(TimeSpan.FromSeconds(al)) : null);
        Stat("IN THE STORM", m.SecondsInStorm is { } st ? (st < 1 ? "0s" : Format.Long(TimeSpan.FromSeconds(st))) : null);
        Stat("TO FIRST CHEST", m.FirstChestSec is { } fc ? $"{fc:0}s" : null);
        Stat("MATS GATHERED", m.MaterialsGathered?.ToString("N0"));
        Stat("MATS USED", m.MaterialsUsed?.ToString("N0"));
        Stat("DISTANCE", m.DistanceMeters is { } d ? (d >= 1000 ? $"{d / 1000.0:0.0} km" : $"{d} m") : null);
        Stat("REVIVES", m.Revives?.ToString());
        Stat("MATCH LENGTH", m.Duration is { } du ? Format.Duration(du) : null);
        Stat("PING", m.PingMs is { } ping ? $"{ping:0} ms" : null);
        Report.Children.Add(grid);

        // ---- Timeline ----
        if (a is not null)
        {
            Report.Children.Add(Section("TIMELINE"));
            Report.Children.Add(Timeline(m, a));
        }

        // ---- Footer ----
        string source = m.ChestsVerified ? "Chests read from the replay (exact - including broken chests)."
            : m.HasChestCount ? "Chests counted live (estimate). The replay of this match wasn't found."
            : "Chests weren't tracked for this match.";
        Report.Children.Add(new TextBlock
        {
            Style = Res<Style>("Muted"), FontSize = 11.5, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap,
            Text = source + (m.ReplayFile is { } rf ? $"\nReplay: {System.IO.Path.GetFileName(rf)}" : ""),
        });
        var del = new Button { Content = "Delete this match", Style = Res<Style>("DangerBtn"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        del.Click += (_, _) =>
        {
            if (Dialogs.Confirm(Window.GetWindow(this), "Delete match?", "It will be removed from your history.", "DELETE"))
                AppServices.History.Delete(m);
        };
        Report.Children.Add(del);
    }

    TextBlock Section(string title) => new() { Text = title, Style = Res<Style>("Caps"), FontSize = 11.5, Margin = new Thickness(0, 18, 0, 10) };

    FrameworkElement ChestBanner(MatchRecord m, MatchAnalysis? a)
    {
        var border = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 14, 18, 14), BorderThickness = new Thickness(1) };
        border.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xD2, 0x3F));
        border.Background = new LinearGradientBrush(Color.FromArgb(0x40, 0xFF, 0xB0, 0x2E), Color.FromArgb(0x10, 0xFF, 0xD2, 0x3F), 0);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new Image { Source = Res<ImageSource>("ArtChest"), Width = 64, Height = 58, VerticalAlignment = VerticalAlignment.Center });
        var num = new StackPanel { Margin = new Thickness(14, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
        num.Children.Add(new TextBlock { Text = m.HasChestCount ? m.Chests.ToString() : "-", FontFamily = Res<FontFamily>("DisplayFont"), FontSize = 48, Foreground = Res<Brush>("GoldGradient"), Margin = new Thickness(0, -6, 0, -6) });
        num.Children.Add(new TextBlock { Text = "CHESTS", Style = Res<Style>("Caps"), Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xE9, 0xA0)) });
        Grid.SetColumn(num, 1);
        g.Children.Add(num);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (m.ChestsVerified)
        {
            var badge = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            badge.Children.Add(new Icon { Data = Res<Geometry>("IconCheck"), Width = 14, Height = 14, Foreground = Res<Brush>("Green") });
            badge.Children.Add(new TextBlock { Text = "Exact - read from the replay", Margin = new Thickness(6, 0, 0, 0), FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("Green") });
            info.Children.Add(badge);
            var parts = new List<string> { $"{m.ChestsSearched} opened", $"{m.ChestsBroken} broken open", $"{m.AmmoBoxes} ammo boxes" };
            info.Children.Add(new TextBlock { Text = string.Join("  ·  ", parts), Foreground = Res<Brush>("Text"), TextWrapping = TextWrapping.Wrap });
            if (m.IsTeamMatch)
            {
                // Who in the team opened what
                var team = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                team.Children.Add(new TextBlock { Text = $"TEAM {m.TeamChests}", FontFamily = Res<FontFamily>("DisplayFont"), FontSize = 16, Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xE7, 0xFF)), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
                var members = a?.SquadMembers.OrderByDescending(s => s.IsMe).ThenByDescending(s => s.Chests).ToList() ?? [];
                foreach (var mem in members)
                {
                    var chip = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 4), Background = new SolidColorBrush(Color.FromArgb(0x30, 0x2C, 0xE6, 0xFF)) };
                    chip.Child = new TextBlock { Text = $"{(mem.IsMe ? "You" : mem.Name)} {mem.Chests}", Foreground = Res<Brush>("Text"), FontSize = 12, FontWeight = mem.IsMe ? FontWeights.Bold : FontWeights.Normal };
                    team.Children.Add(chip);
                }
                if (members.Count == 0 && m.TeamChests > m.Chests)
                    team.Children.Add(new TextBlock { Text = $"you {m.Chests} · teammates {m.TeamChests - m.Chests}", Foreground = Res<Brush>("TextDim"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                team.ToolTip = "Chests your teammates opened near enough to you to be in your replay - usually all of them when you stick together.";
                info.Children.Add(team);
            }
            if (m.LiveChests is { } live)
                info.Children.Add(new TextBlock { Text = $"The live counter saw {live} during the match.", Style = Res<Style>("Muted"), FontSize = 12, Margin = new Thickness(0, 3, 0, 0) });
        }
        else
        {
            info.Children.Add(new TextBlock
            {
                Text = m.HasChestCount ? "Live estimate - no replay found for this match. Broken chests are not included." : "Not tracked - no replay found for this match.",
                Foreground = Res<Brush>("TextDim"), TextWrapping = TextWrapping.Wrap,
            });
        }
        if (PirateToken.IsActive(m.StartUtc) && m.HasChestCount && m.Chests > 0)
            info.Children.Add(new TextBlock
            {
                Text = $"Pirate token rolls this match: {m.Chests} (about {PirateToken.Percent(PirateToken.Chance(m.Chests))})",
                Style = Res<Style>("Muted"), FontSize = 12, Margin = new Thickness(0, 3, 0, 0),
            });
        Grid.SetColumn(info, 2);
        g.Children.Add(info);
        border.Child = g;
        return border;
    }

    FrameworkElement MatchMap(MatchRecord m, MatchAnalysis a)
    {
        var map = new MapView { Height = 420, ShowLandmarks = false, MaxZoom = 8 };
        if (AppServices.Map.Map is { } data) map.SetMap(data);
        double px = 1;
        Brush B(byte r, byte g, byte b, byte alpha = 255) { var br = new SolidColorBrush(Color.FromArgb(alpha, r, g, b)); br.Freeze(); return br; }
        var gold = B(0xFF, 0xD2, 0x3F);
        var orange = B(0xFF, 0x8A, 0x2E);
        var ammo = B(0x9F, 0xE0, 0x70);
        var red = B(0xFF, 0x4D, 0x6D);
        var cyan = B(0x2C, 0xE6, 0xFF);

        void Draw()
        {
            map.ClearLayers();
            px = map.Px(1);
            // Storm: the circle that was closing when the match ended for you.
            float end = a.DeathT ?? a.LengthSec;
            var zone = a.Storm.LastOrDefault(z => z.ShrinkStart <= end) ?? a.Storm.FirstOrDefault();
            if (zone is not null)
            {
                var c = MapView.ToMap(zone.X, zone.Y);
                double r = zone.Radius * MapView.ScaleU * 1024;
                var ring = new Ellipse { Width = r * 2, Height = r * 2, Stroke = B(0xE0, 0x40, 0xFB, 200), StrokeThickness = 2.5 * px, Fill = B(0xE0, 0x40, 0xFB, 18), IsHitTestVisible = false };
                Canvas.SetLeft(ring, c.X - r);
                Canvas.SetTop(ring, c.Y - r);
                map.AddShape(ring);
            }
            // The route
            var line = new Polyline { Stroke = B(0xFF, 0xFF, 0xFF, 210), StrokeThickness = 2.4 * px, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
            foreach (var p in a.Path.Where(p => a.LandT is null || p.T >= a.LandT - 2)) line.Points.Add(MapView.ToMap(p.X, p.Y));
            map.AddShape(line);
            // Glide from the bus (dashed)
            var glide = new Polyline { Stroke = B(0x2C, 0xE6, 0xFF, 170), StrokeThickness = 1.8 * px, StrokeDashArray = [3, 3], IsHitTestVisible = false };
            foreach (var p in a.Path.Where(p => a.LandT is { } lt && p.T <= lt + 1 && p.T >= (a.BusJumpT ?? 0))) glide.Points.Add(MapView.ToMap(p.X, p.Y));
            map.AddShape(glide);

            if (a.Land is { } land)
                map.AddMarker(land.X, land.Y, Dot(14, cyan, "Landed" + (m.LandedAt is { } la ? " at " + la : "") + $" · {a.EnemiesNearLanding} enemies within 150 m"));
            int n = 0;
            foreach (var o in a.Opened.Where(o => o.Pos is not null))
            {
                var tip = $"{Clock(o.T)}  {(o.Type == ContainerType.Chest ? $"Chest #{++n}" : o.Type == ContainerType.AmmoBox ? "Ammo box" : ContainerTypes.ShortName(o.Class))}" +
                          (o.Type == ContainerType.Chest ? (o.Searched ? " (searched)" : " (broken open)") : "") + LootText(o, "\n");
                var brush = o.Type == ContainerType.Chest ? (o.Searched ? gold : orange) : ammo;
                map.AddMarker(o.Pos!.X, o.Pos.Y, Dot(o.Type == ContainerType.Chest ? 11 : 7, brush, tip));
            }
            foreach (var f in a.Fights.Where(f => f.Pos is not null))
            {
                bool mine = f.Kind is "Knock" or "Elim";
                string text = f.Kind switch
                {
                    "Knock" => $"Knocked {f.Other ?? "an enemy"}",
                    "Elim" => $"Eliminated {f.Other ?? "an enemy"}",
                    "KnockedBy" => $"Knocked by {f.Other ?? "an enemy"}",
                    _ => $"Eliminated by {f.Other ?? "an enemy"}",
                } + Coach.With(f.Weapon) + (f.DistanceM is { } d ? $" ({d:0} m)" : "");
                map.AddMarker(f.Pos!.X, f.Pos.Y, Cross(mine ? red : B(0xFF, 0xFF, 0xFF), $"{Clock(f.T)}  {text}"));
            }
        }

        // Re-draw once the map has a size (marker sizes depend on it), then zoom to the match.
        bool fitted = false;
        map.SizeChanged += (_, _) =>
        {
            Draw();
            if (fitted) return;
            fitted = true;
            var pts = a.Path.Where(p => a.LandT is null || p.T >= a.LandT - 2).Select(p => ((double)p.X, (double)p.Y)).ToList();
            map.Dispatcher.BeginInvoke(() => map.FitTo(pts, 140), DispatcherPriority.Loaded);
        };

        var panel = new StackPanel();
        panel.Children.Add(map);
        var legend = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        void Legend(FrameworkElement icon, string text)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 4) };
            icon.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(icon);
            sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), Style = Res<Style>("Muted"), FontSize = 12 });
            legend.Children.Add(sp);
        }
        Legend(new Ellipse { Width = 10, Height = 10, Fill = gold }, "Chest searched");
        Legend(new Ellipse { Width = 10, Height = 10, Fill = orange }, "Chest broken");
        Legend(new Ellipse { Width = 7, Height = 7, Fill = ammo }, "Ammo box");
        Legend(new Ellipse { Width = 10, Height = 10, Fill = cyan }, "Landing");
        Legend(new TextBlock { Text = "✕", Foreground = red, FontWeight = FontWeights.Bold }, "Your knocks / elims");
        Legend(new TextBlock { Text = "✕", Foreground = Brushes.White, FontWeight = FontWeights.Bold }, "Where you went down");
        Legend(new Ellipse { Width = 12, Height = 12, Stroke = B(0xE0, 0x40, 0xFB), StrokeThickness = 2 }, "Storm circle");
        panel.Children.Add(legend);
        panel.Children.Add(new TextBlock { Text = "Scroll to zoom, drag to move. Hover a marker for details.", Style = Res<Style>("Muted"), FontSize = 11.5 });
        return panel;
    }

    static FrameworkElement Dot(double size, Brush fill, string tip) => new Ellipse
    {
        Width = size, Height = size, Fill = fill, ToolTip = tip,
        Stroke = new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)), StrokeThickness = 1.5,
    };

    static FrameworkElement Cross(Brush color, string tip) => new TextBlock
    {
        Text = "✕", Foreground = color, FontWeight = FontWeights.Black, FontSize = 15, ToolTip = tip,
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 1, Color = Colors.Black },
    };

    static string Clock(float t) => $"{(int)(t / 60)}:{(int)(t % 60):00}";

    static string LootText(OpenedContainer o, string prefix)
    {
        var names = o.Loot.Select(LootName).Where(n => n is not null).Distinct().ToList();
        return names.Count == 0 ? "" : prefix + string.Join(", ", names);
    }

    /// <summary>Turns item codenames ("WID_Shotgun_..._Athena_R") into "Rare Shotgun". Ammo and materials are left out.</summary>
    static string? LootName(string raw)
    {
        if (raw.StartsWith("AthenaAmmo", StringComparison.OrdinalIgnoreCase) || raw.EndsWith("ItemData", StringComparison.OrdinalIgnoreCase)) return null;
        string rarity = raw.EndsWith("_UC") ? "Uncommon " : raw.EndsWith("_C") ? "Common " : raw.EndsWith("_VR") ? "Epic " : raw.EndsWith("_SR") ? "Legendary " : raw.EndsWith("_R") ? "Rare " : "";
        (string Key, string Name, bool Weapon)[] map =
        [
            ("Shotgun", "Shotgun", true), ("SMG", "SMG", true), ("Pistol", "Pistol", true), ("Assault", "Assault Rifle", true),
            ("Sniper", "Sniper", true), ("DMR", "DMR", true), ("Slingshot", "Slingshot", true), ("Bow", "Bow", true),
            ("Launcher", "Launcher", true), ("Minigun", "Minigun", true), ("Rifle", "Rifle", true),
            ("MedBox", "Med Kit", false), ("Medkit", "Med Kit", false), ("Bandage", "Bandages", false), ("ShieldSmall", "Small Shield", false),
            ("ShieldPot", "Shield Potion", false), ("Shield", "Shield", false), ("Grenade", "Grenade", false), ("Stimpulse", "Heal", false),
        ];
        foreach (var (key, name, weapon) in map)
            if (raw.Contains(key, StringComparison.OrdinalIgnoreCase)) return weapon ? rarity + name : name;
        return null;
    }

    FrameworkElement Timeline(MatchRecord m, MatchAnalysis a)
    {
        var events = new List<(float T, string Icon, string Brush, string Text)>();
        if (a.BusJumpT is { } bj) events.Add((bj, "IconBus", "Accent", "Jumped from the Battle Bus"));
        if (a.LandT is { } lt)
            events.Add((lt, "IconPin", "Accent", "Landed" + (m.LandedAt is { } la ? $" at {la}" : "") +
                (a.EnemiesNearLanding > 0 ? $" - {a.EnemiesNearLanding} enem{(a.EnemiesNearLanding == 1 ? "y" : "ies")} within 150 m" : " - nobody nearby")));
        int n = 0;
        foreach (var o in a.Opened)
        {
            if (o.Type == ContainerType.Chest)
                events.Add((o.T, "IconChest", "Yellow", $"{(o.Rare ? "Rare chest" : "Chest")} #{++n} {(o.Searched ? "searched" : "broken open")}{LootText(o, " - ")}"));
            else if (o.Type == ContainerType.AmmoBox)
                events.Add((o.T, "IconAmmo", "Green", "Ammo box" + LootText(o, " - ")));
        }
        foreach (var f in a.Fights)
        {
            string who = f.Other ?? "an enemy";
            string dist = f.DistanceM is { } d ? $" from {d:0} m" : "";
            events.Add(f.Kind switch
            {
                "Knock" => (f.T, "IconTarget", "Red", $"You knocked {who}{Coach.With(f.Weapon)}{dist}"),
                "Elim" => (f.T, "IconTarget", "Red", $"You eliminated {who}{Coach.With(f.Weapon)}{dist}"),
                "KnockedBy" => (f.T, "IconHeart", "Orange", $"Knocked by {who}{Coach.With(f.Weapon)}{dist}"),
                _ => (f.T, "IconHeart", "Red", $"Eliminated by {who}{Coach.With(f.Weapon)}{dist}"),
            });
        }
        float end = a.DeathT ?? a.LengthSec;
        foreach (var z in a.Storm.Where(z => z.ShrinkStart < end && z.ShrinkStart > (a.BusJumpT ?? 0)))
            events.Add((z.ShrinkStart, "IconStorm", "Purple", $"Storm {z.Phase} starts closing"));
        if (m.IsVictory) events.Add((a.LengthSec, "IconCrown", "Yellow", "Victory Royale!"));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        int r = 0;
        foreach (var e in events.OrderBy(e => e.T))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var time = new TextBlock { Text = Clock(e.T), Style = Res<Style>("Muted"), FontSize = 12, Margin = new Thickness(0, 3, 0, 3), FontFamily = new FontFamily("Consolas") };
            var icon = new Icon { Data = Res<Geometry>(e.Icon), Width = 15, Height = 15, Foreground = Res<Brush>(e.Brush), Margin = new Thickness(0, 3, 10, 3), VerticalAlignment = VerticalAlignment.Top };
            var text = new TextBlock { Text = e.Text, Foreground = Res<Brush>("TextDim"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
            Grid.SetRow(time, r); Grid.SetRow(icon, r); Grid.SetRow(text, r);
            Grid.SetColumn(icon, 1); Grid.SetColumn(text, 2);
            grid.Children.Add(time); grid.Children.Add(icon); grid.Children.Add(text);
            r++;
        }
        return grid;
    }

    async void Import_Click(object sender, RoutedEventArgs e)
    {
        int added = await AppServices.History.ImportAsync();
        AppServices.Analysis.ScanFolder();
        AppServices.ShowToast(added > 0 ? $"Found {added} new match{(added == 1 ? "" : "es")}" : "History is up to date");
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"fortnite-matches-{DateTime.Now:yyyy-MM-dd}.csv", Filter = "CSV file|*.csv" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        File.WriteAllText(dlg.FileName, AppServices.History.ExportCsv(), new System.Text.UTF8Encoding(true));
        AppServices.ShowToast("Exported!");
    }

    public void Dispose() => AppServices.History.Changed -= OnHistoryChanged;
}
