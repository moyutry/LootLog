using System.Diagnostics;
using System.Windows.Threading;
using FortniteHelper.Core;
using FortniteHelper.Infrastructure;
using FortniteHelper.Replays;

namespace FortniteHelper.Services;

public sealed record DetectionReport(DateTime Time, string Text, string Outcome, long Milliseconds);

public sealed class PendingLook
{
    public DateTime Utc { get; set; }
    public float[] F { get; set; } = [];
}

public sealed class DetectorData
{
    /// <summary>Bumped when the colour features change, so old examples are dropped.</summary>
    public int Version { get; set; }
    /// <summary>Examples labelled by replays (what the "Search" really opened).</summary>
    public List<LookSample> Samples { get; set; } = [];
    /// <summary>Searches from recent matches, waiting for their replay to label them.</summary>
    public List<PendingLook> Pending { get; set; } = [];
}

/// <summary>
/// The live chest counter (an estimate while you play - the replay gives the exact number after the match).
/// 1. While you're in a match, the area around the crosshair is grabbed a few times a second (no OCR, very cheap),
///    so when you press Interact we still have the "Search" prompt from just before the press - even if the chest
///    opens instantly.
/// 2. Windows OCR reads that frame. Fortnite says "Search" for chests AND ammo boxes, so the colours right at the
///    crosshair decide: chests are gold and glow. That colour model learns from your replays.
/// 3. When the prompt disappears the container opened, and it's counted.
/// Broken chests show no prompt; the replay counts those. Nothing is read from or sent to the game process.
/// </summary>
public sealed class ChestDetector : ObservableObject
{
    const string FileName = "detector.json";
    const int DataVersion = 2;
    const int MaxSamples = 400;
    /// <summary>Holding to search can take a while - wait at least this long for the chest to open.</summary>
    const int MinConfirmMs = 3500;
    /// <summary>The colour patch around the crosshair (fraction of the game screen).</summary>
    const double PatchW = 0.20, PatchH = 0.26;

    readonly SettingsService _settings;
    readonly GameSession _session;
    readonly StatsService _stats;
    readonly ScreenCapture _capture;
    readonly OcrService _ocr;
    readonly Dispatcher _ui;
    readonly ChestClassifier _classifier = new(["chest", "chests"], ["ammo box", "ammo"], ["search"]);
    readonly DetectorData _data;
    LookModel _model;
    int _busy;
    DateTime _lastCount = DateTime.MinValue;
    DetectionReport? _last;

    // Pre-press frames: a small ring of recent captures whose pixel buffers are reused.
    readonly object _ringLock = new();
    readonly CapturedFrame?[] _ring = new CapturedFrame?[3];
    readonly DateTime[] _ringAt = new DateTime[3];
    int _ringNext;

    public ChestDetector(SettingsService settings, GameSession session, StatsService stats, ScreenCapture capture,
        OcrService ocr, RawInputService input, Dispatcher ui)
    {
        _settings = settings;
        _session = session;
        _stats = stats;
        _capture = capture;
        _ocr = ocr;
        _ui = ui;
        _data = JsonStore.Load<DetectorData>(FileName);
        if (_data.Version != DataVersion)
        {
            _data.Samples.Clear();
            _data.Pending.Clear();
            _data.Version = DataVersion;
        }
        _data.Pending.RemoveAll(p => p.Utc < DateTime.UtcNow.AddDays(-3));
        _model = new LookModel(_data.Samples);
        input.Input += OnInput;
        if (!AppServices.Headless)
            new Thread(WatchLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Chest prompt watch" }.Start();
    }

    DetectionSettings Cfg => _settings.Settings.Detection;

    public DetectionReport? Last { get => _last; private set => Set(ref _last, value); }
    public bool OcrAvailable => _ocr.IsAvailable;
    /// <summary>Chests / other containers the colour model has learned from.</summary>
    public int LearnedChests => _model.Chests;
    public int LearnedOthers => _model.Others;
    public bool Trained => _model.Trained;

    bool ShouldWatch() =>
        Cfg.Mode == DetectionMode.Auto && _session.Watcher.IsFocused && _session.InMatch
        && _session.Phase == MatchPhase.Playing && _ocr.IsAvailable;

    /// <summary>Keeps the last few frames around the crosshair while you play (about 7 per second, no OCR).</summary>
    void WatchLoop()
    {
        while (true)
        {
            try
            {
                if (!ShouldWatch())
                {
                    lock (_ringLock) Array.Clear(_ring);
                    Thread.Sleep(400);
                    continue;
                }
                int slot;
                byte[]? reuse;
                lock (_ringLock)
                {
                    slot = _ringNext;
                    reuse = _ring[slot]?.Pixels;
                    _ring[slot] = null; // being written
                }
                var frame = _capture.Capture(Region(), Cfg.Capture, reuse);
                lock (_ringLock)
                {
                    _ring[slot] = frame;
                    _ringAt[slot] = DateTime.UtcNow;
                    _ringNext = (slot + 1) % _ring.Length;
                }
            }
            catch (Exception ex) { Log.Warn("Prompt watch: " + ex.Message); Thread.Sleep(1000); }
            Thread.Sleep(140);
        }
    }

    /// <summary>A copy of the newest frame grabbed before <paramref name="before"/> (at most 600 ms old).</summary>
    CapturedFrame? FrameBefore(DateTime before)
    {
        lock (_ringLock)
        {
            int best = -1;
            for (int i = 0; i < _ring.Length; i++)
            {
                if (_ring[i] is null || _ringAt[i] > before || before - _ringAt[i] > TimeSpan.FromMilliseconds(600)) continue;
                if (best < 0 || _ringAt[i] > _ringAt[best]) best = i;
            }
            if (best < 0) return null;
            var f = _ring[best]!;
            return new CapturedFrame((byte[])f.Pixels.Clone(), f.Width, f.Height);
        }
    }

    void OnInput(InputEvent e)
    {
        if (!e.Down || Cfg.Mode != DetectionMode.Auto) return;
        if (!Cfg.InteractKey.Matches(e.Code, e.IsMouse, e.Ctrl, e.Shift, e.Alt)) return;
        if (!_session.Watcher.IsFocused || !_session.InMatch) return;
        if (!_ocr.IsAvailable) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        var pressed = DateTime.UtcNow;
        var before = FrameBefore(pressed);
        _ = Task.Run(async () =>
        {
            try { await DetectAsync(pressed, before); }
            catch (Exception ex) { Log.Warn("Detection: " + ex.Message); }
            finally { Volatile.Write(ref _busy, 0); }
        });
    }

    /// <summary>The capture region around the crosshair, in physical screen pixels.</summary>
    public RECT Region()
    {
        var w = _session.Watcher;
        var area = w.HasGameRect ? w.GameClient : w.GameMonitor;
        int rw = (int)(area.Width * Cfg.RegionWidth), rh = (int)(area.Height * Cfg.RegionHeight);
        int cx = area.Left + area.Width / 2;
        int cy = area.Top + area.Height / 2 + (int)(area.Height * Cfg.RegionOffsetY);
        int l = Math.Max(area.Left, cx - rw / 2), t = Math.Max(area.Top, cy - rh / 2);
        return new RECT(l, t, Math.Min(area.Right, l + rw), Math.Min(area.Bottom, t + rh));
    }

    /// <summary>Colour features of the patch right at the crosshair (the container you're looking at).</summary>
    float[] LookAt(CapturedFrame f)
    {
        double rw = Cfg.RegionWidth, rh = Cfg.RegionHeight;
        double cx = 0.5, cy = 0.5 - Cfg.RegionOffsetY / rh; // the crosshair inside the capture
        double hw = Math.Min(0.5, PatchW / rw / 2), hh = Math.Min(0.5, PatchH / rh / 2);
        return ContainerLook.Features(f.Pixels, f.Width, f.Height, cx - hw, cy - hh, cx + hw, cy + hh);
    }

    async Task<(ContainerKind? Kind, string Text)> ReadAsync(CapturedFrame frame, bool enhanced)
    {
        var input = enhanced ? OcrService.HighContrast(frame) : frame;
        var text = await _ocr.RecognizeAsync(input);
        return (_classifier.Classify(text), text);
    }

    async Task<(ContainerKind? Kind, string Text, CapturedFrame? Frame)> ScanAsync(RECT region, bool enhanced)
    {
        var frame = _capture.Capture(region, Cfg.Capture);
        if (frame is null) return (null, "", null);
        var (kind, text) = await ReadAsync(frame, enhanced);
        return (kind, text, frame);
    }

    async Task DetectAsync(DateTime pressed, CapturedFrame? before)
    {
        var sw = Stopwatch.StartNew();
        var region = Region();
        ContainerKind? kind = null;
        string text = "";
        CapturedFrame? frame = null;
        bool enhanced = false;

        // 1) The frame from just before the press: the prompt is surely on screen there.
        if (before is not null)
        {
            (kind, text) = await ReadAsync(before, false);
            if (kind is null) { var e2 = await ReadAsync(before, true); if (e2.Kind is not null) { (kind, text) = e2; enhanced = true; } }
            if (kind is not null) frame = before;
        }
        // 2) Otherwise the screen right now.
        if (kind is null)
        {
            var now = await ScanAsync(region, false);
            (kind, text, frame) = (now.Kind, now.Text.Length > 0 ? now.Text : text, now.Frame ?? before);
            if (kind is null && now.Frame is not null)
            {
                var e3 = await ScanAsync(region, true);
                if (e3.Kind is not null) { (kind, text) = (e3.Kind, e3.Text); enhanced = true; }
            }
        }
        if (kind is null || frame is null)
        {
            string why = frame is null ? "capture failed" : frame.IsBlank ? "screen capture is black (exclusive fullscreen?)" : "no Search prompt";
            Report(text, why, sw);
            return;
        }

        // What it is: a named prompt wins, otherwise the colours at the crosshair decide.
        var features = LookAt(frame);
        bool chest = kind switch
        {
            ContainerKind.Chest => true,
            ContainerKind.AmmoBox => false,
            _ => _model.IsChest(features),
        };

        // Wait until the prompt disappears (= it opened). Two misses in a row, to ignore one bad OCR read.
        int misses = 0;
        int window = Math.Max(MinConfirmMs, Cfg.ConfirmWindowMs);
        while (sw.ElapsedMilliseconds < window)
        {
            await Task.Delay(130);
            if (!_session.Watcher.IsFocused) { Report(text, "cancelled (left the game window)", sw); return; }
            var (k, _, f) = await ScanAsync(region, enhanced);
            if (f is null) continue;
            misses = k is not null ? 0 : misses + 1;
            if (misses >= 2)
            {
                lock (_data) _data.Pending.Add(new PendingLook { Utc = pressed, F = features });
                string what = chest ? "chest" : kind == ContainerKind.AmmoBox ? "ammo box" : "not a chest";
                Report(text, Count(chest ? ContainerKind.Chest : ContainerKind.AmmoBox) ? $"opened - {what} (gold {features[ContainerLook.GoldIndex]:0.000})" : "ignored (cooldown)", sw);
                QueueSave();
                return;
            }
        }
        Report(text, "not opened (prompt stayed)", sw);
    }

    bool Count(ContainerKind kind)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastCount).TotalMilliseconds < Math.Min(Cfg.CooldownMs, 800)) return false;
        _lastCount = now;
        _ui.BeginInvoke(() => _stats.Add(kind, +1, auto: true));
        return true;
    }

    /// <summary>
    /// A replay was read: it says exactly what each of your searches opened. Those become training examples,
    /// so the next matches are counted better.
    /// </summary>
    public void Learn(MatchAnalysis a)
    {
        List<PendingLook> pending;
        lock (_data)
        {
            var start = a.RecordedUtc.AddSeconds(-10);
            var end = a.EndUtc.AddSeconds(30);
            pending = _data.Pending.Where(p => p.Utc >= start && p.Utc <= end).OrderBy(p => p.Utc).ToList();
            if (pending.Count == 0) return;
            _data.Pending.RemoveAll(pending.Contains);
        }
        var opened = a.Opened.Select(o => (When: a.WallClock(o.T), o.Type)).OrderBy(o => o.When).ToList();
        if (opened.Count == 0) return;

        // The two clocks can differ by a second or two: find the typical gap first, then pair tightly around it.
        var gaps = new List<double>();
        foreach (var p in pending)
        {
            var near = opened.OrderBy(o => Math.Abs((o.When - p.Utc).TotalSeconds)).First();
            double gap = (near.When - p.Utc).TotalSeconds;
            if (Math.Abs(gap) < 10) gaps.Add(gap);
        }
        if (gaps.Count == 0) return;
        gaps.Sort();
        double offset = gaps[gaps.Count / 2];

        var used = new HashSet<int>();
        int added = 0;
        foreach (var p in pending)
        {
            int best = -1;
            double bestErr = 2.5;
            for (int i = 0; i < opened.Count; i++)
            {
                if (used.Contains(i)) continue;
                double err = Math.Abs((opened[i].When - p.Utc).TotalSeconds - offset);
                if (err < bestErr) { bestErr = err; best = i; }
            }
            if (best < 0) continue;
            used.Add(best);
            lock (_data) _data.Samples.Add(new LookSample { F = p.F, Chest = opened[best].Type == ContainerType.Chest });
            added++;
        }
        if (added == 0) return;
        lock (_data)
        {
            // Keep the newest examples, but never let one kind crowd out the other.
            while (_data.Samples.Count > MaxSamples)
            {
                bool dropChest = _data.Samples.Count(s => s.Chest) > _data.Samples.Count / 2;
                int idx = _data.Samples.FindIndex(s => s.Chest == dropChest);
                _data.Samples.RemoveAt(idx < 0 ? 0 : idx);
            }
            _model = new LookModel(_data.Samples);
        }
        Log.Info($"Chest look model: +{added} examples from {System.IO.Path.GetFileName(a.ReplayFile)} ({_model.Chests} chests / {_model.Others} other).");
        _ui.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(LearnedChests));
            OnPropertyChanged(nameof(LearnedOthers));
            OnPropertyChanged(nameof(Trained));
        });
        QueueSave();
    }

    int _saveQueued;
    void QueueSave()
    {
        if (Interlocked.Exchange(ref _saveQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            Volatile.Write(ref _saveQueued, 0);
            Save();
        });
    }

    public void Save()
    {
        try { lock (_data) JsonStore.Save(FileName, _data); }
        catch (Exception ex) { Log.Warn("Saving detector data: " + ex.Message); }
    }

    void Report(string text, string outcome, Stopwatch sw)
    {
        long ms = sw.ElapsedMilliseconds;
        var shown = string.IsNullOrWhiteSpace(text) ? "(no text)" : text.Trim().ReplaceLineEndings(" ");
        // One line per search, so misses can be understood later (local log only).
        Log.Info($"Live search: {outcome} · \"{(shown.Length > 60 ? shown[..60] : shown)}\" · {ms} ms");
        _ui.BeginInvoke(() => Last = new DetectionReport(DateTime.Now, shown, outcome, ms));
    }
}
