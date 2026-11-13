namespace FortniteHelper.Core;

public static class FortnitePaths
{
    public static string SavedDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved");

    public static string LogsDir => Path.Combine(SavedDir, "Logs");
    public static string CurrentLog => Path.Combine(LogsDir, "FortniteGame.log");
    public static string DemosDir => Path.Combine(SavedDir, "Demos");
}

/// <summary>Rebuilds match history from Fortnite's own log files and auto-saved replays.</summary>
public static class HistoryImporter
{
    public static IEnumerable<string> ReadLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        using var sr = new StreamReader(fs);
        while (sr.ReadLine() is { } line) yield return line;
    }

    /// <summary>Parses log files (oldest first) into finished matches.</summary>
    public static List<MatchRecord> ImportLogs(IEnumerable<string> files, Action<StormObservation>? storm = null,
        Action<string>? playerName = null, CancellationToken ct = default)
    {
        var result = new List<MatchRecord>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var tracker = new MatchTracker();
            tracker.MatchEnded += m => result.Add(m);
            if (storm is not null) tracker.StormStepObserved += storm;
            try
            {
                foreach (var line in ReadLinesShared(file))
                {
                    if (LogParser.Parse(line) is { } e) tracker.Process(e);
                }
                // A log that ends mid-match: the game was closed (or is still running).
                if (tracker.Current is not null && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddMinutes(-2))
                    tracker.EndCurrent(tracker.LastEventUtc);
            }
            catch (IOException) { }
            if (tracker.PlayerName is { } pn) playerName?.Invoke(pn);
        }
        return result;
    }

    public static IEnumerable<string> LogFilesOldestFirst() =>
        Directory.Exists(FortnitePaths.LogsDir)
            ? Directory.GetFiles(FortnitePaths.LogsDir, "FortniteGame*.log").OrderBy(File.GetLastWriteTimeUtc)
            : [];

    public static List<ReplayInfo> ScanReplays(string dir, ISet<string>? skipPaths = null, CancellationToken ct = default)
    {
        var list = new List<ReplayInfo>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.GetFiles(dir, "*.replay"))
        {
            ct.ThrowIfCancellationRequested();
            if (skipPaths?.Contains(f) == true) continue;
            try
            {
                var info = ReplayParser.Parse(f);
                if (info.IsLive) continue;
                list.Add(info);
            }
            catch (Exception) { /* locked, partial or unknown format */ }
        }
        return list;
    }

    /// <summary>Finds the replay recorded for a match: it starts a few seconds after joining the server.</summary>
    public static ReplayInfo? FindReplay(MatchRecord m, IEnumerable<ReplayInfo> replays) =>
        replays.Where(r => r.TimestampUtc != default && Math.Abs((r.TimestampUtc - m.StartUtc).TotalSeconds) < 120)
               .OrderBy(r => Math.Abs((r.TimestampUtc - m.StartUtc).TotalSeconds))
               .FirstOrDefault();

    /// <summary>
    /// Merges log matches and replays into <paramref name="existing"/> (deduplicated by start time).
    /// Returns the number of new matches.
    /// </summary>
    public static int Merge(List<MatchRecord> existing, IReadOnlyList<MatchRecord> fromLogs, IReadOnlyList<ReplayInfo> replays)
    {
        int added = 0;
        var usedReplays = new HashSet<string>(existing.Where(m => m.ReplayFile is not null).Select(m => m.ReplayFile!),
            StringComparer.OrdinalIgnoreCase);

        foreach (var m in fromLogs)
        {
            var match = FindExisting(existing, m.StartUtc);
            if (match is null)
            {
                existing.Add(m);
                match = m;
                added++;
            }
            else
            {
                match.Playlist ??= m.Playlist;
                match.DropUtc ??= m.DropUtc;
                match.EndUtc ??= m.EndUtc;
                match.PingMs ??= m.PingMs;
                match.PacketLossIn ??= m.PacketLossIn;
                match.PacketLossOut ??= m.PacketLossOut;
                match.Won |= m.Won;
                match.StormReached = Math.Max(match.StormReached, m.StormReached);
            }

            if (!match.ReplayParsed && FindReplay(match, replays.Where(r => !usedReplays.Contains(r.Path))) is { } rep)
            {
                ReplayParser.Apply(rep, match);
                usedReplays.Add(rep.Path);
            }
        }

        // Replays whose log was already rotated away become stand-alone matches.
        foreach (var rep in replays)
        {
            if (usedReplays.Contains(rep.Path) || rep.Stats is null || rep.LengthMs < 30_000) continue;
            if (FindExisting(existing, rep.TimestampUtc) is { } near)
            {
                if (!near.ReplayParsed) ReplayParser.Apply(rep, near);
                continue;
            }
            var m = new MatchRecord
            {
                StartUtc = rep.TimestampUtc,
                EndUtc = rep.EndUtc,
                Source = "replay",
            };
            ReplayParser.Apply(rep, m);
            existing.Add(m);
            added++;
        }

        existing.Sort((a, b) => b.StartUtc.CompareTo(a.StartUtc));
        return added;
    }

    public static MatchRecord? FindExisting(IEnumerable<MatchRecord> list, DateTime startUtc) =>
        list.FirstOrDefault(x => Math.Abs((x.StartUtc - startUtc).TotalSeconds) < 90);
}
