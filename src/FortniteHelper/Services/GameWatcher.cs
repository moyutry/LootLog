using System.Diagnostics;
using System.Windows.Threading;
using FortniteHelper.Infrastructure;

namespace FortniteHelper.Services;

/// <summary>
/// Knows whether Fortnite is running / focused and where its window is.
/// Only compares process IDs (no handle is ever opened to the game process).
/// </summary>
public sealed class GameWatcher : IDisposable
{
    static readonly string[] ProcessNames = ["FortniteClient-Win64-Shipping"];

    readonly Dispatcher _ui;
    readonly Timer _timer;
    HashSet<int> _pids = [];
    DateTime _nextProcessScan;
    volatile bool _running, _focused;
    IntPtr _gameWindow;
    RECT _client, _monitor;

    public bool IsRunning => _running;
    public bool IsFocused => _focused;
    /// <summary>Fortnite's client area in physical screen pixels (valid once it has been focused).</summary>
    public RECT GameClient { get { lock (this) return _client; } }
    /// <summary>The monitor Fortnite is on (or the primary monitor).</summary>
    public RECT GameMonitor { get { lock (this) return _monitor; } }
    public bool HasGameRect { get; private set; }

    public event Action? RunningChanged;
    public event Action? FocusChanged;
    public event Action? GeometryChanged;

    public GameWatcher(Dispatcher ui)
    {
        _ui = ui;
        _monitor = Native.PrimaryMonitor();
        _client = _monitor;
        _timer = new Timer(_ => Tick(), null, 200, 150);
    }

    int _busy;

    void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return; // timer callbacks may overlap
        try
        {
            var now = DateTime.UtcNow;
            if (now >= _nextProcessScan)
            {
                _nextProcessScan = now.AddSeconds(_running ? 3 : 2);
                var pids = new HashSet<int>();
                foreach (var name in ProcessNames)
                    foreach (var p in Process.GetProcessesByName(name)) { pids.Add(p.Id); p.Dispose(); }
                _pids = pids;
                bool running = pids.Count > 0;
                if (running != _running)
                {
                    _running = running;
                    if (!running) { _gameWindow = IntPtr.Zero; }
                    Post(RunningChanged);
                }
            }

            var fg = Native.GetForegroundWindow();
            bool focused = false;
            if (fg != IntPtr.Zero && _pids.Count > 0)
            {
                Native.GetWindowThreadProcessId(fg, out uint pid);
                focused = _pids.Contains((int)pid) && !Native.IsIconic(fg);
            }

            if (focused)
            {
                _gameWindow = fg;
                UpdateGeometry(fg);
            }

            if (focused != _focused)
            {
                _focused = focused;
                Post(FocusChanged);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("GameWatcher: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    void UpdateGeometry(IntPtr hwnd)
    {
        if (!Native.GetClientRect(hwnd, out var rc) || rc.Width < 100 || rc.Height < 100) return;
        var pt = new POINT();
        if (!Native.ClientToScreen(hwnd, ref pt)) return;
        var client = new RECT(pt.X, pt.Y, pt.X + rc.Width, pt.Y + rc.Height);
        var monitor = Native.MonitorRect(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST));
        bool changed;
        lock (this)
        {
            changed = !client.Equals(_client) || !monitor.Equals(_monitor) || !HasGameRect;
            _client = client;
            _monitor = monitor;
            HasGameRect = true;
        }
        if (changed) Post(GeometryChanged);
    }

    void Post(Action? a)
    {
        if (a is null) return;
        _ui.BeginInvoke(a);
    }

    public void Dispose() => _timer.Dispose();
}
