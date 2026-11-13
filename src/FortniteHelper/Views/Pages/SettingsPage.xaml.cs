using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FortniteHelper.Core;
using FortniteHelper.Infrastructure;
using FortniteHelper.Services;

namespace FortniteHelper.Views.Pages;

public partial class SettingsPage : UserControl, IDisposable
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContext = AppServices.Settings;
        Preview.DataContext = AppServices.Overlays.Hud;
        VersionText.Text = $"Version {typeof(App).Assembly.GetName().Version?.ToString(3)} · free";
        WindowModeHint.Text = AppServices.CleanSnapshots
            ? "The box shows on top of Fortnite in Windowed Fullscreen (Fortnite Settings → Display → Window Mode)."
            : DescribeWindowMode();
        AppServices.Overlays.HudEditModeChanged += UpdateMoveButton;
        AppServices.Detector.PropertyChanged += OnDetector;
        AppServices.Updates.PropertyChanged += OnUpdates;
        Loaded += (_, _) => { UpdateMoveButton(); UpdateDetector(); UpdateUpdates(); };
        Unloaded += (_, _) => { if (AppServices.Overlays.HudEditMode) AppServices.Overlays.SetHudEditMode(false); };
    }

    void OnDetector(object? s, System.ComponentModel.PropertyChangedEventArgs e) => UpdateDetector();
    void OnUpdates(object? s, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(UpdateUpdates);

    void UpdateUpdates()
    {
        var u = AppServices.Updates;
        UpdateText.Text = u.StatusText;
        UpdateBtn.Visibility = u.Available ? Visibility.Visible : Visibility.Collapsed;
        UpdateBtn.IsEnabled = u.State is not (UpdateState.Checking or UpdateState.Downloading);
        UpdateBtn.Content = u.State switch
        {
            UpdateState.Ready => "Restart & update",
            UpdateState.Failed => "Try again",
            _ => "Check now",
        };
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        var u = AppServices.Updates;
        if (u.State == UpdateState.Ready)
        {
            if (AppServices.Session.InMatch && !Dialogs.Confirm(Window.GetWindow(this), "Update now?",
                    "You're in a match. LootLog closes for a few seconds while it updates, so the live count for this match pauses.", "UPDATE")) return;
            u.InstallNow(showWindow: true);
            return;
        }
        await u.CheckAsync(userAsked: true);
        if (u.State == UpdateState.UpToDate) AppServices.ShowToast("You have the newest version");
    }

    void UpdateDetector()
    {
        var d = AppServices.Detector;
        LearnText.Text = !d.OcrAvailable
            ? "The live counter needs Windows' English text recognition (Settings → Time & language → Language → add English). Exact counts from replays work anyway."
            : d.Trained
                ? $"It has learned from {d.LearnedChests} chests and {d.LearnedOthers} ammo boxes in your matches."
                : $"Still learning what chests look like on your screen ({d.LearnedChests} chests, {d.LearnedOthers} ammo boxes so far). It gets better every match.";
        LastDetection.Text = d.Last is { } r ? $"Last search: {r.Time:HH:mm:ss} · \"{Trim(r.Text)}\" · {r.Outcome} · {r.Milliseconds} ms" : "";
    }

    static string Trim(string s)
    {
        s = s.ReplaceLineEndings(" ");
        return s.Length > 40 ? s[..40] + "..." : s;
    }

    void LiveOff(object sender, RoutedEventArgs e) => AppServices.Settings.Detection.Mode = DetectionMode.Manual;

    void UpdateMoveButton()
    {
        bool on = AppServices.Overlays.HudEditMode;
        MoveText.Text = on ? "Done - drag it where you like, then click here" : "Move it on screen";
    }

    void More_Click(object sender, RoutedEventArgs e)
    {
        bool show = Advanced.Visibility != Visibility.Visible;
        Advanced.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        MoreText.Text = show ? "Fewer options  ▴" : "More options  ▾";
    }

    void Welcome_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.ShowWelcome();

    void Move_Click(object sender, RoutedEventArgs e) => AppServices.Overlays.SetHudEditMode(!AppServices.Overlays.HudEditMode);

    void ResetPos_Click(object sender, RoutedEventArgs e) => AppServices.Overlays.SnapHud(0.012, 0.34);

    /// <summary>Reads Fortnite's own display setting to tell the player if overlays will be visible.</summary>
    public static string DescribeWindowMode()
    {
        const string generic = "Overlays appear on top of Fortnite in Windowed Fullscreen mode (Fortnite Settings → Display → Window Mode).";
        try
        {
            var ini = Path.Combine(FortnitePaths.SavedDir, "Config", "WindowsClient", "GameUserSettings.ini");
            if (!File.Exists(ini)) return generic;
            var lines = File.ReadAllLines(ini);
            var line = new[] { "PreferredFullscreenMode=", "LastConfirmedFullscreenMode=", "FullscreenMode=" }
                .Select(key => lines.FirstOrDefault(l => l.StartsWith(key, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(l => l is not null);
            return line?.Split('=')[1].Trim() switch
            {
                "1" => "Your Fortnite runs in Windowed Fullscreen - overlays show on top.",
                "2" => "Your Fortnite runs in Windowed mode - overlays show on top.",
                "0" => "Your Fortnite runs in exclusive Fullscreen, which hides overlays and the live counter. Switch to Windowed Fullscreen (Settings → Display → Window Mode).",
                _ => generic,
            };
        }
        catch { return generic; }
    }

    void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(JsonStore.DataDir) { UseShellExecute = true }); } catch { }
    }

    async void Import_Click(object sender, RoutedEventArgs e)
    {
        int added = await AppServices.History.ImportAsync();
        AppServices.Analysis.ScanFolder();
        AppServices.ShowToast(added > 0 ? $"Found {added} new matches" : "History is up to date");
    }

    void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm(Window.GetWindow(this), "Clear match history?",
                "Saved matches are removed, then whatever Fortnite still has in its logs and replays is read again.", "CLEAR")) return;
        AppServices.History.ClearAll();
        AppServices.ShowToast("Match history cleared");
        _ = ReimportAsync();
    }

    static async Task ReimportAsync()
    {
        await AppServices.History.ImportAsync();
        AppServices.Analysis.ReapplyAll();
    }

    public void Dispose()
    {
        AppServices.Overlays.HudEditModeChanged -= UpdateMoveButton;
        AppServices.Detector.PropertyChanged -= OnDetector;
        AppServices.Updates.PropertyChanged -= OnUpdates;
    }
}
