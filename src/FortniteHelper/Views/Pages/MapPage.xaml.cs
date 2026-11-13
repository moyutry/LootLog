using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using FortniteHelper.Services;
using FortniteHelper.Views.Controls;

namespace FortniteHelper.Views.Pages;

public partial class MapPage : UserControl, IDisposable
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    readonly DispatcherTimer _refresh = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
    Dictionary<string, PlaceStats> _places = new(StringComparer.OrdinalIgnoreCase);
    string? _selected;
    bool _loaded, _fitted;

    public MapPage()
    {
        InitializeComponent();
        Map.BadgeFor = poi => _places.TryGetValue(poi.Name, out var p) && p.KnownChests > 0 ? $"{p.KnownChests} chests" : null;
        Map.PoiClicked += poi => Select(poi.Name);
        Map.SizeChanged += (_, _) => { DrawLayers(); FitOnce(); };
        _refresh.Tick += (_, _) => { _refresh.Stop(); Refresh(); };
        AppServices.Map.Changed += Queue;
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            if (AppServices.Map.Map is null)
            {
                await AppServices.Map.LoadAsync();
                if (AppServices.Map.Map is null) MapLoadingText.Text = "Couldn't load the island map. Check your internet connection.";
            }
            Refresh();
        };
    }

    void Queue()
    {
        if (!_refresh.IsEnabled) _refresh.Start();
    }

    void Refresh()
    {
        var ms = AppServices.Map;
        _places = ms.Places.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        if (ms.Map is { } data)
        {
            MapLoading.Visibility = Visibility.Collapsed;
            Map.SetMap(data);
            Map.Select(_selected);
            FitOnce();
        }
        DrawLayers();
        FillLists();
        if (_selected is not null) ShowPlace(_selected, zoom: false);

        int replays = AppServices.History.Matches.Count(m => m.ChestsVerified);
        AboutText.Text = $"{ms.ChestCount:N0} chest spots learned from your {replays:N0} replays. A chest shows up once it's opened - by you or anyone near you - " +
                         "in one of your matches, so the map fills in the more you play. Bigger dots are rare chests; brighter ones were opened more often.";
    }

    void FitOnce()
    {
        if (_fitted || Map.ActualWidth <= 0 || AppServices.Map.Map is null) return;
        _fitted = true;
        Dispatcher.BeginInvoke(Map.FitIsland, DispatcherPriority.Loaded);
    }

    void DrawLayers()
    {
        Map.ClearLayers();
        if (Map.ActualWidth <= 0) return;
        if (ShowChests.IsChecked == true)
        {
            var normal = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x3F)));
            var rare = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x2E)));
            var stroke = Freeze(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)));
            foreach (var c in AppServices.Map.Chests)
            {
                var dot = new Ellipse
                {
                    Width = c.Rare ? 10 : 7, Height = c.Rare ? 10 : 7,
                    Fill = c.Rare ? rare : normal, Stroke = stroke, StrokeThickness = 1,
                    Opacity = Math.Clamp(0.55 + c.Seen * 0.15, 0.55, 1),
                    ToolTip = $"{(c.Rare ? "Rare chest" : "Chest")} · opened in {c.Seen} of your matches" + (c.Mine > 0 ? $" ({c.Mine}× by you)" : ""),
                };
                Map.AddMarker(c.X, c.Y, dot);
            }
        }
        if (ShowLandings.IsChecked == true)
        {
            var cyan = Freeze(new SolidColorBrush(Color.FromRgb(0x2C, 0xE6, 0xFF)));
            var stroke = Freeze(new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)));
            var since = DateTime.UtcNow.AddDays(-60);
            foreach (var m in AppServices.History.Matches.Where(m => m.LandX is not null && m.StartUtc >= since))
            {
                var dot = new Rectangle
                {
                    Width = 9, Height = 9, Fill = cyan, Stroke = stroke, StrokeThickness = 1.2, RadiusX = 2, RadiusY = 2,
                    ToolTip = $"You landed here · {MatchRow.FormatWhen(m.StartUtc)} · {m.Chests} chests" + (m.Placement is { } p ? $" · #{p}" : ""),
                };
                Map.AddMarker(m.LandX!.Value, m.LandY!.Value, dot);
            }
        }
    }

    static Brush Freeze(Brush b) { b.Freeze(); return b; }

    void FillLists()
    {
        double? cpm = AppServices.Stats.RecentAverageChests ?? AppServices.Stats.AverageChests;
        var best = _places.Values.Where(p => p.Landings > 0 && p.AvgChests is not null)
            .OrderByDescending(p => p.AvgChests).ThenByDescending(p => p.Landings).Take(8).ToList();
        BestList.Items.Clear();
        int rank = 0;
        foreach (var p in best)
        {
            string sub = $"{p.Landings} landing{(p.Landings == 1 ? "" : "s")}" + (p.AvgEnemies is >= 3 ? " · busy" : "");
            BestList.Items.Add(Row(++rank, p.Name, $"{p.AvgChests:0.0}", "chests / match", sub, p.AvgChests >= (cpm ?? 0) + 1));
        }
        NoBest.Visibility = best.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var dense = _places.Values.Where(p => p.KnownChests > 0).OrderByDescending(p => p.KnownChests).Take(6).ToList();
        DenseList.Items.Clear();
        rank = 0;
        foreach (var p in dense)
            DenseList.Items.Add(Row(++rank, p.Name, p.KnownChests.ToString(), "chests", p.Landings > 0 ? $"you: {p.AvgChests:0.0} / match" : "not tried yet", false));
    }

    FrameworkElement Row(int rank, string name, string value, string unit, string sub, bool highlight)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 6), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var border = new Border { Style = (Style)FindResource("InsetBorder"), Padding = new Thickness(10, 7, 12, 7), Child = g };
        if (highlight) border.BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xD2, 0x3F));
        g.Children.Add(new TextBlock { Text = rank.ToString(), FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 16, Foreground = (Brush)FindResource("TextMuted"), VerticalAlignment = VerticalAlignment.Center });
        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Text"), TextTrimming = TextTrimming.CharacterEllipsis });
        mid.Children.Add(new TextBlock { Text = sub, Style = (Style)FindResource("Muted"), FontSize = 11.5 });
        Grid.SetColumn(mid, 1);
        g.Children.Add(mid);
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        right.Children.Add(new TextBlock { Text = value, FontFamily = (FontFamily)FindResource("DisplayFont"), FontSize = 19, Foreground = (Brush)FindResource("Yellow"), HorizontalAlignment = HorizontalAlignment.Right });
        right.Children.Add(new TextBlock { Text = unit.ToUpperInvariant(), Style = (Style)FindResource("Caps"), FontSize = 9, HorizontalAlignment = HorizontalAlignment.Right });
        Grid.SetColumn(right, 2);
        g.Children.Add(right);
        border.MouseLeftButtonUp += (_, _) => Select(name);
        border.Margin = new Thickness(0, 0, 0, 6);
        return border;
    }

    void Select(string name)
    {
        _selected = name;
        Map.Select(name);
        ShowPlace(name, zoom: true);
    }

    void ShowPlace(string name, bool zoom)
    {
        if (!_places.TryGetValue(name, out var p)) { PlaceCard.Visibility = Visibility.Collapsed; return; }
        PlaceCard.Visibility = Visibility.Visible;
        PlaceName.Text = p.Name.ToUpperInvariant();
        double? cpm = AppServices.Stats.RecentAverageChests ?? AppServices.Stats.AverageChests;
        string verdict;
        if (p.Landings == 0)
            verdict = p.KnownChests > 0 ? $"You haven't landed here yet. {p.KnownChests} chest spots are known." : "You haven't landed here yet.";
        else if (cpm is { } c && p.Landings >= 2 && p.AvgChests >= c + 1)
            verdict = $"Good for chests: {p.AvgChests - c:0.0} more per match than your average.";
        else if (cpm is { } c2 && p.Landings >= 2 && p.AvgChests <= c2 - 1)
            verdict = $"Below your average by {c2 - p.AvgChests:0.0} chests per match.";
        else
            verdict = p.Landings == 1 ? "One landing so far - land here again to compare." : "About your normal chest count.";
        if (p.AvgEnemies is >= 3) verdict += $" Busy: about {p.AvgEnemies:0.#} enemies land here with you.";
        PlaceVerdict.Text = verdict;

        PlaceTiles.Children.Clear();
        Tile("KNOWN CHESTS", p.KnownChests.ToString());
        Tile("YOUR LANDINGS", p.Landings.ToString());
        Tile("CHESTS / MATCH", p.AvgChests is { } a ? a.ToString("0.0", Inv) : "-");
        Tile("BEST MATCH", p.Landings > 0 ? p.BestChests.ToString() : "-");
        Tile("ENEMIES NEARBY", p.AvgEnemies is { } e ? e.ToString("0.#", Inv) : "-");
        Tile("OUT IN 2 MIN", p.EarlyDeathRate is { } r ? $"{r * 100:0}%" : "-");

        if (zoom) Map.FitTo([(p.X, p.Y)], 260);
    }

    void Tile(string label, string value)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 8, 10) };
        sp.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Caps"), FontSize = 9.5 });
        sp.Children.Add(new TextBlock { Text = value, Style = (Style)FindResource("H2"), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Left });
        PlaceTiles.Children.Add(sp);
    }

    void ClosePlace_Click(object sender, RoutedEventArgs e)
    {
        _selected = null;
        Map.Select(null);
        PlaceCard.Visibility = Visibility.Collapsed;
        Map.FitIsland();
    }

    void Layer_Click(object sender, RoutedEventArgs e) => DrawLayers();
    void ResetView_Click(object sender, RoutedEventArgs e) => Map.FitIsland();

    public void Dispose()
    {
        _refresh.Stop();
        AppServices.Map.Changed -= Queue;
    }
}
