using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using FortniteHelper.Core;
using FortniteHelper.Infrastructure;
using FortniteHelper.Views.Controls;

namespace FortniteHelper.Overlays;

/// <summary>Transparent, top-most, click-through window that never takes focus from the game.</summary>
public class OverlayWindow : Window
{
    bool _clickThrough = true, _excludeFromCapture = true;

    public IntPtr Hwnd { get; private set; }

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        Left = -10000;
        Top = -10000;
        Width = 10;
        Height = 10;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Hwnd = new WindowInteropHelper(this).Handle;
        Native.MakeClickThrough(Hwnd, _clickThrough);
        Native.SetWindowDisplayAffinity(Hwnd, _excludeFromCapture ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
    }

    public bool ClickThrough
    {
        get => _clickThrough;
        set { _clickThrough = value; if (Hwnd != IntPtr.Zero) Native.MakeClickThrough(Hwnd, value); }
    }

    /// <summary>Hidden from screenshots / recordings / our own OCR capture (Windows 10 2004+).</summary>
    public bool ExcludeFromCapture
    {
        get => _excludeFromCapture;
        set { _excludeFromCapture = value; if (Hwnd != IntPtr.Zero) Native.SetWindowDisplayAffinity(Hwnd, value ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE); }
    }

    public void EnsureCreated()
    {
        if (Hwnd == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
    }

    /// <summary>Position and size in physical screen pixels.</summary>
    public void PlacePhysical(int x, int y, int w, int h)
    {
        EnsureCreated();
        Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
    }

    public void MovePhysical(int x, int y)
    {
        EnsureCreated();
        Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOACTIVATE | Native.SWP_NOSIZE);
    }

    public void BringToTop()
    {
        if (Hwnd != IntPtr.Zero) Native.TopMost(Hwnd);
    }

    public void ShowNoActivate()
    {
        if (!IsVisible) Show();
        BringToTop();
    }
}

/// <summary>The in-game custom crosshair. A small window centred on the game, drawn in exact physical pixels.</summary>
public sealed class CrosshairOverlay : OverlayWindow
{
    readonly CrosshairSurface _surface = new();

    public CrosshairOverlay()
    {
        Content = _surface;
        Title = "LootLog Crosshair";
    }

    public CrosshairProfile? Profile
    {
        get => _surface.Profile;
        set { _surface.Profile = value; _surface.InvalidateVisual(); }
    }

    public void Redraw() => _surface.InvalidateVisual();

    /// <summary>Centres the crosshair on the given client area (physical pixels).</summary>
    public void CenterOn(RECT area)
    {
        int extent = Profile is null ? 64 : CrosshairRenderer.Extent(Profile);
        int size = Math.Max(64, extent * 2 + 4);
        if (size % 2 == 1) size++;
        int cx = area.Left + area.Width / 2, cy = area.Top + area.Height / 2;
        _surface.CenterPx = size / 2;
        PlacePhysical(cx - size / 2, cy - size / 2, size, size);
        _surface.InvalidateVisual();
    }

    sealed class CrosshairSurface : FrameworkElement
    {
        public CrosshairProfile? Profile;
        public int CenterPx = 64;

        public CrosshairSurface()
        {
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
            SnapsToDevicePixels = true;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (Profile is null) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            CrosshairRenderer.Draw(dc, Profile, CenterPx, CenterPx, 1.0 / dpi.DpiScaleX);
        }
    }
}

/// <summary>Shows the chest-detection capture area for a few seconds (calibration aid).</summary>
public sealed class RegionOverlay : OverlayWindow
{
    public RegionOverlay()
    {
        Title = "Detection region";
        var rect = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x3F)),
            StrokeThickness = 3,
            StrokeDashArray = [4, 3],
            Fill = new SolidColorBrush(Color.FromArgb(28, 0xFF, 0xD2, 0x3F)),
            RadiusX = 8,
            RadiusY = 8,
        };
        var label = new System.Windows.Controls.TextBlock
        {
            Text = "CHEST DETECTION AREA",
            Foreground = Brushes.White,
            FontFamily = (FontFamily)Application.Current.FindResource("DisplayFont"),
            FontSize = 16,
            Margin = new Thickness(10, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var grid = new System.Windows.Controls.Grid();
        grid.Children.Add(rect);
        grid.Children.Add(label);
        Content = grid;
        ExcludeFromCapture = true;
    }
}
