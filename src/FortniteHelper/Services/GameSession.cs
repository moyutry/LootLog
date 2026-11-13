using System.IO;
using System.Text;
using System.Windows.Threading;
using FortniteHelper.Core;
using FortniteHelper.Infrastructure;

namespace FortniteHelper.Services;

/// <summary>
/// Live view of the game: tails FortniteGame.log, drives a <see cref="MatchTracker"/> and combines it with
/// <see cref="GameWatcher"/> (running / focused).
/// </summary>
public sealed class GameSession : ObservableObject, IDisposable
{
    readonly GameWatcher _watcher;
    readonly StatsService _stats;
    readonly MatchTracker _tracker = new();
    readonly DispatcherTimer _tail;
    long _pos;
    DateTime _fileCreated;
    bool _ready;

    public GameSession(GameWatcher watcher, StatsService stats)
    {
        _watcher = watcher;
        _stats = stats;
        _tracker.MatchStarted += m => { if (_ready) OnMatchStarted(m); };
        _tracker.MatchEnded += OnTrackerMatchEnded;
        _tracker.StormStepObserved += o => { if (_ready) _stats.LearnStorm(o); else _catchUpStorm.Add(o); };
        _tracker.Changed += () => { if (_ready) Raise(); };
        _watcher.RunningChanged += OnRunningChanged;
        _watcher.FocusChanged += () => OnPropertyChanged(nameof(IsFocused));
        _tail = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        _tail.Tick += (_, _) => Poll();
    }

    readonly List<MatchRecord> _catchUpEnded = [];
    readonly List<StormObservation> _catchUpStorm = [];

    public bool IsRunning => _watcher.IsRunning;
    public bool IsFocused => _watcher.IsFocused;
    public MatchPhase Phase => !_watcher.IsRunning && _tracker.Current is null ? MatchPhase.NotRunning : _tracker.Phase;
    public bool InMatch => _tracker.Current is not null;
    public MatchRecord? Current => _tracker.Current;
    public MatchRecord? LastMatch { get; private set; }
    public StormStatus? Storm => _tracker.Storm;
    public string? PlayerName => _tracker.PlayerName ?? _stats.Data.PlayerName;
    public GameWatcher Watcher => _watcher;

    public event Action<MatchRecord>? MatchStarted;
    public event Action<MatchRecord>? MatchEnded;

    public string PhaseText => Phase switch
    {
        MatchPhase.NotRunning => "Fortnite is not running",
        MatchPhase.Lobby => "In the lobby",
        MatchPhase.Loading => "Loading into match",
        MatchPhase.Warmup => "Warm-up island",
        MatchPhase.Bus => "On the Battle Bus",
        MatchPhase.Playing => "In match",
        MatchPhase.Ending => "Match over",
        _ => "",
    };

    /// <summary>Seconds left in the current storm step, using learned timings.</summary>
    public (string Label, TimeSpan? Remaining, int Index)? StormInfo()
    {
        if (Storm is not { } s || Phase is MatchPhase.Ending or MatchPhase.Lobby) return null;
        var est = _stats.Data.Storm.Estimate(s.Step, s.Index);
        TimeSpan? left = est is { } e ? TimeSpan.FromSeconds(Math.Max(0, e - (DateTime.UtcNow - s.StepStartUtc).TotalSeconds)) : null;
        string label = s.Step switch
        {
            StormStep.Forming => "STORM FORMING",
            StormStep.Holding => "STORM SHRINKS IN",
            StormStep.Shrinking => "STORM CLOSING",
            _ => "STORM",
        };
        return (label, left, s.Index);
    }

    public async Task StartAsync()
    {
        string path = FortnitePaths.CurrentLog;
        long pos = 0;
        DateTime created = default;
        await Task.Run(() =>
        {
            try
            {
                if (!File.Exists(path)) return;
                created = File.GetCreationTimeUtc(path);
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                while (sr.ReadLine() is { } line)
                    if (LogParser.Parse(line) is { } e) _tracker.Process(e);
                pos = fs.Length;
            }
            catch (Exception ex) { Log.Warn("Log catch-up failed: " + ex.Message); }
        });

        _pos = pos;
        _fileCreated = created;

        // The log ends mid-match but the game is closed -> that match is over.
        if (_tracker.Current is not null && !_watcher.IsRunning && !IsProbablyRunning())
            _tracker.EndCurrent(_tracker.LastEventUtc);

        // Restore chests counted before the app restarted during this match.
        if (_tracker.Current is { } cur)
        {
            cur.ChestsTracked = true;
            cur.Source = "live";
            _stats.RestoreLiveMatch(cur);
        }

        _ready = true;
        foreach (var m in _catchUpEnded) MatchEnded?.Invoke(m);
        _catchUpEnded.Clear();
        foreach (var o in _catchUpStorm) _stats.LearnStorm(o);
        _catchUpStorm.Clear();
        if (_tracker.PlayerName is { } name) _stats.SetPlayerName(name);
        _tail.Start();
        Raise();
    }

    static bool IsProbablyRunning()
    {
        var ps = System.Diagnostics.Process.GetProcessesByName("FortniteClient-Win64-Shipping");
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    void OnRunningChanged()
    {
        _tail.Interval = TimeSpan.FromMilliseconds(_watcher.IsRunning ? 400 : 2000);
        if (!_watcher.IsRunning && _tracker.Current is not null)
        {
            Poll();
            _tracker.EndCurrent(DateTime.UtcNow);
        }
        Raise();
    }

    void OnTrackerMatchEnded(MatchRecord m)
    {
        if (!_ready) { _catchUpEnded.Add(m); return; }
        LastMatch = m;
        _stats.ClearLiveMatch();
        MatchEnded?.Invoke(m);
    }

    void OnMatchStarted(MatchRecord m)
    {
        m.ChestsTracked = true;
        m.Source = "live";
        _stats.BeginLiveMatch(m);
        MatchStarted?.Invoke(m);
    }

    void Poll()
    {
        string path = FortnitePaths.CurrentLog;
        try
        {
            if (!File.Exists(path)) return;
            var created = File.GetCreationTimeUtc(path);
            var len = new FileInfo(path).Length;
            if (len < _pos || created != _fileCreated)
            {
                // Fortnite restarted and began a new log file.
                if (_tracker.Current is not null) _tracker.EndCurrent(_tracker.LastEventUtc);
                _pos = 0;
                _fileCreated = created;
            }
            if (len == _pos) return;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Position = _pos;
            int toRead = (int)Math.Min(len - _pos, 8 * 1024 * 1024);
            var buf = new byte[toRead];
            int read = fs.ReadAtLeast(buf, toRead, throwOnEndOfStream: false);
            int lastNl = Array.LastIndexOf(buf, (byte)'\n', Math.Max(0, read - 1));
            if (lastNl < 0) return; // wait for a full line
            var text = Encoding.UTF8.GetString(buf, 0, lastNl + 1);
            _pos += lastNl + 1;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (LogParser.Parse(line) is { } e) _tracker.Process(e);
            }
            if (_tracker.PlayerName is { } name) _stats.SetPlayerName(name);
        }
        catch (IOException) { }
        catch (Exception ex) { Log.Warn("Log tail: " + ex.Message); }
    }

    void Raise()
    {
        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(PhaseText));
        OnPropertyChanged(nameof(InMatch));
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(Storm));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(PlayerName));
    }

    public void Dispose() => _tail.Stop();
}
