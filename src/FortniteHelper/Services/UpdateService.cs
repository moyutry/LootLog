using System.Windows.Threading;
using FortniteHelper.Core;
using FortniteHelper.Infrastructure;
using Velopack;
using Velopack.Sources;

namespace FortniteHelper.Services;

public enum UpdateState { NotInstalled, Idle, Checking, UpToDate, Downloading, Ready, Failed }

/// <summary>
/// Updates from GitHub Releases (Velopack). New versions download in the background and install when it can't get in
/// the way: when Fortnite isn't running and the window is closed, or whenever LootLog next closes. Never mid-match.
/// </summary>
public sealed class UpdateService : ObservableObject
{
    public const string RepoUrl = "https://github.com/moyutry/LootLog";
    static readonly TimeSpan CheckEvery = TimeSpan.FromHours(4);

    readonly UpdateManager? _mgr;
    readonly GameSession _session;
    readonly Dispatcher _ui;
    readonly DispatcherTimer _timer;
    UpdateInfo? _ready;
    DateTime _lastCheck = DateTime.MinValue;
    UpdateState _state;
    int _progress;
    string? _newVersion;
    bool _busy, _applying;

    public UpdateService(GameSession session, Dispatcher ui)
    {
        _session = session;
        _ui = ui;
        if (!AppServices.Headless)
        {
            try
            {
                var mgr = new UpdateManager(new GithubSource(RepoUrl, null, false));
                if (mgr.IsInstalled) _mgr = mgr;
            }
            catch (Exception ex) { Log.Warn("Updates: " + ex.Message); }
        }
        _state = _mgr is null ? UpdateState.NotInstalled : UpdateState.Idle;
        _timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMinutes(10) };
        _timer.Tick += (_, _) => Tick();
        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GameSession.IsRunning)) _ui.BeginInvoke(TryInstallQuietly);
        };
    }

    public bool Available => _mgr is not null;
    public string CurrentVersion => _mgr?.CurrentVersion?.ToString() ?? typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?";
    public string? NewVersion { get => _newVersion; private set => Set(ref _newVersion, value); }
    public int Progress { get => _progress; private set { if (Set(ref _progress, value)) OnPropertyChanged(nameof(StatusText)); } }
    public UpdateState State
    {
        get => _state;
        private set { if (Set(ref _state, value)) OnPropertyChanged(nameof(StatusText)); }
    }

    public string StatusText => State switch
    {
        UpdateState.NotInstalled => $"Version {CurrentVersion}. Updates work when LootLog is installed with LootLog-Setup.exe.",
        UpdateState.Checking => "Looking for a new version...",
        UpdateState.Downloading => $"Downloading version {NewVersion}... {Progress}%",
        UpdateState.Ready => $"Version {NewVersion} is ready. It installs by itself when Fortnite is closed, or now:",
        UpdateState.Failed => "Couldn't check for updates (no internet?). It tries again later.",
        _ => $"You have the newest version ({CurrentVersion}). New ones install by themselves.",
    };

    public void Start()
    {
        if (_mgr is null) return;
        _timer.Start();
        _ui.BeginInvoke(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20)); // let startup (history import) settle first
            await CheckAsync(userAsked: false);
        });
    }

    void Tick()
    {
        if (_ready is not null) TryInstallQuietly();
        else if (DateTime.UtcNow - _lastCheck > CheckEvery) _ = CheckAsync(userAsked: false);
    }

    public async Task CheckAsync(bool userAsked)
    {
        if (_mgr is null || _busy || _ready is not null) return;
        if (!userAsked && !AppServices.Settings.General.AutoUpdate) return;
        _busy = true;
        _lastCheck = DateTime.UtcNow;
        try
        {
            State = UpdateState.Checking;
            var info = await _mgr.CheckForUpdatesAsync();
            if (info is null)
            {
                Log.Info($"Updates: {CurrentVersion} is the newest version");
                State = UpdateState.UpToDate;
                return;
            }
            NewVersion = info.TargetFullRelease.Version.ToString();
            Progress = 0;
            State = UpdateState.Downloading;
            await _mgr.DownloadUpdatesAsync(info, p => _ui.BeginInvoke(() => Progress = p));
            _ready = info;
            State = UpdateState.Ready;
            Log.Info($"Update {NewVersion} downloaded");
            if (!TryInstallQuietly())
                App.Current.Notify("LootLog update ready", $"Version {NewVersion} installs by itself when Fortnite is closed.");
        }
        catch (Exception ex)
        {
            Log.Warn("Update check failed: " + ex.Message);
            State = UpdateState.Failed;
            _lastCheck = DateTime.UtcNow - CheckEvery + TimeSpan.FromMinutes(30); // offline? try again in half an hour
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Installs a downloaded update if nobody would notice: Fortnite closed and the window not open.</summary>
    bool TryInstallQuietly()
    {
        if (_ready is null || _session.IsRunning || _session.InMatch || App.Current.MainWindowOpen) return false;
        InstallNow(showWindow: false);
        return true;
    }

    /// <summary>Closes LootLog, installs the update and starts it again.</summary>
    public void InstallNow(bool showWindow = true)
    {
        if (_mgr is null || _ready is null || _applying) return;
        _applying = true;
        Log.Info($"Installing update {NewVersion}");
        try
        {
            _mgr.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: true, restartArgs: showWindow ? [] : ["--minimized"]);
            App.Current.Quit();
        }
        catch (Exception ex)
        {
            _applying = false;
            Log.Error("Update install", ex);
            State = UpdateState.Failed;
        }
    }

    /// <summary>The user quit LootLog: install a downloaded update on the way out (no restart).</summary>
    public void OnQuit()
    {
        if (_mgr is null || _ready is null || _applying) return;
        _applying = true;
        try { _mgr.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: false); }
        catch (Exception ex) { Log.Warn("Update on exit: " + ex.Message); }
    }
}
