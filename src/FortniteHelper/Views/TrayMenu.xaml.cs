using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FortniteHelper.Core;
using FortniteHelper.Infrastructure;
using FortniteHelper.Services;

namespace FortniteHelper.Views;

public partial class TrayMenu : Window
{
    bool _closing;

    public TrayMenu()
    {
        InitializeComponent();
        Refresh();
        Deactivated += (_, _) => SafeClose();
        Loaded += (_, _) => PositionNearCursor();
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) SafeClose(); };
    }

    void Refresh()
    {
        var s = AppServices.Settings;
        Status.Text = AppServices.Session.PhaseText;
        var st = AppServices.Stats;
        ChestCount.Text = st.HasLiveMatch ? $"{st.MatchChests}" : $"{st.TodayChests}";
        ChestSub.Text = st.HasLiveMatch ? $"THIS MATCH · {st.TodayChests} TODAY" : "CHESTS TODAY";
        PirateText.Text = PirateToken.Percent(st.PirateChance);
        CrosshairToggle.IsChecked = s.Crosshair.Mode != CrosshairMode.Off;
        HudToggle.IsChecked = s.Hud.Enabled;
        DetectToggle.IsChecked = s.Detection.Mode == DetectionMode.Auto;
    }

    void PositionNearCursor()
    {
        Native.GetCursorPos(out var pt);
        var mon = Native.WorkArea(Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST));
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.GetWindowRect(hwnd, out var r);
        int x = Math.Clamp(pt.X - r.Width / 2, mon.Left, mon.Right - r.Width);
        int y = Math.Clamp(pt.Y - r.Height, mon.Top, mon.Bottom - r.Height);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE);
        Native.SetForegroundWindow(hwnd);
        Activate();

        if (Root.RenderTransform is ScaleTransform st)
        {
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        }
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
    }

    void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    void Crosshair_Click(object s, RoutedEventArgs e) { AppServices.ToggleCrosshair(); Refresh(); }
    void Hud_Click(object s, RoutedEventArgs e) { AppServices.Settings.Hud.Enabled = HudToggle.IsChecked == true; }
    void Detect_Click(object s, RoutedEventArgs e) =>
        AppServices.Settings.Detection.Mode = DetectToggle.IsChecked == true ? DetectionMode.Auto : DetectionMode.Manual;
    void Open_Click(object s, RoutedEventArgs e) { SafeClose(); App.Current.ShowMainWindow(); }
    void Quit_Click(object s, RoutedEventArgs e) { SafeClose(); App.Current.Quit(); }
}

/// <summary>Notification-area icon. Left click opens the app, right click opens the menu.</summary>
public sealed class TrayService : IDisposable
{
    readonly System.Windows.Forms.NotifyIcon _icon;
    TrayMenu? _menu;

    public TrayService(App app)
    {
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "LootLog",
            Visible = true,
            Icon = LoadIcon(),
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) app.ShowMainWindow();
            else if (e.Button == System.Windows.Forms.MouseButtons.Right) ShowMenu();
        };
        _icon.BalloonTipClicked += (_, _) => app.ShowMainWindow();
    }

    static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.ico"));
            if (res is not null)
            {
                using Stream s = res.Stream;
                return new System.Drawing.Icon(s, System.Windows.Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex) { Log.Warn("Tray icon: " + ex.Message); }
        return System.Drawing.SystemIcons.Application;
    }

    void ShowMenu()
    {
        _menu?.Close();
        _menu = new TrayMenu();
        _menu.Closed += (_, _) => _menu = null;
        _menu.Show();
    }

    public void Balloon(string title, string text)
    {
        if (!AppServices.Settings.General.ShowTrayTips) return;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(4000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
