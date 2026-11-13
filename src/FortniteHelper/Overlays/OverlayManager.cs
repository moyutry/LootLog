using System.ComponentModel;
using System.Windows.Threading;
using FortniteHelper.Core;
using FortniteHelper.Infrastructure;
using FortniteHelper.Services;

namespace FortniteHelper.Overlays;

/// <summary>Owns the overlay windows and decides when each one is visible and where.</summary>
public sealed class OverlayManager
{
    readonly SettingsService _settings;
    readonly GameSession _session;
    readonly GameWatcher _watcher;
    readonly RawInputService _input;
    readonly Dispatcher _ui;
    readonly CrosshairOverlay _crosshair = new();
    readonly HudOverlay _hud;
    RegionOverlay? _region;
    DispatcherTimer? _regionTimer;
    readonly DispatcherTimer _topmostTimer;
    bool _hudEdit, _aiming;

    public HudModel Hud { get; }
    public System.Windows.FrameworkElement HudVisual => _hud.View;
    public bool HudEditMode => _hudEdit;
    public event Action? HudEditModeChanged;

    public OverlayManager(SettingsService settings, GameSession session, StatsService stats, HistoryService history, RawInputService input, Dispatcher ui)
    {
        _settings = settings;
        _session = session;
        _watcher = session.Watcher;
        _input = input;
        _ui = ui;
        Hud = new HudModel(session, stats, history, settings);
        _hud = new HudOverlay(Hud);
        _hud.Dragged += SaveHudPosition;

        settings.CrosshairChanged += UpdateCrosshair;
        settings.Settings.Hud.PropertyChanged += OnHudSetting;
        settings.Settings.General.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GeneralSettings.HideOverlaysFromCapture)) ApplyCaptureExclusion();
        };
        _watcher.FocusChanged += UpdateAll;
        _watcher.GeometryChanged += UpdateAll;
        _watcher.RunningChanged += UpdateAll;
        session.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(GameSession.InMatch) or nameof(GameSession.Phase)) UpdateHud(); };
        input.Input += e =>
        {
            if (e.IsMouse && e.Code == (int)MouseCode.Right)
                _ui.BeginInvoke(() => { _aiming = e.Down; UpdateCrosshair(); });
        };

        // Games sometimes push themselves above top-most windows; re-assert periodically while visible.
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topmostTimer.Tick += (_, _) =>
        {
            if (_crosshair.IsVisible) _crosshair.BringToTop();
            if (_hud.IsVisible) _hud.BringToTop();
        };
        _topmostTimer.Start();

        _crosshair.EnsureCreated();
        _hud.EnsureCreated();
        ApplyCaptureExclusion();
        UpdateAll();
    }

    void OnHudSetting(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HudSettings.PosX) or nameof(HudSettings.PosY) && _hudEdit) return;
        UpdateHud();
    }

    void ApplyCaptureExclusion()
    {
        bool hide = _settings.Settings.General.HideOverlaysFromCapture;
        _crosshair.ExcludeFromCapture = hide;
        _hud.ExcludeFromCapture = hide;
    }

    public void UpdateAll()
    {
        UpdateCrosshair();
        UpdateHud();
    }

    RECT GameArea => _watcher.HasGameRect && _watcher.IsRunning ? _watcher.GameClient : Native.PrimaryMonitor();

    public void UpdateCrosshair()
    {
        if (AppServices.Headless) return;
        var cs = _settings.Settings.Crosshair;
        bool show = cs.Mode switch
        {
            CrosshairMode.Always => true,
            CrosshairMode.FortniteOnly => _watcher.IsFocused,
            _ => false,
        };
        if (show && cs.HideWhileAiming && _aiming && _watcher.IsFocused) show = false;

        if (!show)
        {
            if (_crosshair.IsVisible) _crosshair.Hide();
            return;
        }
        _crosshair.Profile = cs.Active;
        _crosshair.ShowNoActivate(); // show first: WPF may re-apply Left/Top when a window is first shown
        _crosshair.CenterOn(GameArea);
        _crosshair.Redraw();
    }

    public void UpdateHud()
    {
        if (AppServices.Headless) return;
        var h = _settings.Settings.Hud;
        bool show = _hudEdit || (h.Enabled && h.Visibility switch
        {
            OverlayVisibility.Always => true,
            OverlayVisibility.FortniteFocused => _watcher.IsFocused,
            OverlayVisibility.InMatch => _watcher.IsFocused && _session.InMatch,
            _ => false,
        });

        if (!show)
        {
            if (_hud.IsVisible) _hud.Hide();
            return;
        }
        var mon = _watcher.HasGameRect && _watcher.IsRunning ? _watcher.GameMonitor : Native.PrimaryMonitor();
        int x = mon.Left + (int)(h.PosX * mon.Width), y = mon.Top + (int)(h.PosY * mon.Height);
        _hud.ShowNoActivate();
        _hud.MovePhysical(x, y);
    }

    public void SetHudEditMode(bool on)
    {
        if (_hudEdit == on) return;
        _hudEdit = on;
        _hud.SetEditMode(on);
        if (!on) SaveHudPosition();
        UpdateHud();
        HudEditModeChanged?.Invoke();
    }

    void SaveHudPosition()
    {
        if (_hud.Hwnd == IntPtr.Zero || !Native.GetWindowRect(_hud.Hwnd, out var r)) return;
        var mon = Native.MonitorRect(Native.MonitorFromWindow(_hud.Hwnd, Native.MONITOR_DEFAULTTONEAREST));
        var h = _settings.Settings.Hud;
        h.PosX = Math.Clamp((r.Left - mon.Left) / (double)mon.Width, 0, 0.98);
        h.PosY = Math.Clamp((r.Top - mon.Top) / (double)mon.Height, 0, 0.98);
    }

    /// <summary>Snap the HUD to a preset corner (fractions of the monitor).</summary>
    public void SnapHud(double x, double y)
    {
        var h = _settings.Settings.Hud;
        h.PosX = x;
        h.PosY = y;
        UpdateHud();
    }

    /// <summary>Flashes the chest detection region on screen for a few seconds.</summary>
    public void ShowRegion(RECT region, int seconds = 5)
    {
        _region ??= new RegionOverlay();
        _region.PlacePhysical(region.Left, region.Top, region.Width, region.Height);
        _region.ShowNoActivate();
        _regionTimer?.Stop();
        _regionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _regionTimer.Tick += (_, _) => { _regionTimer.Stop(); _region?.Hide(); };
        _regionTimer.Start();
    }

    public void CloseAll()
    {
        _topmostTimer.Stop();
        _crosshair.Close();
        _hud.Close();
        _region?.Close();
    }
}
