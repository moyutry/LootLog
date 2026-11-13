using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FortniteHelper.Core;
using FortniteHelper.Infrastructure;
using FortniteHelper.Services;
using FortniteHelper.Views.Pages;

namespace FortniteHelper.Views;

/// <summary>First-run setup, one simple step at a time: hello, chest key, replays, window mode, in-game box, done.</summary>
public partial class WelcomeWizard : UserControl
{
    const int ReplayStep = 2, ModeStep = 3;
    int _step = -1;

    /// <summary>Raised when the player finishes (true) or skips (false) the setup.</summary>
    public event Action<bool>? Finished;

    public WelcomeWizard()
    {
        InitializeComponent();
        DataContext = AppServices.Settings;
        Preview.DataContext = AppServices.Overlays.Hud;
        StreamSwitch.IsChecked = !AppServices.Settings.General.HideOverlaysFromCapture;
        for (int i = 0; i < StepCount; i++)
            Dots.Children.Add(new Border { Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(4, 0, 4, 0) });
    }

    int StepCount => Steps.Children.Count;
    public int Step => _step;

    public void Start() => GoTo(0, forward: true, animate: false);

    public void GoTo(int step, bool forward = true, bool animate = true)
    {
        step = Math.Clamp(step, 0, StepCount - 1);
        if (step == ReplayStep) CheckReplays();
        if (step == ModeStep) CheckWindowMode();

        var next = (FrameworkElement)Steps.Children[step];
        for (int i = 0; i < StepCount; i++)
            if (i != step) ((UIElement)Steps.Children[i]).Visibility = Visibility.Collapsed;
        next.Visibility = Visibility.Visible;
        _step = step;

        if (animate)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var tt = new TranslateTransform(forward ? 46 : -46, 0);
            next.RenderTransform = tt;
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
            next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        }

        bool last = step == StepCount - 1;
        StepCounter.Text = step == 0 ? "SETUP · 30 SECONDS" : last ? "DONE" : $"STEP {step} OF {StepCount - 2}";
        NextText.Text = step == 0 ? "LET'S START" : last ? "LET'S GO!" : "NEXT";
        BackBtn.Visibility = step == 0 ? Visibility.Hidden : Visibility.Visible;
        SkipBtn.Visibility = last ? Visibility.Hidden : Visibility.Visible;
        for (int i = 0; i < Dots.Children.Count; i++)
        {
            var dot = (Border)Dots.Children[i];
            dot.Width = i == step ? 26 : 8;
            dot.Background = i == step ? (Brush)FindResource("Yellow") : i < step ? new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xD2, 0x3F)) : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        }
        if (step == 1) Dispatcher.BeginInvoke(() => KeyBox.Focus());
    }

    void CheckReplays()
    {
        bool on = false;
        try
        {
            var dir = new System.IO.DirectoryInfo(FortnitePaths.DemosDir);
            on = dir.Exists && dir.GetFiles("*.replay").Any(f => f.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-30));
        }
        catch { }
        SetStatus(ReplayBadge, ReplayIcon, on);
        ReplayTitle.Text = on ? "REPLAYS ARE ON" : "TURN ON REPLAYS";
        ReplayText.Text = on
            ? "Fortnite is saving your replays, so every chest gets counted exactly - even the ones you break. Nothing to do here."
            : "LootLog reads the replay Fortnite saves after each match. That's how it knows your exact chests.";
        ReplayHow.Visibility = ReplayRecheck.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    void CheckWindowMode()
    {
        var mode = SettingsPage.DescribeWindowMode();
        bool exclusive = mode.Contains("exclusive", StringComparison.OrdinalIgnoreCase) && !AppServices.CleanSnapshots;
        SetStatus(ModeBadge, ModeIcon, !exclusive);
        ModeTitle.Text = exclusive ? "ONE FORTNITE SETTING" : "YOUR SCREEN IS READY";
        ModeText.Text = exclusive
            ? "Fortnite is in Fullscreen mode, which hides LootLog's box and its live counter. Windowed Fullscreen fixes that."
            : "Fortnite runs in Windowed Fullscreen (or LootLog couldn't tell), so the box shows on top of the game.";
        ModeHow.Visibility = ModeRecheck.Visibility = exclusive ? Visibility.Visible : Visibility.Collapsed;
    }

    void SetStatus(Border badge, Controls.Icon icon, bool good)
    {
        icon.Data = (Geometry)FindResource(good ? "IconCheck" : "IconInfo");
        icon.Foreground = (Brush)FindResource(good ? "Green" : "Orange");
        badge.Background = new SolidColorBrush(good ? Color.FromArgb(0x26, 0x32, 0xD7, 0x4B) : Color.FromArgb(0x26, 0xFF, 0x9F, 0x1C));
    }

    void Recheck_Click(object sender, RoutedEventArgs e)
    {
        if (_step == ReplayStep) CheckReplays();
        if (_step == ModeStep) CheckWindowMode();
        AppServices.ShowToast("Checked again");
    }

    void Stream_Changed(object sender, RoutedEventArgs e) =>
        AppServices.Settings.General.HideOverlaysFromCapture = StreamSwitch.IsChecked != true;

    void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step >= StepCount - 1) Finished?.Invoke(true);
        else GoTo(_step + 1, forward: true);
    }

    void Back_Click(object sender, RoutedEventArgs e) => GoTo(_step - 1, forward: false);

    void Skip_Click(object sender, RoutedEventArgs e) => Finished?.Invoke(true);
}
