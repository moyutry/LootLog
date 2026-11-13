using System.Globalization;
using FortniteHelper.Core;
using FortniteHelper.Replays;

namespace FortniteHelper.Services;

public enum TipKind { Good, Improve, Info }

public sealed record Tip(TipKind Kind, string Title, string Text);

/// <summary>Your normal numbers, to compare one match against.</summary>
public sealed class Baseline
{
    public int Matches { get; init; }
    public double? Chests { get; init; }
    public double? FirstChestSec { get; init; }
    public double? AliveSec { get; init; }
    public double? StormSec { get; init; }
    public double? Accuracy { get; init; }
    public double? Damage { get; init; }
    public double? ChestsPerMinute { get; init; }
    public double? EarlyDeathRate { get; init; }
    public double? HotDropRate { get; init; }
}

/// <summary>Turns replay analyses into plain advice: what went well, what to change, how to open more chests.</summary>
public static class Coach
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static string N1(double v) => v.ToString("0.#", Inv);
    /// <summary>" with a shotgun", " with an SMG", "".</summary>
    public static string With(string? weapon) => weapon switch
    {
        null => "",
        "SMG" or "DMR" => $" with a{(weapon == "SMG" ? "n" : "")} {weapon}",
        "Melee" => " in melee",
        "Vehicle" => " with a vehicle",
        _ => $" with a{("aeiou".Contains(char.ToLowerInvariant(weapon[0])) ? "n" : "")} {weapon.ToLowerInvariant()}",
    };

    static string Time(double sec) => sec >= 60 ? $"{(int)(sec / 60)}:{(int)(sec % 60):00}" : $"{sec:0}s";

    /// <summary>Averages over your last 30 matches with an exact (replay) count.</summary>
    public static Baseline Baseline(IEnumerable<MatchRecord> matches, MatchRecord? exclude = null)
    {
        var list = matches.Where(m => m.ChestsVerified && m.AnalysisVersion > 0 && !ReferenceEquals(m, exclude)).Take(30).ToList();
        double? Avg(Func<MatchRecord, double?> f)
        {
            var v = list.Select(f).Where(x => x is not null).Select(x => x!.Value).ToList();
            return v.Count > 0 ? v.Average() : null;
        }
        var alive = list.Where(m => m.SecondsAlive is > 0).ToList();
        return new Baseline
        {
            Matches = list.Count,
            Chests = Avg(m => m.Chests),
            FirstChestSec = Avg(m => m.FirstChestSec),
            AliveSec = Avg(m => m.SecondsAlive),
            StormSec = Avg(m => m.SecondsInStorm),
            Accuracy = Avg(m => m.Accuracy),
            Damage = Avg(m => m.DamageDealt),
            ChestsPerMinute = alive.Count > 0 ? alive.Sum(m => m.Chests) / alive.Sum(m => m.SecondsAlive!.Value / 60) : null,
            EarlyDeathRate = alive.Count > 0 ? alive.Count(m => m.SecondsAlive < 120 && !m.IsVictory) / (double)alive.Count : null,
            HotDropRate = list.Count(m => m.EnemiesNearLanding is not null) is int n and > 0
                ? list.Count(m => m.EnemiesNearLanding >= 4) / (double)n : null,
        };
    }

    /// <summary>Advice for one match, most useful first.</summary>
    public static List<Tip> ForMatch(MatchRecord m, MatchAnalysis? a, Baseline b)
    {
        var tips = new List<Tip>();
        if (!m.ChestsVerified) return tips;
        double alive = m.SecondsAlive ?? 0;
        bool won = m.IsVictory;

        // ---- Chests ----
        if (b.Chests is { } avg && b.Matches >= 3)
        {
            if (m.Chests >= avg * 1.25 && m.Chests >= avg + 2)
                tips.Add(new(TipKind.Good, "Great looting", $"{m.Chests} chests - your average is {N1(avg)}."));
            else if (m.Chests <= avg * 0.7 && alive > 300)
                tips.Add(new(TipKind.Improve, "Few chests for a long match",
                    $"{m.Chests} chests in {Time(alive)} alive (your average is {N1(avg)}). Between fights, loot buildings room by room: chests sit in attics, basements and behind walls - listen for the chest hum."));
        }
        if (alive >= 120 && m.Chests > 0 && b.ChestsPerMinute is { } cpmBase)
        {
            double perMin = m.Chests / (alive / 60);
            if (perMin >= cpmBase * 1.3)
                tips.Add(new(TipKind.Good, "Fast looting", $"{N1(perMin)} chests per minute alive (your normal: {N1(cpmBase)})."));
        }
        if (m.ChestsBroken > 0)
            tips.Add(new(TipKind.Info, $"{m.ChestsBroken} chest{(m.ChestsBroken == 1 ? "" : "s")} broken open",
                $"Breaking a chest (pickaxe or explosives) opens it too, and it is counted. {m.ChestsSearched} were searched normally."));

        // ---- Landing ----
        if (m.FirstChestSec is { } fc)
        {
            if (fc > 30)
                tips.Add(new(TipKind.Improve, "Slow first chest",
                    $"Your first chest came {Time(fc)} after landing{(b.FirstChestSec is { } bf ? $" (usually {Time(bf)})" : "")}. Aim your glider at a chest you know - the Chest Map shows where they are."));
            else if (fc <= 10)
                tips.Add(new(TipKind.Good, "Landed on loot", $"First chest {Time(fc)} after touching down."));
        }
        if (m.EnemiesNearLanding is { } hot && hot >= 4)
        {
            if (!won && alive < 180)
                tips.Add(new(TipKind.Improve, "Hot drop",
                    $"{hot} enemies landed within 150 m of you and you were out after {Time(alive)}. For more chests (and Pirate token rolls), land a bit off the busy spots."));
            else
                tips.Add(new(TipKind.Good, "Survived a hot drop", $"{hot} enemies landed near you and you made it out."));
        }

        // ---- Storm ----
        if (m.SecondsInStorm is { } storm && storm >= 15)
        {
            string circle = "";
            if (a?.Storm.Count > 0 && a.DeathT is { } dt)
            {
                var closing = a.Storm.LastOrDefault(z => z.ShrinkStart <= dt);
                if (closing is not null) circle = $" Storm {closing.Phase} started closing {Time(Math.Max(0, closing.ShrinkStart - (a.LandT ?? 0)))} after you landed.";
            }
            tips.Add(new(TipKind.Improve, "Too long in the storm",
                $"{Time(storm)} in the storm{(b.StormSec is { } bs && b.Matches >= 3 ? $" (usually {Time(bs)})" : "")}. Start rotating when the next circle appears instead of when the wall reaches you.{circle}"));
        }

        // ---- Death ----
        if (!won && (m.KilledBy is not null || m.DeathWeapon is not null))
        {
            string who = m.KilledBy is null ? "an enemy" : m.KilledByBot ? $"{m.KilledBy} (bot)" : m.KilledBy;
            string with = With(m.DeathWeapon);
            if (m.DeathWeapon == "Storm")
                tips.Add(new(TipKind.Improve, "Eliminated by the storm", "Carry heals and keep an eye on the storm timer - leave earlier when you're far from the circle."));
            else if (m.DeathWeapon == "Fall damage")
                tips.Add(new(TipKind.Improve, "Fall damage", "You were eliminated by a fall. Check the height before jumping off builds or cliffs."));
            else if (m.KilledFromM is { } d && d >= 70)
                tips.Add(new(TipKind.Improve, "Taken out from range",
                    $"{who} got you{with} from {d:0} m. Avoid crossing open ground - move between cover, and break line of sight when you get hit."));
            else if (m.KilledFromM is { } c && c <= 8)
                tips.Add(new(TipKind.Info, "Lost a close fight",
                    $"{who} won a close-range fight{with} ({c:0} m). Up close, a ready shotgun and heals before pushing win most fights."));
            else if (m.KilledBy is not null)
                tips.Add(new(TipKind.Info, "Eliminated", $"By {who}{with}{(m.KilledFromM is { } dd ? $" from {dd:0} m" : "")}, after {Time(alive)} alive."));
        }
        if (won) tips.Insert(0, new(TipKind.Good, "Victory Royale!", $"{m.Kills ?? 0} eliminations and {m.Chests} chests."));

        // ---- Aim ----
        if (m.Accuracy is { } acc && b.Accuracy is { } ba && b.Matches >= 5 && (m.DamageDealt ?? 0) > 100)
        {
            if (acc < ba * 0.7)
                tips.Add(new(TipKind.Improve, "Shots missed", $"Accuracy {acc * 100:0}% (usually {ba * 100:0}%). Fewer shots from far away, wait for the crosshair to settle (crouch, don't jump-shoot)."));
            else if (acc > ba * 1.3)
                tips.Add(new(TipKind.Good, "Sharp aim", $"Accuracy {acc * 100:0}% (usually {ba * 100:0}%)."));
        }

        // ---- Squad / Pirate token ----
        if (m.IsTeamMatch && m.TeamChests > m.Chests)
            tips.Add(new(TipKind.Info, $"Your team opened {m.TeamChests} chests",
                $"You opened {m.Chests} of them ({m.Chests * 100 / Math.Max(1, m.TeamChests)}%). A Pirate token found by anyone in the team unlocks the shop for everyone, so every team chest counts."));

        return tips;
    }

    /// <summary>Patterns across many matches: what to change to open more chests and live longer.</summary>
    public static List<Tip> Career(IReadOnlyList<MatchRecord> matches, IReadOnlyList<PlaceStats> places)
    {
        var tips = new List<Tip>();
        var verified = matches.Where(m => m.ChestsVerified && m.AnalysisVersion > 0).ToList();
        if (verified.Count < 3) return tips;
        var b = Baseline(verified);

        // Best landing spot for chests
        var good = places.Where(p => p.Landings >= 2 && p.AvgChests is not null).OrderByDescending(p => p.AvgChests).ToList();
        if (good.Count >= 2 && good[0].AvgChests > (b.Chests ?? 0) + 1)
        {
            var top = good[0];
            tips.Add(new(TipKind.Good, $"Your best drop: {top.Name}",
                $"{N1(top.AvgChests!.Value)} chests per match there ({top.Landings} landings) vs {N1(b.Chests ?? 0)} on average. Land there more to raise your CPM."));
            var worst = good[^1];
            if (worst.AvgChests < (b.Chests ?? 0) - 2 && worst.Name != top.Name)
                tips.Add(new(TipKind.Improve, $"Weak drop: {worst.Name}", $"Only {N1(worst.AvgChests!.Value)} chests per match there ({worst.Landings} landings)."));
        }

        if (b.EarlyDeathRate is { } early && early >= 0.25 && verified.Count >= 5)
            tips.Add(new(TipKind.Improve, "Early eliminations",
                $"In {early * 100:0}% of your matches you were out within 2 minutes of landing. Every extra minute alive is about {N1(b.ChestsPerMinute ?? 1)} more chests."));

        if (b.HotDropRate is { } hotRate && hotRate >= 0.4)
            tips.Add(new(TipKind.Info, "You like hot drops", $"{hotRate * 100:0}% of your landings had 4+ enemies within 150 m. Quieter spots give more chests per match."));

        if (b.FirstChestSec is { } fc && fc > 25)
            tips.Add(new(TipKind.Improve, "Land on a chest", $"On average your first chest comes {Time(fc)} after landing. Pick a chest before you jump and glide straight to it."));

        if (b.StormSec is { } st && st > 20)
            tips.Add(new(TipKind.Improve, "Storm time", $"You spend about {Time(st)} per match in the storm. Rotate as soon as the next circle shows."));

        // Trend
        if (verified.Count >= 12)
        {
            double recent = verified.Take(6).Average(m => m.Chests);
            double before = verified.Skip(6).Take(20).Average(m => m.Chests);
            if (recent >= before + 1.5) tips.Add(new(TipKind.Good, "Chests trending up", $"{N1(recent)} per match in your last 6 vs {N1(before)} before."));
            else if (recent <= before - 1.5) tips.Add(new(TipKind.Improve, "Chests trending down", $"{N1(recent)} per match in your last 6 vs {N1(before)} before."));
        }

        // Deaths by range
        var deaths = verified.Where(m => m.KilledFromM is not null && !m.IsVictory).Take(30).ToList();
        if (deaths.Count >= 6)
        {
            double far = deaths.Count(m => m.KilledFromM >= 70) / (double)deaths.Count;
            double close = deaths.Count(m => m.KilledFromM <= 8) / (double)deaths.Count;
            if (far >= 0.35)
                tips.Add(new(TipKind.Improve, "Long-range deaths", $"{far * 100:0}% of your eliminations came from 70 m or more. Rotate behind cover and avoid open fields."));
            else if (close >= 0.5)
                tips.Add(new(TipKind.Info, "Close-range fights", $"{close * 100:0}% of your eliminations were close-range fights. Heal up and reload before pushing."));
        }
        return tips;
    }
}
