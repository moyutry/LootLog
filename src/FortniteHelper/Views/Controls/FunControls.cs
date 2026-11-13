using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using FortniteHelper.Services;

namespace FortniteHelper.Views.Controls;

/// <summary>
/// Zoomable / pannable island map. Named places are labelled; pages draw their own layers on top
/// (chest spots, a match's path ...) in world meters. Markers keep their size when zooming.
/// </summary>
public class MapView : Border
{
    // World (meters) -> map image transform, fitted against the fortnite-api.com map (markers land on the named locations).
    public static double OffsetU = 0.47715, ScaleU = 3.2228e-4, OffsetV = 0.49639, ScaleV = 3.2249e-4;
    const double Size = 1024;

    public static Point ToMap(double x, double y) => new((OffsetU + x * ScaleU) * Size, (OffsetV + y * ScaleV) * Size);

    static readonly System.Windows.Media.Effects.DropShadowEffect LabelShadow = CreateShadow();

    static System.Windows.Media.Effects.DropShadowEffect CreateShadow()
    {
        var e = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 5, ShadowDepth = 1.5, Opacity = 0.95, Color = Colors.Black };
        e.Freeze();
        return e;
    }

    readonly Grid _content = new() { Width = Size, Height = Size };
    readonly Image _image = new() { Stretch = Stretch.Fill };
    readonly Canvas _shapes = new();
    readonly Canvas _markers = new();
    readonly Canvas _labels = new();
    readonly ScaleTransform _scale = new(1, 1);
    readonly TranslateTransform _pan = new();
    /// <summary>Applied to every marker so it keeps its on-screen size at any zoom.</summary>
    readonly ScaleTransform _inverse = new(1, 1);
    readonly Viewbox _holder;
    readonly Grid _viewport = new() { ClipToBounds = true, Background = Brushes.Transparent };
    Point? _dragStart;
    Point _panStart;
    bool _dragged;
    string? _selected;
    MapData? _map;

    public event Action<MapPoi>? PoiClicked;

    /// <summary>Extra text under a place's name (e.g. "23 chests").</summary>
    public Func<MapPoi, string?>? BadgeFor { get; set; }
    public bool ShowLandmarks { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    public double MaxZoom { get; set; } = 6;

    public MapView()
    {
        CornerRadius = new CornerRadius(14);
        ClipToBounds = true;
        Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x3A, 0x7A));
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _content.Children.Add(_image);
        _content.Children.Add(_shapes);
        _content.Children.Add(_markers);
        _content.Children.Add(_labels);
        var tg = new TransformGroup();
        tg.Children.Add(_scale);
        tg.Children.Add(_pan);
        _holder = new Viewbox { Child = _content, Stretch = Stretch.Uniform, RenderTransform = tg, RenderTransformOrigin = new Point(0.5, 0.5) };
        _viewport.Children.Add(_holder);
        Child = _viewport;

        _viewport.MouseWheel += (_, e) =>
        {
            ZoomAround(e.GetPosition(_viewport), _scale.ScaleX * (e.Delta > 0 ? 1.25 : 1 / 1.25));
            e.Handled = true;
        };
        _viewport.MouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(this); _panStart = new Point(_pan.X, _pan.Y); _dragged = false; _viewport.CaptureMouse(); };
        _viewport.MouseMove += (_, e) =>
        {
            if (_dragStart is not { } s || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - s.X) + Math.Abs(p.Y - s.Y) > 4) _dragged = true;
            _pan.X = _panStart.X + (p.X - s.X);
            _pan.Y = _panStart.Y + (p.Y - s.Y);
            ClampPan();
        };
        _viewport.MouseLeftButtonUp += (_, _) => { _dragStart = null; _viewport.ReleaseMouseCapture(); };
        SizeChanged += (_, _) => { SetZoom(_scale.ScaleX); ClampPan(); };
    }

    double ContentScale => Math.Max(1e-6, Math.Min(_viewport.ActualWidth, _viewport.ActualHeight) / Size);

    void SetZoom(double z)
    {
        z = Math.Clamp(z, 1, MaxZoom);
        _scale.ScaleX = _scale.ScaleY = z;
        // Markers and labels are sized in screen pixels, whatever the window size or zoom.
        _inverse.ScaleX = _inverse.ScaleY = 1 / (z * ContentScale);
    }

    void ZoomAround(Point viewportPoint, double z)
    {
        double old = _scale.ScaleX;
        SetZoom(z);
        double k = _scale.ScaleX / old;
        var c = new Point(_viewport.ActualWidth / 2, _viewport.ActualHeight / 2);
        // Keep the point under the mouse where it is.
        _pan.X = (viewportPoint.X - c.X) - k * (viewportPoint.X - c.X - _pan.X);
        _pan.Y = (viewportPoint.Y - c.Y) - k * (viewportPoint.Y - c.Y - _pan.Y);
        ClampPan();
    }

    void ClampPan()
    {
        double z = _scale.ScaleX;
        double s = Math.Min(_viewport.ActualWidth, _viewport.ActualHeight);
        double maxX = Math.Max(0, (s * z - _viewport.ActualWidth) / 2), maxY = Math.Max(0, (s * z - _viewport.ActualHeight) / 2);
        _pan.X = Math.Clamp(_pan.X, -maxX, maxX);
        _pan.Y = Math.Clamp(_pan.Y, -maxY, maxY);
    }

    public void ResetView()
    {
        SetZoom(1);
        _pan.X = _pan.Y = 0;
    }

    /// <summary>Zooms onto the island (all named places).</summary>
    public void FitIsland()
    {
        if (_map is { Pois.Count: > 0 } m) FitTo(m.Pois.Select(p => (p.X, p.Y)), 230);
        else ResetView();
    }

    /// <summary>Zooms so the given world points (meters) fill the view.</summary>
    public void FitTo(IEnumerable<(double X, double Y)> points, double paddingM = 120)
    {
        var pts = points.Select(p => ToMap(p.X, p.Y)).ToList();
        if (pts.Count == 0 || _viewport.ActualWidth <= 0) { ResetView(); return; }
        double pad = paddingM * ScaleU * Size;
        double minX = pts.Min(p => p.X) - pad, maxX = pts.Max(p => p.X) + pad, minY = pts.Min(p => p.Y) - pad, maxY = pts.Max(p => p.Y) + pad;
        double s = Math.Min(_viewport.ActualWidth, _viewport.ActualHeight);
        double z = Math.Min(_viewport.ActualWidth / ((maxX - minX) / Size * s), _viewport.ActualHeight / ((maxY - minY) / Size * s));
        SetZoom(z);
        // Map point -> untransformed viewport position, then pan it to the center.
        double ox = (_viewport.ActualWidth - s) / 2, oy = (_viewport.ActualHeight - s) / 2;
        var q = new Point(ox + (minX + maxX) / 2 / Size * s, oy + (minY + maxY) / 2 / Size * s);
        var c = new Point(_viewport.ActualWidth / 2, _viewport.ActualHeight / 2);
        _pan.X = -(q.X - c.X) * _scale.ScaleX;
        _pan.Y = -(q.Y - c.Y) * _scale.ScaleX;
        ClampPan();
    }

    public async void SetMap(MapData data)
    {
        bool first = _map is null;
        _map = data;
        RebuildLabels();
        if (!first && _image.Source is not null) return;
        // The blank map + our own labels looks cleaner than the pre-labelled image.
        _image.Source = await ImageCache.GetAsync(data.BlankImage, 2048) ?? await ImageCache.GetAsync(data.PoiImage, 2048);
        _image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)));
    }

    public void Select(string? name)
    {
        _selected = name;
        RebuildLabels();
    }

    public void ClearLayers()
    {
        _shapes.Children.Clear();
        _markers.Children.Clear();
    }

    /// <summary>A shape in map-image coordinates (see <see cref="ToMap"/>); it scales with the zoom.</summary>
    public void AddShape(Shape s) => _shapes.Children.Add(s);

    /// <summary>A marker centered on a world position (meters); it keeps its size when zooming.</summary>
    public void AddMarker(double x, double y, FrameworkElement el, Action? click = null)
    {
        var p = ToMap(x, y);
        el.RenderTransform = _inverse;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.Loaded += (_, _) => { Canvas.SetLeft(el, p.X - el.ActualWidth / 2); Canvas.SetTop(el, p.Y - el.ActualHeight / 2); };
        double w = double.IsNaN(el.Width) ? 10 : el.Width, h = double.IsNaN(el.Height) ? 10 : el.Height;
        Canvas.SetLeft(el, p.X - w / 2);
        Canvas.SetTop(el, p.Y - h / 2);
        if (click is not null)
        {
            el.Cursor = Cursors.Hand;
            // Handled on press so the map doesn't capture the mouse for a drag.
            el.MouseLeftButtonDown += (_, e) => e.Handled = true;
            el.MouseLeftButtonUp += (_, e) => { click(); e.Handled = true; };
        }
        _markers.Children.Add(el);
    }

    /// <summary>Converts a marker size wanted on screen (px) to map-image units at zoom 1.</summary>
    public double Px(double screenPx) => screenPx / ContentScale;

    void RebuildLabels()
    {
        _labels.Children.Clear();
        if (_map is null || !ShowLabels) return;
        var font = (FontFamily)Application.Current.FindResource("DisplayFont");
        foreach (var poi in _map.Pois)
        {
            if (poi.IsLandmark && !ShowLandmarks && poi.Name != _selected) continue;
            var p = ToMap(poi.X, poi.Y);
            bool sel = poi.Name == _selected;
            var stack = new StackPanel { Cursor = Cursors.Hand, Background = Brushes.Transparent };
            var name = new TextBlock
            {
                Text = poi.Name.ToUpperInvariant(),
                Foreground = sel ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x3F)) : poi.IsLandmark ? new SolidColorBrush(Color.FromRgb(0xC8, 0xF6, 0xFF)) : Brushes.White,
                FontSize = sel ? 17 : poi.IsLandmark ? 11 : 13.5,
                FontFamily = font,
                HorizontalAlignment = HorizontalAlignment.Center,
                Effect = LabelShadow,
            };
            stack.Children.Add(name);
            if (BadgeFor?.Invoke(poi) is { } badge)
            {
                stack.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(220, 0x12, 0x15, 0x3A)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(160, 0xFF, 0xD2, 0x3F)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(5, 0, 5, 1),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Child = new TextBlock { Text = badge, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xE9, 0xA0)), FontSize = 10, FontWeight = FontWeights.Bold },
                });
            }
            stack.RenderTransform = _inverse;
            stack.RenderTransformOrigin = new Point(0.5, 0.5);
            stack.Loaded += (_, _) => { Canvas.SetLeft(stack, p.X - stack.ActualWidth / 2); Canvas.SetTop(stack, p.Y - stack.ActualHeight / 2); };
            Canvas.SetLeft(stack, p.X - 40);
            Canvas.SetTop(stack, p.Y - 12);
            stack.MouseLeftButtonDown += (_, e) => e.Handled = true;
            stack.MouseLeftButtonUp += (_, e) => { PoiClicked?.Invoke(poi); e.Handled = true; };
            _labels.Children.Add(stack);
        }
    }
}

/// <summary>
/// Attached image loader: downloads (with disk cache) only when the image scrolls into view, then fades it in.
/// Usage: &lt;Image c:Img.Url="{Binding ImageUrl}" c:Img.DecodeWidth="256" /&gt;
/// </summary>
public static class Img
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached("Url", typeof(string), typeof(Img),
        new PropertyMetadata(null, OnUrlChanged));
    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached("DecodeWidth", typeof(int), typeof(Img),
        new PropertyMetadata(256));

    public static string? GetUrl(DependencyObject d) => (string?)d.GetValue(UrlProperty);
    public static void SetUrl(DependencyObject d, string? v) => d.SetValue(UrlProperty, v);
    public static int GetDecodeWidth(DependencyObject d) => (int)d.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(DependencyObject d, int v) => d.SetValue(DecodeWidthProperty, v);

    static void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image img) return;
        img.Source = null;
        if (e.NewValue is not string) return;
        if (img.IsLoaded) Schedule(img);
        else
        {
            RoutedEventHandler? h = null;
            h = (_, _) => { img.Loaded -= h; Schedule(img); };
            img.Loaded += h;
        }
    }

    static void Schedule(Image img)
    {
        var sv = FindScrollViewer(img);
        if (sv is null || InView(img, sv)) { Load(img); return; }
        ScrollChangedEventHandler? h = null;
        h = (_, _) =>
        {
            if (!InView(img, sv)) return;
            sv.ScrollChanged -= h;
            Load(img);
        };
        sv.ScrollChanged += h;
        img.Unloaded += (_, _) => sv.ScrollChanged -= h;
    }

    static async void Load(Image img)
    {
        var url = GetUrl(img);
        if (url is null) return;
        img.Opacity = 0;
        var bmp = await ImageCache.GetAsync(url, GetDecodeWidth(img));
        if (GetUrl(img) != url || bmp is null) return;
        img.Source = bmp;
        img.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
    }

    static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        for (var p = VisualTreeHelper.GetParent(d); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is ScrollViewer sv) return sv;
        return null;
    }

    static bool InView(FrameworkElement el, ScrollViewer sv)
    {
        try
        {
            var bounds = el.TransformToAncestor(sv).TransformBounds(new Rect(el.RenderSize));
            var view = new Rect(0, -sv.ViewportHeight * 0.5, sv.ViewportWidth, sv.ViewportHeight * 2);
            return view.IntersectsWith(bounds);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
