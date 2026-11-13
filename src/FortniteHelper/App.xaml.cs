using System.Windows;
using System.Windows.Threading;
using FortniteHelper.Infrastructure;
using FortniteHelper.Services;
using FortniteHelper.Views;

namespace FortniteHelper;

public partial class App : Application
{
    const string MutexName = "FortniteHelper.SingleInstance.v2";
    const string ShowEventName = "FortniteHelper.ShowWindow.v2";

    Mutex? _mutex;
    EventWaitHandle? _showEvent;
    TrayService? _tray;
    MainWindow? _main;

    public static new App Current => (App)Application.Current;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Build aid: --render-splash <file.png> draws the installer's splash image with the app's own art, then quits.
        int splash = Array.IndexOf(e.Args, "--render-splash");
        if (splash >= 0 && splash + 1 < e.Args.Length)
        {
            try { RenderSplash(e.Args[splash + 1]); } catch (Exception ex) { Log.Error("Splash", ex); }
            Shutdown();
            return;
        }

        // Developer aid: --snap <folder> renders every page to PNGs. It runs "headless": no single-instance lock,
        // no overlays, no input hooks, no tray icon, no autostart changes - so it can run next to the real app,
        // even during a match. Point LOOTLOG_DATA_DIR at a copy of the data folder.
        int snap = Array.IndexOf(e.Args, "--snap");
        AppServices.Headless = snap >= 0;

        if (!AppServices.Headless)
        {
            _mutex = new Mutex(true, MutexName, out bool first);
            if (!first)
            {
                // Already running: ask the running copy to show its window.
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
                Shutdown();
                return;
            }
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            new Thread(() =>
            {
                while (_showEvent.WaitOne())
                    Dispatcher.BeginInvoke(ShowMainWindow);
            }) { IsBackground = true, Name = "ShowSignal" }.Start();
        }

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Fatal", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Task", ex.Exception); ex.SetObserved(); };

        Log.Info($"Starting LootLog {typeof(App).Assembly.GetName().Version}{(AppServices.Headless ? " (snapshot mode)" : "")}");
        AppServices.Init(Dispatcher);
        if (!AppServices.Headless) _tray = new TrayService(this);

        bool minimized = e.Args.Contains("--minimized") || AppServices.Settings.General.StartMinimized || AppServices.Headless;
        if (!minimized) ShowMainWindow();
        if (snap >= 0 && snap + 1 < e.Args.Length) _ = SnapshotPagesAsync(e.Args[snap + 1]);

        try { await AppServices.StartAsync(); }
        catch (Exception ex) { Log.Error("Startup", ex); }

        // The history import streams ~100 MB of logs; give that memory back before settling in the tray.
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>Developer aid: renders every page to PNG files (LootLog.exe --snap &lt;folder&gt;), then quits.</summary>
    async Task SnapshotPagesAsync(string folder)
    {
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            // Wait for the history import and any replay reading to finish, so the pages show real numbers.
            await Task.Delay(3000);
            for (int i = 0; i < 120 && (AppServices.History.IsImporting || AppServices.Analysis.Pending > 0); i++) await Task.Delay(1000);

            // Off-screen and without focus, so it never interrupts whatever the user is doing.
            _main = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = 0, ShowActivated = false };
            _main.Closed += (_, _) => _main = null;
            _main.Show();
            await Task.Delay(2500);
            if (!AppServices.Settings.General.OnboardingDone && !_main.WelcomeVisible) _main.ShowWelcome();
            if (_main.WelcomeVisible)
            {
                for (int i = 0; i < _main.WelcomeSteps; i++)
                {
                    _main.ShowWelcomeStep(i);
                    await Task.Delay(700);
                    SaveSnapshot(_main, System.IO.Path.Combine(folder, $"welcome-{i + 1}.png"));
                }
                _main.HideWelcome(done: false);
            }
            foreach (var page in new[] { "home", "matches", "map", "crosshair", "settings" })
            {
                if (_main is null) return;
                try { AppServices.RequestNavigate(page); }
                catch (Exception ex) { Log.Error("Snapshot " + page, ex); continue; }
                await Task.Delay(page is "map" or "home" ? 5000 : 2500);
                SaveSnapshot(_main, System.IO.Path.Combine(folder, page + ".png"));
                if (_main.CurrentPage is Views.Pages.MatchesPage mp)
                {
                    // The rest of the match report (map, timeline)
                    foreach (var (offset, name) in new[] { (790.0, "matches-2"), (1700.0, "matches-3") })
                    {
                        mp.ScrollReportTo(offset);
                        await Task.Delay(1500);
                        SaveSnapshot(_main, System.IO.Path.Combine(folder, name + ".png"));
                    }
                }
            }

            // The in-game counter, drawn off-screen over a game-like background.
            var view = new Overlays.HudView { DataContext = AppServices.Overlays.Hud, Margin = new Thickness(18) };
            var stage = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromRgb(0x5A, 0x9C, 0xD8), System.Windows.Media.Color.FromRgb(0x4E, 0x8A, 0x3A), 90),
                Child = view,
            };
            stage.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            stage.Arrange(new Rect(stage.DesiredSize));
            stage.UpdateLayout();
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(stage.ActualWidth * 2), (int)(stage.ActualHeight * 2), 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(stage);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var f = System.IO.File.Create(System.IO.Path.Combine(folder, "hud-overlay.png"))) enc.Save(f);
            Log.Info("Snapshots saved to " + folder);
        }
        catch (Exception ex)
        {
            Log.Error("Snapshots", ex);
        }
        finally
        {
            if (AppServices.Headless) Quit();
        }
    }

    /// <summary>The picture shown while LootLog-Setup.exe installs.</summary>
    void RenderSplash(string file)
    {
        var ui = (System.Windows.Media.FontFamily)FindResource("UiFont");
        var display = (System.Windows.Media.FontFamily)FindResource("DisplayFont");
        var yellow = (System.Windows.Media.Brush)FindResource("Yellow");
        var root = new System.Windows.Controls.Grid { Width = 560, Height = 320 };
        root.Children.Add(new System.Windows.Controls.Border
        {
            CornerRadius = new CornerRadius(0),
            Background = new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x24, 0x70), System.Windows.Media.Color.FromRgb(0x10, 0x13, 0x36), 45),
        });
        var glow = new System.Windows.Shapes.Ellipse
        {
            Width = 340, Height = 340, Margin = new Thickness(-40, -60, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Fill = new System.Windows.Media.RadialGradientBrush(System.Windows.Media.Color.FromArgb(0x70, 0xFF, 0xD2, 0x3F), System.Windows.Media.Color.FromArgb(0, 0xFF, 0xD2, 0x3F)),
        };
        root.Children.Add(glow);
        var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 30) };
        row.Children.Add(new System.Windows.Controls.Image { Source = (System.Windows.Media.ImageSource)FindResource("ArtChest"), Width = 130, Height = 118 });
        var words = new System.Windows.Controls.StackPanel { Margin = new Thickness(22, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new System.Windows.Controls.TextBlock { Text = "LOOT", FontFamily = display, FontSize = 64, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 0, -22) });
        words.Children.Add(new System.Windows.Controls.TextBlock { Text = "LOG", FontFamily = display, FontSize = 64, Foreground = yellow });
        row.Children.Add(words);
        root.Children.Add(row);
        root.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Chest tracker for Fortnite  ·  installing...", FontFamily = ui, FontSize = 15,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 34),
        });
        root.Measure(new Size(560, 320));
        root.Arrange(new Rect(0, 0, 560, 320));
        root.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(560, 320, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var f = System.IO.File.Create(file);
        enc.Save(f);
    }

    static void SaveSnapshot(Window w, string file)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(w.ActualWidth * dpi.DpiScaleX), (int)(w.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(w);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = System.IO.File.Create(file);
        enc.Save(fs);
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception", e.Exception);
        e.Handled = true; // keep the app (and the overlays) alive
    }

    public void ShowMainWindow()
    {
        if (_main is null)
        {
            _main = new MainWindow();
            _main.Closed += (_, _) =>
            {
                _main = null;
                // Free the UI memory while living in the tray.
                Dispatcher.BeginInvoke(() =>
                {
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                }, DispatcherPriority.ApplicationIdle);
            };
            _main.Show();
        }
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.Topmost = true;
        _main.Topmost = false;
        _main.Focus();
    }

    public void ShowPage(string page)
    {
        ShowMainWindow();
        AppServices.RequestNavigate(page);
    }

    /// <summary>The window's close button: hide to the tray (or quit, if the user turned that off).</summary>
    public void OnMainClosing()
    {
        var g = AppServices.Settings.General;
        if (!g.CloseToTray)
        {
            Dispatcher.BeginInvoke(Quit);
            return;
        }
        if (!g.SeenTrayTip)
        {
            g.SeenTrayTip = true;
            AppServices.SettingsService.QueueSave();
            _tray?.Balloon("Still running in the background", "LootLog keeps counting chests and showing your overlays. Right-click the tray icon to quit.");
        }
    }

    public bool IsQuitting { get; private set; }
    public bool MainWindowOpen => _main is { IsVisible: true };

    /// <summary>A tray notification (if the player allows them).</summary>
    public void Notify(string title, string text)
    {
        if (AppServices.Settings.General.ShowTrayTips) _tray?.Balloon(title, text);
    }

    public void Quit()
    {
        if (IsQuitting) return;
        IsQuitting = true;
        AppServices.Updates?.OnQuit();
        _tray?.Dispose();
        _main?.Close();
        AppServices.Shutdown();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _mutex?.Dispose();
        _showEvent?.Dispose();
        base.OnExit(e);
    }
}
