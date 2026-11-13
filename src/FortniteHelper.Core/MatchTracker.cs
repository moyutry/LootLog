namespace FortniteHelper.Core;

public enum MatchPhase
{
    NotRunning,
    Lobby,
    Loading,
    Warmup,
    Bus,
    Playing,
    Ending,
}

public enum StormStep
{
    None,
    Forming,
    Holding,
    Shrinking,
}

public sealed record StormStatus(int Index, StormStep Step, DateTime StepStartUtc);

public sealed record StormObservation(string? Playlist, StormStep Step, int Index, double Seconds);

/// <summary>
/// Pure state machine fed by <see cref="LogParser"/> events. Used both live (tailing the current log)
/// and offline (importing old logs into match history).
/// </summary>
public sealed class MatchTracker
{
    bool _started; // MatchState reached InProgress

    public MatchRecord? Current { get; private set; }
    public MatchPhase Phase { get; private set; } = MatchPhase.Lobby;
    public StormStatus? Storm { get; private set; }
    public string? PlayerName { get; private set; }
    public DateTime LastEventUtc { get; private set; }

    public bool InMatch => Current is not null;

    public event Action<MatchRecord>? MatchStarted;
    public event Action<MatchRecord>? MatchEnded;
    public event Action<StormObservation>? StormStepObserved;
    public event Action? Changed;

    public void Process(LogEvent e)
    {
        LastEventUtc = e.Time;
        switch (e)
        {
            case ServerJoinedEvent j:
                if (Current is not null) EndCurrent(j.Time);
                Current = new MatchRecord { StartUtc = j.Time, Map = j.Map, Source = "log" };
                _started = false;
                Storm = null;
                Phase = MatchPhase.Loading;
                MatchStarted?.Invoke(Current);
                break;

            case PlaylistEvent p when Current is not null:
                if (Current.Playlist is null) { Current.Playlist = p.Playlist; break; }
                return;

            case MatchStateEvent ms when Current is not null:
                if (ms.Current == "InProgress")
                {
                    _started = true;
                    if (Phase == MatchPhase.Loading) Phase = MatchPhase.Warmup;
                }
                else if (ms.Current == "WaitingPostMatch")
                {
                    Phase = MatchPhase.Ending;
                }
                break;

            case GamePhaseEvent gp when Current is not null:
                switch (gp.NewPhase)
                {
                    case "Warmup": if (Phase is MatchPhase.Loading) Phase = MatchPhase.Warmup; break;
                    case "Aircraft": if (Phase is MatchPhase.Loading or MatchPhase.Warmup) Phase = MatchPhase.Bus; break;
                    case "EndGame": Phase = MatchPhase.Ending; break;
                }
                break;

            case AircraftEvent a when Current is not null:
                if (a.Entered) Phase = MatchPhase.Bus;
                else
                {
                    Phase = MatchPhase.Playing;
                    Current.DropUtc ??= a.Time;
                }
                break;

            case PhaseStepEvent ps when Current is not null:
                HandleStep(ps);
                break;

            case WinEvent when Current is not null:
                Current.Won = true;
                break;

            case NetworkStatsEvent n when Current is not null:
                if (n.PingMs is { } ping) Current.PingMs = ping;
                if (n.LossIn is { } li) Current.PacketLossIn = li;
                if (n.LossOut is { } lo) Current.PacketLossOut = lo;
                break;

            case LobbyLoadedEvent l:
                if (Current is not null) EndCurrent(l.Time);
                Phase = MatchPhase.Lobby;
                break;

            case PlayerNameEvent pn:
                if (PlayerName == pn.Name) return;
                PlayerName = pn.Name;
                break;

            default:
                return;
        }
        Changed?.Invoke();
    }

    void HandleStep(PhaseStepEvent ps)
    {
        var step = ps.Step switch
        {
            "StormForming" => StormStep.Forming,
            "StormHolding" => StormStep.Holding,
            "StormShrinking" => StormStep.Shrinking,
            _ => StormStep.None,
        };

        if (step == StormStep.None)
        {
            if (ps.Step is "GetReady" or "BusLocked" or "BusFlying" && Phase is MatchPhase.Warmup or MatchPhase.Loading)
                Phase = MatchPhase.Bus;
            return;
        }

        // Close the previous storm step and report its duration (used to learn storm timings).
        if (Storm is { } prev && prev.Step != StormStep.None && Phase != MatchPhase.Ending)
        {
            var secs = (ps.Time - prev.StepStartUtc).TotalSeconds;
            if (secs > 1) StormStepObserved?.Invoke(new StormObservation(Current?.Playlist, prev.Step, prev.Index, secs));
        }

        int index = Storm?.Index ?? 0;
        if (step == StormStep.Holding) index++;
        Storm = new StormStatus(index, step, ps.Time);
        if (Current is not null) Current.StormReached = Math.Max(Current.StormReached, index);
        if (Phase is MatchPhase.Bus or MatchPhase.Warmup && step != StormStep.Forming) Phase = MatchPhase.Playing;
    }

    /// <summary>Ends the current match (lobby reached, game closed, or a new match started).</summary>
    public void EndCurrent(DateTime time)
    {
        var m = Current;
        if (m is null) return;
        Current = null;
        Storm = null;
        Phase = MatchPhase.Lobby;
        m.EndUtc ??= time;
        if (_started) MatchEnded?.Invoke(m);
        _started = false;
        Changed?.Invoke();
    }

    public void SetNotRunning()
    {
        if (Phase == MatchPhase.NotRunning) return;
        Phase = MatchPhase.NotRunning;
        Changed?.Invoke();
    }
}

/// <summary>Storm timings: defaults measured from real matches, refined from what the app observes.</summary>
public sealed class StormTimings
{
    public double FormingSec { get; set; } = 100;
    public List<double> Holding { get; set; } = [90, 90, 70, 70, 70, 70, 60, 60, 45, 30, 30, 30];
    public List<double> Shrinking { get; set; } = [70, 60, 50, 50, 50, 35, 20, 20, 20, 20, 20, 20];

    public double? Estimate(StormStep step, int index) => step switch
    {
        StormStep.Forming => FormingSec,
        StormStep.Holding => At(Holding, index),
        StormStep.Shrinking => At(Shrinking, index),
        _ => null,
    };

    static double? At(List<double> list, int index) =>
        index <= 0 ? null : index <= list.Count ? list[index - 1] : list.Count > 0 ? list[^1] : null;

    public void Learn(StormStep step, int index, double seconds)
    {
        if (seconds < 3 || seconds > 600) return;
        switch (step)
        {
            case StormStep.Forming:
                FormingSec = Blend(FormingSec, seconds);
                break;
            case StormStep.Holding:
                Put(Holding, index, seconds);
                break;
            case StormStep.Shrinking:
                Put(Shrinking, index, seconds);
                break;
        }
    }

    static void Put(List<double> list, int index, double seconds)
    {
        if (index <= 0 || index > 30) return;
        while (list.Count < index) list.Add(list.Count > 0 ? list[^1] : seconds);
        list[index - 1] = Blend(list[index - 1], seconds);
    }

    // Storm timings are fixed per playlist, so new observations replace the old value quickly.
    static double Blend(double old, double observed) => Math.Round(old * 0.25 + observed * 0.75, 1);
}
