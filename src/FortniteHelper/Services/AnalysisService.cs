using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using FortniteHelper.Core;
using FortniteHelper.Infrastructure;
using FortniteHelper.Replays;

namespace FortniteHelper.Services;

sealed class AnalysisIndex
{
    /// <summary>Replay file name -> what we did with it.</summary>
    public Dictionary<string, AnalysisEntry> Replays { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Container classes discovered in earlier replays (new chest types every season).</summary>
    public List<string> ContainerClasses { get; set; } = [];
}

sealed class AnalysisEntry
{
    public int Version { get; set; }
    public long FileTicks { get; set; }
    public bool Failed { get; set; }
}

/// <summary>
/// Reads every finished replay in the background and turns it into a <see cref="MatchAnalysis"/>:
/// the exact chest count (searched AND broken), landing, fights, storm and path.
/// Fortnite finishes a replay when you leave the match, so the exact count appears a few seconds after that.
/// </summary>
public sealed class AnalysisService : ObservableObject
{
    const string IndexFile = "analysis\\index.json";
    static readonly JsonSerializerOptions Compact = new(JsonStore.Options) { WriteIndented = false };

    readonly HistoryService _history;
    readonly Func<bool> _inMatch;
    readonly Dispatcher _ui;
    readonly string _dir;
    readonly AnalysisIndex _index;
    readonly object _gate = new();
    readonly List<string> _queue = [];
    readonly AutoResetEvent _wake = new(false);
    readonly DispatcherTimer _debounce;
    readonly Dictionary<string, MatchAnalysis> _recent = new(StringComparer.OrdinalIgnoreCase);
    FileSystemWatcher? _watcher;
    Thread? _thread;
    int _pending;
    string _status = "";
    bool _busy, _collect;

    public AnalysisService(HistoryService history, Func<bool> inMatch, Dispatcher ui)
    {
        _history = history;
        _inMatch = inMatch;
        _ui = ui;
        _dir = JsonStore.PathOf("analysis");
        Directory.CreateDirectory(_dir);
        _index = JsonStore.Load<AnalysisIndex>(IndexFile);
        ContainerTypes.Register(_index.ContainerClasses);
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); ScanFolder(); };
    }

    /// <summary>Raised on the UI thread after a replay was read and applied to its match.</summary>
    public event Action<MatchRecord, MatchAnalysis>? Analyzed;

    public int Pending { get => _pending; private set => Set(ref _pending, value); }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    public void Start()
    {
        _thread = new Thread(Worker) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Replay analysis" };
        _thread.Start();
        try
        {
            Directory.CreateDirectory(FortnitePaths.DemosDir);
            _watcher = new FileSystemWatcher(FortnitePaths.DemosDir, "*.replay")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler h = (_, _) => _ui.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });
            _watcher.Changed += h;
            _watcher.Created += h;
            _watcher.Renamed += (_, _) => _ui.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });
        }
        catch (Exception ex)
        {
            Log.Warn("Replay folder watch: " + ex.Message);
        }
        ScanFolder();
    }

    /// <summary>After a match: Fortnite closes the replay when you leave, which can be long after you died (spectating).
    /// The folder watcher catches it; this is a slow safety net in case a change event is missed.</summary>
    public async void WatchForReplayOf(MatchRecord m)
    {
        for (int i = 0; i < 90 && !m.ChestsVerified; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(i < 6 ? 10 : 30));
            if (!m.ChestsVerified) ScanFolder();
        }
    }

    /// <summary>Queues every replay that has not been read (or was read by an older analyzer).</summary>
    public void ScanFolder()
    {
        Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(FortnitePaths.DemosDir)) return;
                var files = new DirectoryInfo(FortnitePaths.DemosDir).GetFiles("*.replay")
                    .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
                lock (_gate)
                {
                    foreach (var f in files)
                    {
                        if (_queue.Contains(f.FullName, StringComparer.OrdinalIgnoreCase)) continue;
                        if (_index.Replays.TryGetValue(f.Name, out var e) && e.FileTicks == f.LastWriteTimeUtc.Ticks
                            && (e.Failed || e.Version >= ReplayAnalyzer.Version)) continue;
                        _queue.Add(f.FullName);
                    }
                    UpdatePending();
                }
                _wake.Set();
            }
            catch (Exception ex) { Log.Warn("Replay scan: " + ex.Message); }
        });
    }

    void UpdatePending()
    {
        int n = _queue.Count;
        _ui.BeginInvoke(() =>
        {
            Pending = n;
            Status = n == 0 ? "" : n == 1 ? "Reading your last replay..." : $"Reading replays... {n} left";
        });
    }

    void Worker()
    {
        while (true)
        {
            string? path;
            lock (_gate) path = _queue.Count > 0 ? _queue[0] : null;
            if (path is null)
            {
                _ui.BeginInvoke(() => Busy = false);
                if (_collect)
                {
                    // Reading a replay allocates a lot; give it back to Windows once the batch is done.
                    _collect = false;
                    GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                }
                _wake.WaitOne();
                continue;
            }

            // Old replays wait while you play; the one from the match you just left goes right away.
            bool fresh = File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromMinutes(20);
            if (!fresh && _inMatch())
            {
                _wake.WaitOne(TimeSpan.FromSeconds(20));
                continue;
            }

            _ui.BeginInvoke(() => Busy = true);
            try { Process(path); }
            catch (Exception ex) { Log.Warn($"Analysis of {Path.GetFileName(path)}: {ex.Message}"); }
            lock (_gate)
            {
                _queue.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                UpdatePending();
            }
        }
    }

    void Process(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) return;

        // Cheap header read first: a replay that is still being recorded is not readable yet.
        ReplayInfo? info = null;
        try { info = ReplayParser.Parse(path); } catch { }
        if (info is null || info.IsLive)
        {
            // Still recording, or the game crashed mid-match. Try again when the file changes.
            if (info is not null && DateTime.UtcNow - fi.LastWriteTimeUtc > TimeSpan.FromHours(3)) MarkDone(fi, failed: true);
            return;
        }
        if (info.LengthMs < 20_000) { MarkDone(fi, failed: true); return; }

        var cached = LoadFile(fi.Name);
        MatchAnalysis? a = cached is { Version: >= ReplayAnalyzer.Version } ? cached : null;
        if (a is null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _collect = true;
            a = ReplayAnalyzer.Analyze(path);
            if (a is null)
            {
                Log.Warn($"Replay {fi.Name} could not be read.");
                MarkDone(fi, failed: true);
                return;
            }
            Log.Info($"Replay {fi.Name}: {a.Chests} chests ({a.ChestsSearched} searched, {a.ChestsBroken} broken), {a.AmmoBoxes} ammo boxes in {sw.ElapsedMilliseconds} ms.");
            if (a.NewContainerClasses.Count > 0)
            {
                lock (_gate)
                {
                    foreach (var c in a.NewContainerClasses)
                        if (!_index.ContainerClasses.Contains(c)) _index.ContainerClasses.Add(c);
                }
                Log.Info("New container types: " + string.Join(", ", a.NewContainerClasses));
            }
            try { File.WriteAllText(Path.Combine(_dir, Path.ChangeExtension(fi.Name, ".json")), JsonSerializer.Serialize(a, Compact)); }
            catch (Exception ex) { Log.Warn("Saving analysis: " + ex.Message); }
        }
        MarkDone(fi, failed: false);

        var result = a;
        _ui.BeginInvoke(() =>
        {
            lock (_recent) _recent[fi.Name] = result;
            if (_history.ApplyAnalysis(result, info) is { } m) Analyzed?.Invoke(m, result);
        });
    }

    void MarkDone(FileInfo fi, bool failed)
    {
        lock (_gate)
        {
            _index.Replays[fi.Name] = new AnalysisEntry { Version = ReplayAnalyzer.Version, FileTicks = fi.LastWriteTimeUtc.Ticks, Failed = failed };
            try { JsonStore.Save(IndexFile, _index); } catch (Exception ex) { Log.Warn("Saving analysis index: " + ex.Message); }
        }
    }

    MatchAnalysis? LoadFile(string replayName)
    {
        try
        {
            var file = Path.Combine(_dir, Path.ChangeExtension(replayName, ".json"));
            return File.Exists(file) ? JsonSerializer.Deserialize<MatchAnalysis>(File.ReadAllText(file), Compact) : null;
        }
        catch { return null; }
    }

    /// <summary>Applies every stored analysis to the history again (after the history was cleared). Fast: results are cached on disk.</summary>
    public void ReapplyAll()
    {
        lock (_gate)
        {
            foreach (var key in _index.Replays.Where(kv => !kv.Value.Failed).Select(kv => kv.Key).ToList()) _index.Replays.Remove(key);
        }
        ScanFolder();
    }

    /// <summary>The full analysis of a match (path, chests, fights ...), or null if its replay was never read.</summary>
    public MatchAnalysis? For(MatchRecord m)
    {
        if (m.ReplayFile is null) return null;
        var name = Path.GetFileName(m.ReplayFile);
        lock (_recent) if (_recent.TryGetValue(name, out var hit)) return hit;
        var a = LoadFile(name);
        if (a is not null) lock (_recent) { if (_recent.Count > 30) _recent.Clear(); _recent[name] = a; }
        return a;
    }

    /// <summary>Every stored analysis (for the chest map). Slow - call off the UI thread.</summary>
    public IEnumerable<MatchAnalysis> All()
    {
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
        {
            if (f.EndsWith("index.json", StringComparison.OrdinalIgnoreCase)) continue;
            MatchAnalysis? a = null;
            try { a = JsonSerializer.Deserialize<MatchAnalysis>(File.ReadAllText(f), Compact); } catch { }
            if (a is not null) yield return a;
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
