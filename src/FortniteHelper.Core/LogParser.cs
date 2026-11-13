using System.Globalization;

namespace FortniteHelper.Core;

public abstract record LogEvent(DateTime Time);
public sealed record ServerJoinedEvent(DateTime Time, string Map) : LogEvent(Time);
public sealed record PlaylistEvent(DateTime Time, string Playlist) : LogEvent(Time);
public sealed record MatchStateEvent(DateTime Time, string Previous, string Current) : LogEvent(Time);
public sealed record GamePhaseEvent(DateTime Time, string OldPhase, string NewPhase) : LogEvent(Time);
public sealed record PhaseStepEvent(DateTime Time, string Phase, string Step) : LogEvent(Time);
public sealed record AircraftEvent(DateTime Time, bool Entered) : LogEvent(Time);
public sealed record LobbyLoadedEvent(DateTime Time) : LogEvent(Time);
public sealed record WinEvent(DateTime Time) : LogEvent(Time);
public sealed record NetworkStatsEvent(DateTime Time, double? PingMs, double? LossIn, double? LossOut) : LogEvent(Time);
public sealed record PlayerNameEvent(DateTime Time, string Name) : LogEvent(Time);

/// <summary>
/// Parses FortniteGame.log lines into game events. Fortnite writes log timestamps in UTC:
/// <c>[2026.11.11-21.25.12:839][673]LogNet: Welcomed by server (Level: /Delilah/Maps/Delilah_Terrain?...</c>
/// </summary>
public static class LogParser
{
    public static LogEvent? Parse(string line)
    {
        if (line.Length < 32 || line[0] != '[') return null;

        // Category starts after the second ']'
        int b1 = line.IndexOf(']');
        if (b1 < 0) return null;
        int b2 = line.IndexOf(']', b1 + 1);
        if (b2 < 0 || b2 + 1 >= line.Length) return null;
        var body = line.AsSpan(b2 + 1);

        // Fast reject on category
        if (!(body.StartsWith("LogNet:") || body.StartsWith("LogFort") || body.StartsWith("LogBattleRoyaleGamePhaseLogic:")
              || body.StartsWith("LogLoad:") || body.StartsWith("LogPlayerKills:")))
            return null;

        LogEvent? Make(Func<DateTime, LogEvent?> f) => TryTime(line, out var t) ? f(t) : null;

        if (body.StartsWith("LogNet: Welcomed by server (Level: "))
        {
            var rest = body["LogNet: Welcomed by server (Level: ".Length..];
            int end = rest.IndexOfAny('?', ',', ')');
            var map = (end >= 0 ? rest[..end] : rest).ToString();
            return Make(t => new ServerJoinedEvent(t, map));
        }

        if (body.StartsWith("LogLoad: LoadMap: /Game/Maps/Frontend"))
            return Make(t => new LobbyLoadedEvent(t));

        if (body.StartsWith("LogBattleRoyaleGamePhaseLogic:"))
        {
            var s = body.ToString();
            int i = s.IndexOf("NewPhase = EAthenaGamePhase::", StringComparison.Ordinal);
            if (i >= 0)
            {
                int o = s.IndexOf("OldPhase = EAthenaGamePhase::", StringComparison.Ordinal);
                string oldP = o >= 0 ? Word(s, o + "OldPhase = EAthenaGamePhase::".Length) : "";
                string newP = Word(s, i + "NewPhase = EAthenaGamePhase::".Length);
                return Make(t => new GamePhaseEvent(t, oldP, newP));
            }
            int st = s.IndexOf("PhaseStep = EAthenaGamePhaseStep::", StringComparison.Ordinal);
            if (st >= 0)
            {
                int p = s.IndexOf("Phase = EAthenaGamePhase::", StringComparison.Ordinal);
                string phase = p >= 0 ? Word(s, p + "Phase = EAthenaGamePhase::".Length) : "";
                string step = Word(s, st + "PhaseStep = EAthenaGamePhaseStep::".Length);
                return Make(t => new PhaseStepEvent(t, phase, step));
            }
            return null;
        }

        if (body.StartsWith("LogFort: "))
        {
            var s = body.ToString();
            if (s.StartsWith("LogFort: MatchState changed previous=", StringComparison.Ordinal))
            {
                int pi = "LogFort: MatchState changed previous=".Length;
                string prev = Word(s, pi);
                int ci = s.IndexOf("current=", StringComparison.Ordinal);
                string cur = ci >= 0 ? Word(s, ci + "current=".Length) : "";
                return Make(t => new MatchStateEvent(t, prev, cur));
            }
            if (s.StartsWith("LogFort: PLAYLIST:", StringComparison.Ordinal))
            {
                int pi = s.IndexOf("PlaylistName is ", StringComparison.Ordinal);
                if (pi < 0) return null;
                string pl = Word(s, pi + "PlaylistName is ".Length);
                if (pl.Length == 0 || pl.StartsWith('[')) return null;
                return Make(t => new PlaylistEvent(t, pl));
            }
            if (s.StartsWith("LogFort: EnterAircraft:", StringComparison.Ordinal)) return Make(t => new AircraftEvent(t, true));
            if (s.StartsWith("LogFort: ExitAircraft:", StringComparison.Ordinal)) return Make(t => new AircraftEvent(t, false));
            return null;
        }

        if (body.StartsWith("LogFortPostScreen:"))
        {
            if (body.IndexOf("GetLocalPlayerHasWinningPlacement 1") >= 0) return Make(t => new WinEvent(t));
            return null;
        }

        if (body.StartsWith("LogFortAnalyticsEvent: Display: Core.EndGamePhase_ClientMetrics"))
        {
            var s = body.ToString();
            double? ping = Number(s, "BaselinePing: ");
            double? lin = Number(s, "InPacketsLostPercentage: ");
            double? lout = Number(s, "OutPacketsLostPercentage: ");
            if (ping is null && lin is null && lout is null) return null;
            return Make(t => new NetworkStatsEvent(t, ping, lin, lout));
        }

        if (body.StartsWith("LogPlayerKills: HandlePlayerStateChanged : "))
        {
            var name = body["LogPlayerKills: HandlePlayerStateChanged : ".Length..].Trim().ToString();
            if (name.Length == 0) return null;
            return Make(t => new PlayerNameEvent(t, name));
        }

        return null;
    }

    static string Word(string s, int start)
    {
        int i = start;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
        return s[start..i];
    }

    static double? Number(string s, string key)
    {
        int i = s.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return null;
        i += key.Length;
        int j = i;
        while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '.' || s[j] == '-')) j++;
        return double.TryParse(s.AsSpan(i, j - i), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Parses "[2026.11.11-21.25.12:839]" (UTC).</summary>
    public static bool TryTime(string line, out DateTime utc)
    {
        utc = default;
        if (line.Length < 25 || line[0] != '[' || line[24] != ']') return false;
        var s = line.AsSpan(1, 23); // 2026.11.11-21.25.12:839
        if (!int.TryParse(s[0..4], out int y) || !int.TryParse(s[5..7], out int mo) || !int.TryParse(s[8..10], out int d)
            || !int.TryParse(s[11..13], out int h) || !int.TryParse(s[14..16], out int mi) || !int.TryParse(s[17..19], out int sec)
            || !int.TryParse(s[20..23], out int ms))
            return false;
        try
        {
            utc = new DateTime(y, mo, d, h, mi, sec, ms, DateTimeKind.Utc);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
