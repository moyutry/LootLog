using System.Windows.Threading;
using FortniteHelper.Core;
using FortniteHelper.Infrastructure;
using FortniteHelper.Overlays;

namespace FortniteHelper.Services;

/// <summary>Application-wide services (created once at startup, live while the app runs in the tray).</summary>
public static class AppServices
{
    public static SettingsService SettingsService { get; private set; } = null!;
    public static AppSettings Settings => SettingsService.Settings;
    public static StatsService Stats { get; private set; } = null!;
    public static HistoryService History { get; private set; } = null!;
    public static AnalysisService Analysis { get; private set; } = null!;
    public static MapService Map { get; private set; } = null!;
    public static GameWatcher Watcher { get; private set; } = null!;
    public static GameSession Session { get; private set; } = null!;
    public static RawInputService Input { get; private set; } = null!;
    public static HotkeyService Hotkeys { get; private set; } = null!;
    public static ScreenCapture Capture { get; private set; } = null!;
    public static OcrService Ocr { get; private set; } = null!;
    public static ChestDetector Detector { get; private set; } = null!;
    public static FortniteApi Api { get; } = new();
    public static SoundService Sound { get; } = new();
    public static OverlayManager Overlays { get; private set; } = null!;
    public static UpdateService Updates { get; private set; } = null!;

    /// <summary>Snapshot mode: no overlays, no input hooks, no autostart changes.</summary>
    public static bool Headless { get; set; }
    /// <summary>Snapshot mode for promo images (LOOTLOG_SNAP_CLEAN=1): hides warnings that only apply to this PC.</summary>
    public static bool CleanSnapshots => Headless && Environment.GetEnvironmentVariable("LOOTLOG_SNAP_CLEAN") == "1";

    /// <summary>Raised when something outside the main window wants a page shown (tray menu etc.).</summary>
    public static event Action<string>? NavigateRequested;
    public static event Action<string>? Toast;

    public static void Init(Dispatcher ui)
    {
        SettingsService = new SettingsService();
        Stats = new StatsService(SettingsService);
        History = new HistoryService(Stats);
        Stats.Attach(History);
        Watcher = new GameWatcher(ui);
        Session = new GameSession(Watcher, Stats);
        Analysis = new AnalysisService(History, () => Session.InMatch, ui);
        Map = new MapService(Api, History, Analysis, ui);
        Input = new RawInputService(start: !Headless);
        Hotkeys = new HotkeyService(SettingsService, Watcher, Input, ui);
        Capture = new ScreenCapture();
        Ocr = new OcrService();
        Detector = new ChestDetector(SettingsService, Session, Stats, Capture, Ocr, Input, ui);
        Overlays = new OverlayManager(SettingsService, Session, Stats, History, Input, ui);
        Updates = new UpdateService(Session, ui);

        Session.MatchEnded += m =>
        {
            History.AddLive(m);
            Analysis.WatchForReplayOf(m);
        };
        Session.MatchStarted += _ =>
        {
            // Get OCR + screen capture ready before the first chest, so the first detection is instant.
            if (Settings.Detection.Mode != DetectionMode.Auto) return;
            Ocr.Warm();
            var region = Detector.Region();
            Task.Run(() => Capture.Capture(new RECT(region.Left, region.Top, region.Left + 8, region.Top + 8), Settings.Detection.Capture));
        };
        Analysis.Analyzed += (m, a) =>
        {
            Task.Run(() => Detector.Learn(a));
            // Tell the player when the exact number for the match they just played is in.
            if (DateTime.UtcNow - a.EndUtc < TimeSpan.FromMinutes(30) && m.LiveChests is { } live && live != m.Chests)
                ShowToast($"Last match: {m.Chests} chests (exact, from the replay). The live counter saw {live}.");
        };
        Stats.Counted += (kind, delta, auto) =>
        {
            if (delta > 0 && kind == ContainerKind.Chest && Settings.General.ChestSound) Sound.PlayChest(Settings.General.ChestSoundVolume);
        };

        Hotkeys.ToggleCrosshair += ToggleCrosshair;
        Hotkeys.ToggleHud += () => Settings.Hud.Enabled = !Settings.Hud.Enabled;

        Settings.General.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GeneralSettings.StartWithWindows) && !Headless) Autostart.Apply(Settings.General.StartWithWindows);
        };
        foreach (var section in new ObservableObject[] { Settings.Detection, Settings.Hotkeys, Settings.Crosshair })
            section.PropertyChanged += (_, _) => RefreshMouseNeeds();
        RefreshMouseNeeds();
        if (Settings.General.StartWithWindows && !Headless) Autostart.Apply(true); // keeps the path current if the exe moved
    }

    public static async Task StartAsync()
    {
        await Session.StartAsync();
        await History.ImportAsync();
        await Map.LoadAsync();
        Analysis.Start();
        Updates.Start();
    }

    public static void RefreshMouseNeeds() => Input.SetMouseNeeded(Hotkeys.NeedsMouse());

    public static void ToggleCrosshair()
    {
        var c = Settings.Crosshair;
        c.Mode = c.Mode == CrosshairMode.Off ? (c.LastOnMode == CrosshairMode.Off ? CrosshairMode.FortniteOnly : c.LastOnMode) : CrosshairMode.Off;
    }

    public static void RequestNavigate(string page) => NavigateRequested?.Invoke(page);
    public static void ShowToast(string text) => Toast?.Invoke(text);

    public static void Shutdown()
    {
        try
        {
            SettingsService.SaveNow();
            Stats.Save();
            History.Save();
            Map.Save();
            Detector.Save();
            Analysis.Dispose();
            Overlays.CloseAll();
            Input.Dispose();
            Watcher.Dispose();
            Capture.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown", ex);
        }
    }
}
