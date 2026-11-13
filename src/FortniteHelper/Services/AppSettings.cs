using System.Collections.ObjectModel;
using System.Windows.Input;
using FortniteHelper.Core;
using FortniteHelper.Infrastructure;

namespace FortniteHelper.Services;

public enum CrosshairMode { Off, FortniteOnly, Always }
public enum OverlayVisibility { FortniteFocused, InMatch, Always }
public enum DetectionMode { Auto, Manual }
public enum HudLayout { Card, Compact, Minimal }
public enum HudTheme { Glass, Solid, Clear }
public enum CaptureMethod { Auto, Dxgi, Gdi }

public sealed class AppSettings : ObservableObject
{
    public int Version { get; set; } = 4;
    public GeneralSettings General { get; set; } = new();
    public CrosshairSettings Crosshair { get; set; } = new();
    public HudSettings Hud { get; set; } = new();
    public DetectionSettings Detection { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();

    public IEnumerable<ObservableObject> Sections() => [General, Crosshair, Hud, Detection, Hotkeys];
}

public sealed class GeneralSettings : ObservableObject
{
    bool _startWithWindows, _startMinimized, _closeToTray = true, _showTips = true, _hideFromCapture = true, _chestSound;
    int _dayStartHour = 5;
    double _chestSoundVolume = 0.5;

    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }
    public bool StartMinimized { get => _startMinimized; set => Set(ref _startMinimized, value); }
    public bool CloseToTray { get => _closeToTray; set => Set(ref _closeToTray, value); }
    public bool ShowTrayTips { get => _showTips; set => Set(ref _showTips, value); }
    /// <summary>Overlays are invisible to screenshots/recordings (and to the chest detector's own capture).</summary>
    public bool HideOverlaysFromCapture { get => _hideFromCapture; set => Set(ref _hideFromCapture, value); }
    /// <summary>"Today" starts at this hour, so late-night sessions count as one day.</summary>
    public int DayStartHour { get => _dayStartHour; set => Set(ref _dayStartHour, Math.Clamp(value, 0, 12)); }
    public bool ChestSound { get => _chestSound; set => Set(ref _chestSound, value); }
    public double ChestSoundVolume { get => _chestSoundVolume; set => Set(ref _chestSoundVolume, value); }
    bool _autoUpdate = true;
    /// <summary>Download new versions from GitHub Releases and install them when Fortnite is closed.</summary>
    public bool AutoUpdate { get => _autoUpdate; set => Set(ref _autoUpdate, value); }
    public bool SeenTrayTip { get; set; }
    /// <summary>The first-run welcome (interact key, replays, HUD) was completed.</summary>
    public bool OnboardingDone { get; set; }
    public bool LegacyImported { get; set; }
}

public sealed class CrosshairSettings : ObservableObject
{
    CrosshairMode _mode = CrosshairMode.FortniteOnly, _lastOnMode = CrosshairMode.FortniteOnly;
    int _activeIndex;
    bool _hideWhileAiming;

    public CrosshairMode Mode
    {
        get => _mode;
        set { if (Set(ref _mode, value) && value != CrosshairMode.Off) LastOnMode = value; }
    }
    public CrosshairMode LastOnMode { get => _lastOnMode; set => Set(ref _lastOnMode, value); }
    public ObservableCollection<CrosshairProfile> Profiles { get; set; } = [];
    public int ActiveIndex { get => _activeIndex; set => Set(ref _activeIndex, value); }
    /// <summary>Hide the custom crosshair while the right mouse button (aim) is held.</summary>
    public bool HideWhileAiming { get => _hideWhileAiming; set => Set(ref _hideWhileAiming, value); }

    public CrosshairProfile Active
    {
        get
        {
            if (Profiles.Count == 0) Profiles.Add(new CrosshairProfile { Name = "My Crosshair" });
            if (ActiveIndex < 0 || ActiveIndex >= Profiles.Count) ActiveIndex = 0;
            return Profiles[ActiveIndex];
        }
    }
}

/// <summary>The in-game chest HUD. Small on purpose: chests and the few numbers that matter.</summary>
public sealed class HudSettings : ObservableObject
{
    bool _enabled = true, _showToday = true, _showAllTime = true, _showAverage = true, _showPirate = true, _animate = true;
    OverlayVisibility _visibility = OverlayVisibility.FortniteFocused;
    HudLayout _layout = HudLayout.Card;
    HudTheme _theme = HudTheme.Glass;
    double _scale = 1.0, _opacity = 0.95, _posX = 0.012, _posY = 0.34;
    string _accent = "#FFD23F";

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public OverlayVisibility Visibility { get => _visibility; set => Set(ref _visibility, value); }
    public HudLayout Layout { get => _layout; set => Set(ref _layout, value); }
    public HudTheme Theme { get => _theme; set => Set(ref _theme, value); }
    public double Scale { get => _scale; set => Set(ref _scale, Math.Clamp(value, 0.5, 2.5)); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0.2, 1)); }
    public string Accent { get => _accent; set => Set(ref _accent, value); }
    /// <summary>Top-left corner as a fraction of the game monitor.</summary>
    public double PosX { get => _posX; set => Set(ref _posX, Math.Clamp(value, 0, 1)); }
    public double PosY { get => _posY; set => Set(ref _posY, Math.Clamp(value, 0, 1)); }

    public bool ShowToday { get => _showToday; set => Set(ref _showToday, value); }
    public bool ShowAllTime { get => _showAllTime; set => Set(ref _showAllTime, value); }
    /// <summary>Average chests per match (CPM).</summary>
    public bool ShowAverage { get => _showAverage; set => Set(ref _showAverage, value); }
    /// <summary>Chance of having found the Pirate Shop token so far.</summary>
    public bool ShowPirate { get => _showPirate; set => Set(ref _showPirate, value); }
    public bool Animate { get => _animate; set => Set(ref _animate, value); }
}

public sealed class DetectionSettings : ObservableObject
{
    DetectionMode _mode = DetectionMode.Auto;
    KeyBind _interact = KeyBind.Key(Key.E);
    double _w = 0.40, _h = 0.50, _offY = 0.0;
    int _window = 3500, _cooldown = 700;
    CaptureMethod _capture = CaptureMethod.Auto;

    /// <summary>Auto = live estimate while playing. The exact count always comes from the replay after the match.</summary>
    public DetectionMode Mode { get => _mode; set => Set(ref _mode, value); }
    /// <summary>The player's Fortnite "Interact" key (E by default, but anything incl. mouse buttons).</summary>
    public KeyBind InteractKey { get => _interact; set => Set(ref _interact, value); }
    public double RegionWidth { get => _w; set => Set(ref _w, Math.Clamp(value, 0.1, 1)); }
    public double RegionHeight { get => _h; set => Set(ref _h, Math.Clamp(value, 0.1, 1)); }
    public double RegionOffsetY { get => _offY; set => Set(ref _offY, Math.Clamp(value, -0.4, 0.4)); }
    public int ConfirmWindowMs { get => _window; set => Set(ref _window, Math.Clamp(value, 500, 8000)); }
    public int CooldownMs { get => _cooldown; set => Set(ref _cooldown, Math.Clamp(value, 0, 5000)); }
    public CaptureMethod Capture { get => _capture; set => Set(ref _capture, value); }
}

/// <summary>Only two optional hotkeys, and they only work while Fortnite is the active window.</summary>
public sealed class HotkeySettings : ObservableObject
{
    KeyBind _crosshair = KeyBind.None, _hud = KeyBind.None;

    public KeyBind ToggleCrosshair { get => _crosshair; set => Set(ref _crosshair, value); }
    public KeyBind ToggleHud { get => _hud; set => Set(ref _hud, value); }
}
