using System.Windows;
using System.Windows.Media;
using FortniteHelper.Core;

namespace FortniteHelper.Views.Controls;

/// <summary>
/// Draws a crosshair in whole physical pixels. Used by the in-game overlay and by the editor preview
/// (with a zoom factor, so each screen pixel becomes a visible block).
/// Outlines are drawn as one combined shape around the lines (never under them), so translucent lines
/// blend with the game instead of turning dark, and overlapping outlines don't stack.
/// </summary>
public static class CrosshairRenderer
{
    /// <param name="cx">Center pixel column (integer), in pixel units.</param>
    /// <param name="cy">Center pixel row (integer), in pixel units.</param>
    /// <param name="px">Size of one screen pixel in drawing units.</param>
    public static void Draw(DrawingContext dc, CrosshairProfile p, int cx, int cy, double px)
    {
        Color baseColor;
        try { baseColor = (Color)ColorConverter.ConvertFromString(p.Color); }
        catch { baseColor = Colors.Cyan; }

        var pieces = new List<(Int32Rect R, double Opacity)>();

        void Lines(int thickness, int length, int offset, double opacity)
        {
            if (thickness <= 0 || length <= 0) return;
            int a = -(thickness / 2), b = a + thickness; // span around the center pixel
            // top, bottom, left, right
            if (!p.TStyle) pieces.Add((new Int32Rect(cx + a, cy + a - offset - length, thickness, length), opacity));
            pieces.Add((new Int32Rect(cx + a, cy + b + offset, thickness, length), opacity));
            pieces.Add((new Int32Rect(cx + a - offset - length, cy + a, length, thickness), opacity));
            pieces.Add((new Int32Rect(cx + b + offset, cy + a, length, thickness), opacity));
        }

        if (p.InnerLines) Lines(p.InnerThickness, p.InnerLength, p.InnerOffset, p.InnerOpacity);
        if (p.OuterLines) Lines(p.OuterThickness, p.OuterLength, p.OuterOffset, p.OuterOpacity);
        if (p.CenterDot && p.DotThickness > 0)
        {
            int a = -(p.DotThickness / 2);
            pieces.Add((new Int32Rect(cx + a, cy + a, p.DotThickness, p.DotThickness), p.DotOpacity));
        }

        Rect ToRect(Int32Rect r, int grow) => new((r.X - grow) * px, (r.Y - grow) * px, (r.Width + grow * 2) * px, (r.Height + grow * 2) * px);

        // Outline ring = union(expanded pieces) minus union(pieces)
        if (p.Outlines && p.OutlineThickness > 0 && p.OutlineOpacity > 0 && pieces.Count > 0)
        {
            var outer = new GeometryGroup { FillRule = FillRule.Nonzero };
            var inner = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var (r, _) in pieces)
            {
                outer.Children.Add(new RectangleGeometry(ToRect(r, p.OutlineThickness)));
                inner.Children.Add(new RectangleGeometry(ToRect(r, 0)));
            }
            var ring = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
            var ob = new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(p.OutlineOpacity, 0, 1)), 0, 0, 0));
            dc.DrawGeometry(ob, null, ring);
        }

        if (p.Circle && p.CircleRadius > 0)
        {
            double r = p.CircleRadius * px;
            var center = new Point((cx + 0.5) * px, (cy + 0.5) * px);
            if (p.Outlines && p.OutlineOpacity > 0)
            {
                var opn = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * p.OutlineOpacity), 0, 0, 0)), (p.CircleThickness + p.OutlineThickness * 2) * px);
                dc.DrawEllipse(null, opn, center, r, r);
            }
            var cpen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(p.CircleOpacity, 0, 1)), baseColor.R, baseColor.G, baseColor.B)), p.CircleThickness * px);
            dc.DrawEllipse(null, cpen, center, r, r);
        }

        foreach (var (r, opacity) in pieces)
        {
            var brush = new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(opacity, 0, 1)), baseColor.R, baseColor.G, baseColor.B));
            dc.DrawRectangle(brush, null, ToRect(r, 0));
        }
    }

    /// <summary>Furthest pixel the crosshair can reach from the center (for sizing the overlay window).</summary>
    public static int Extent(CrosshairProfile p) =>
        Math.Max(p.OuterOffset + p.OuterLength, p.InnerOffset + p.InnerLength) + p.OutlineThickness + 12 + (p.Circle ? p.CircleRadius + 6 : 0);
}

public enum PreviewScene { Sky, Grass, Storm, Snow, Night, Desert }

/// <summary>Editor preview: a little Fortnite-like scene with the crosshair at 1x - 6x zoom.</summary>
public class CrosshairPreview : FrameworkElement
{
    public static readonly DependencyProperty ProfileProperty = DependencyProperty.Register(nameof(Profile), typeof(CrosshairProfile), typeof(CrosshairPreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnProfileChanged));
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(nameof(Zoom), typeof(int), typeof(CrosshairPreview),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(nameof(Scene), typeof(PreviewScene), typeof(CrosshairPreview),
        new FrameworkPropertyMetadata(PreviewScene.Sky, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowSceneProperty = DependencyProperty.Register(nameof(ShowScene), typeof(bool), typeof(CrosshairPreview),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public CrosshairProfile? Profile { get => (CrosshairProfile?)GetValue(ProfileProperty); set => SetValue(ProfileProperty, value); }
    public int Zoom { get => (int)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public PreviewScene Scene { get => (PreviewScene)GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    public bool ShowScene { get => (bool)GetValue(ShowSceneProperty); set => SetValue(ShowSceneProperty, value); }

    static void OnProfileChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (CrosshairPreview)d;
        if (e.OldValue is CrosshairProfile o) o.PropertyChanged -= self.OnProfilePropertyChanged;
        if (e.NewValue is CrosshairProfile n) n.PropertyChanged += self.OnProfilePropertyChanged;
    }

    void OnProfilePropertyChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e) => InvalidateVisual();

    public CrosshairPreview()
    {
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        SnapsToDevicePixels = true;
        ClipToBounds = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (ShowScene) DrawScene(dc, bounds, Scene);
        if (Profile is null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double px = Math.Max(1, Zoom) / dpi.DpiScaleX; // one screen pixel, magnified
        int cx = (int)(ActualWidth / px / 2), cy = (int)(ActualHeight / px / 2);
        CrosshairRenderer.Draw(dc, Profile, cx, cy, px);
    }

    static void DrawScene(DrawingContext dc, Rect b, PreviewScene scene)
    {
        (Color top, Color bottom, Color ground, Color ground2) = scene switch
        {
            PreviewScene.Grass => (Color.FromRgb(0x7F, 0xD0, 0xFF), Color.FromRgb(0xC8, 0xEE, 0xFF), Color.FromRgb(0x4E, 0xA8, 0x3A), Color.FromRgb(0x2F, 0x7A, 0x26)),
            PreviewScene.Storm => (Color.FromRgb(0x5A, 0x22, 0x9C), Color.FromRgb(0xB0, 0x4C, 0xE8), Color.FromRgb(0x3A, 0x18, 0x6A), Color.FromRgb(0x24, 0x0E, 0x48)),
            PreviewScene.Snow => (Color.FromRgb(0xD8, 0xEC, 0xFF), Color.FromRgb(0xF4, 0xFA, 0xFF), Color.FromRgb(0xF2, 0xF6, 0xFA), Color.FromRgb(0xC8, 0xD8, 0xE8)),
            PreviewScene.Night => (Color.FromRgb(0x08, 0x0C, 0x24), Color.FromRgb(0x1C, 0x24, 0x52), Color.FromRgb(0x14, 0x1E, 0x2E), Color.FromRgb(0x0A, 0x10, 0x1A)),
            PreviewScene.Desert => (Color.FromRgb(0xFF, 0xC8, 0x8A), Color.FromRgb(0xFF, 0xE8, 0xC0), Color.FromRgb(0xE0, 0xA8, 0x5A), Color.FromRgb(0xB8, 0x7A, 0x38)),
            _ => (Color.FromRgb(0x3C, 0x9C, 0xFF), Color.FromRgb(0xA8, 0xDC, 0xFF), Color.FromRgb(0x6A, 0xC0, 0x4A), Color.FromRgb(0x3E, 0x8E, 0x30)),
        };
        double horizon = b.Height * 0.62;
        dc.DrawRectangle(new LinearGradientBrush(top, bottom, 90), null, new Rect(0, 0, b.Width, horizon));
        dc.DrawRectangle(new LinearGradientBrush(ground, ground2, 90), null, new Rect(0, horizon, b.Width, b.Height - horizon));

        // soft clouds / hills
        var cloud = new SolidColorBrush(Color.FromArgb(scene == PreviewScene.Night ? (byte)30 : (byte)150, 255, 255, 255));
        dc.DrawEllipse(cloud, null, new Point(b.Width * 0.18, b.Height * 0.2), b.Width * 0.12, b.Height * 0.06);
        dc.DrawEllipse(cloud, null, new Point(b.Width * 0.26, b.Height * 0.17), b.Width * 0.08, b.Height * 0.07);
        dc.DrawEllipse(cloud, null, new Point(b.Width * 0.78, b.Height * 0.28), b.Width * 0.14, b.Height * 0.05);
        var hill = new SolidColorBrush(Color.FromArgb(90, ground2.R, ground2.G, ground2.B));
        dc.DrawEllipse(hill, null, new Point(b.Width * 0.3, horizon + 6), b.Width * 0.35, b.Height * 0.1);
        dc.DrawEllipse(hill, null, new Point(b.Width * 0.85, horizon + 4), b.Width * 0.3, b.Height * 0.08);

        // a little wooden wall in the distance (contrast check against brown)
        var wood = new SolidColorBrush(Color.FromRgb(0xA8, 0x6A, 0x36));
        dc.DrawRectangle(wood, null, new Rect(b.Width * 0.58, horizon - b.Height * 0.2, b.Width * 0.14, b.Height * 0.22));
        var plank = new Pen(new SolidColorBrush(Color.FromRgb(0x7A, 0x48, 0x20)), 2);
        for (int i = 1; i < 4; i++)
        {
            double y = horizon - b.Height * 0.2 + i * b.Height * 0.055;
            dc.DrawLine(plank, new Point(b.Width * 0.58, y), new Point(b.Width * 0.72, y));
        }
    }
}
