namespace FortniteHelper.Core;

/// <summary>A Treasure Hour: a window in which the token odds are boosted.</summary>
public readonly record struct TreasureHour(DateTime StartUtc, DateTime EndUtc, double Boost);

/// <summary>
/// Odds of finding the Pirate Shop Access token (Treasure Hunters mini season).
/// Epic's published rate: 1 in 137,500 per chest (Fortnite Status, Nov 7 2026), boosted during Treasure Hours.
/// Every chest is an independent roll, and when one squad member finds it the whole squad gets access.
/// </summary>
public static class PirateToken
{
    public const int OneIn = 137_500;
    public const double PerChest = 1.0 / OneIn;

    /// <summary>The mini season started on November 1, 2026 and ends on December 5, 2026.</summary>
    public static readonly DateTime SeasonStartUtc = new(2026, 10, 31, 22, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime SeasonEndUtc = new(2026, 12, 5, 23, 59, 0, DateTimeKind.Utc);

    /// <summary>
    /// Saturdays 11 AM-1 PM and 6-8 PM Pacific time (UTC-8 in November): odds x1.5. The Nov 7 evening session was x2.
    /// Later sessions follow the announced pattern; Epic confirms each boost on Fortnite Status shortly before.
    /// </summary>
    public static readonly IReadOnlyList<TreasureHour> TreasureHours = BuildTreasureHours();

    static List<TreasureHour> BuildTreasureHours()
    {
        var list = new List<TreasureHour>();
        DateTime[] saturdays = [new(2026, 11, 7), new(2026, 11, 14), new(2026, 11, 21), new(2026, 11, 28), new(2026, 12, 5)];
        foreach (var d in saturdays)
        {
            var day = DateTime.SpecifyKind(d, DateTimeKind.Utc);
            list.Add(new TreasureHour(day.AddHours(19), day.AddHours(21), 1.5));                    // 11 AM-1 PM PST
            if (d.Month == 12) continue;                                                            // the finale day ends the season
            list.Add(new TreasureHour(day.AddHours(26), day.AddHours(28), d.Day == 7 ? 2.0 : 1.5)); // 6-8 PM PST
        }
        return list;
    }

    public static bool IsActive(DateTime utc) => utc >= SeasonStartUtc && utc <= SeasonEndUtc;

    /// <summary>The odds multiplier at a moment (1 outside Treasure Hours).</summary>
    public static double BoostAt(DateTime utc)
    {
        foreach (var t in TreasureHours)
            if (utc >= t.StartUtc && utc < t.EndUtc) return t.Boost;
        return 1;
    }

    /// <summary>The Treasure Hour running now, or the next one (null when none are left).</summary>
    public static TreasureHour? CurrentOrNextTreasureHour(DateTime utc) =>
        TreasureHours.Where(t => t.EndUtc > utc).OrderBy(t => t.StartUtc).Cast<TreasureHour?>().FirstOrDefault();

    /// <summary>log(chance of NOT finding it) over <paramref name="chests"/> chests opened at a given boost.</summary>
    public static double LogMiss(long chests, double boost = 1) => chests <= 0 ? 0 : chests * Math.Log(1 - PerChest * boost);

    /// <summary>Chance of at least one token, from a sum of <see cref="LogMiss"/> values.</summary>
    public static double ChanceFromLogMiss(double logMiss) => logMiss >= 0 ? 0 : -double.ExpM1(logMiss);

    /// <summary>Chance of at least one token after opening <paramref name="chests"/> chests at the normal rate.</summary>
    public static double Chance(long chests) => ChanceFromLogMiss(LogMiss(chests));

    /// <summary>Chests needed (at the normal rate) to reach a given chance (e.g. 0.5 -> about 95,300).</summary>
    public static long ChestsFor(double chance) => (long)Math.Ceiling(Math.Log(1 - chance) / Math.Log(1 - PerChest));

    /// <summary>"0.218%", "0.0073%", "12.4%".</summary>
    public static string Percent(double p)
    {
        double pct = p * 100;
        if (pct <= 0) return "0%";
        if (pct >= 10) return pct.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";
        // three significant digits
        int decimals = Math.Clamp(2 - (int)Math.Floor(Math.Log10(pct)), 1, 6);
        return pct.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>"1 in 460" style.</summary>
    public static string OneInText(double p) => p <= 0 ? "-" : "1 in " + Math.Round(1 / p).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}
