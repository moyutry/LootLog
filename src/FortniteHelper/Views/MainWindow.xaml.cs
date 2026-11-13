using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FortniteHelper.Core;
using FortniteHelper.Infrastructure;
using FortniteHelper.Services;
using FortniteHelper.Views.Pages;

namespace FortniteHelper.Views;

public partial class MainWindow : Window
{
    static readonly Dictionary<string, (string Title, string Subtitle, Func<FrameworkElement> Create)> Pages = new()
    {
        ["home"] = ("HOME", "Your chests, your odds, your next step", () => new DashboardPage()),
        ["matches"] = ("MY MATCHES", "Every match read from its replay - and how to do better", () => new MatchesPage()),
        ["map"] = ("CHEST MAP", "Where the chests are and where you open the most", () => new MapPage()),
        ["crosshair"] = ("CROSSHAIR", "Custom crosshair, shown only in Fortnite", () => new CrosshairPage()),
        ["settings"] = ("SETTINGS", "HUD, chest counting, hotkeys and startup", () => new SettingsPage()),
    };

    readonly Dictionary<string, FrameworkElement> _cache = [];
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    string _current = "";

    public MainWindow()
    {
        InitializeComponent();
        Wizard.Finished += OnWizardFinished;
        foreach (RadioButton rb in Nav.Children.OfType<RadioButton>())
            rb.Checked += (s, _) => Navigate((string)((RadioButton)s).Tag);

        SourceInitialized += (_, _) => ApplyWindowEffects();
        StateChanged += (_, _) => RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        Loaded += (_, _) =>
        {
            Navigate("home");
            UpdateStatus();
            if (!AppServices.Settings.General.OnboardingDone) ShowWelcome();
        };

        AppServices.NavigateRequested += OnNavigateRequested;
        AppServices.Toast += ShowToast;
        AppServices.Session.PropertyChanged += OnSessionChanged;
        AppServices.Stats.PropertyChanged += OnSessionChanged;
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); HideToast(); };

        Closing += OnClosing;
        Closed += (_, _) =>
        {
            AppServices.NavigateRequested -= OnNavigateRequested;
            AppServices.Toast -= ShowToast;
            AppServices.Session.PropertyChanged -= OnSessionChanged;
            AppServices.Stats.PropertyChanged -= OnSessionChanged;
            foreach (var page in _cache.Values.OfType<IDisposable>()) page.Dispose();
            _cache.Clear();
        };
    }

    void ApplyWindowEffects()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int round = 2, dark = 1;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int border = 0x00402A1E; // COLORREF (BGR): subtle navy border
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    void OnNavigateRequested(string page)
    {
        foreach (RadioButton rb in Nav.Children.OfType<RadioButton>())
            if ((string)rb.Tag == page) { rb.IsChecked = true; return; }
    }

    public FrameworkElement? CurrentPage => PageHost.Content as FrameworkElement;
    public bool WelcomeVisible => Welcome.Visibility == Visibility.Visible;

    /// <summary>The first-run setup wizard (also from Settings → "Show the welcome again").</summary>
    public void ShowWelcome()
    {
        Wizard.Start();
        Welcome.Visibility = Visibility.Visible;
        Welcome.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
        var scale = new ScaleTransform(0.94, 0.94);
        Wizard.RenderTransformOrigin = new Point(0.5, 0.5);
        Wizard.RenderTransform = scale;
        var pop = new DoubleAnimation(1, TimeSpan.FromMilliseconds(380)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    /// <summary>Snapshot aid: shows one wizard step.</summary>
    public void ShowWelcomeStep(int step) => Wizard.GoTo(step, animate: false);
    public int WelcomeSteps => 6;

    public void HideWelcome(bool done)
    {
        Welcome.Visibility = Visibility.Collapsed;
        if (!done) return;
        AppServices.Settings.General.OnboardingDone = true;
        AppServices.SettingsService.QueueSave();
    }

    void OnWizardFinished(bool done)
    {
        HideWelcome(done);
        ShowToast("You're all set - go open some chests!");
    }

    public void Navigate(string key)
    {
        if (key == _current || !Pages.TryGetValue(key, out var def)) return;
        _current = key;
        PageTitle.Text = def.Title;
        PageSubtitle.Text = def.Subtitle;
        if (!_cache.TryGetValue(key, out var page))
        {
            page = def.Create();
            _cache[key] = page;
        }

        PageHost.Content = page;
        var tt = new TranslateTransform(0, 14);
        page.RenderTransform = tt;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        PageTitle.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(300)));
    }

    void OnSessionChanged(object? sender, PropertyChangedEventArgs e) => UpdateStatus();

    void UpdateStatus()
    {
        var s = AppServices.Session;
        StatusText.Text = s.PhaseText;
        StatusSub.Text = s.InMatch ? PlaylistNames.Pretty(s.Current?.Playlist) : s.IsRunning ? "Ready when you are" : "Launch Fortnite to start tracking";
        var color = s.Phase switch
        {
            MatchPhase.NotRunning => (Brush)FindResource("TextMuted"),
            MatchPhase.Lobby => (Brush)FindResource("Accent"),
            MatchPhase.Ending => (Brush)FindResource("Yellow"),
            _ => (Brush)FindResource("Green"),
        };
        StatusDot.Fill = color;
        StatusPulse.Fill = color;
        bool live = s.InMatch;
        if (live && StatusPulse.Tag is null)
        {
            StatusPulse.Tag = "on";
            var grow = new DoubleAnimation(1, 2.6, TimeSpan.FromSeconds(1.5)) { RepeatBehavior = RepeatBehavior.Forever };
            ((ScaleTransform)StatusPulse.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            ((ScaleTransform)StatusPulse.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            StatusPulse.BeginAnimation(OpacityProperty, new DoubleAnimation(0.7, 0, TimeSpan.FromSeconds(1.5)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else if (!live && StatusPulse.Tag is not null)
        {
            StatusPulse.Tag = null;
            StatusPulse.BeginAnimation(OpacityProperty, null);
            StatusPulse.Opacity = 0;
        }

        var stats = AppServices.Stats;
        SideChests.Value = stats.HasLiveMatch ? stats.MatchChests : stats.TodayChests;
        SideChestsLabel.Text = stats.HasLiveMatch ? "CHESTS THIS MATCH" : "CHESTS TODAY";
    }

    public void ShowToast(string text)
    {
        ToastText.Text = text;
        var tt = (TranslateTransform)ToastBox.RenderTransform;
        ToastBox.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } });
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    void HideToast()
    {
        var tt = (TranslateTransform)ToastBox.RenderTransform;
        ToastBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(20, TimeSpan.FromMilliseconds(250)));
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!App.Current.IsQuitting) App.Current.OnMainClosing();
    }

    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
