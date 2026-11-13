using FortniteHelper.Core;
using Xunit;

namespace FortniteHelper.Tests;

public class PirateTokenTests
{
    [Fact]
    public void OneChestIsOneIn137500()
    {
        Assert.Equal(1.0 / 137_500, PirateToken.Chance(1), 12);
        Assert.Equal(0, PirateToken.Chance(0));
    }

    [Fact]
    public void ManyChestsCompound()
    {
        // 1 - (1 - 1/137500)^1000
        Assert.Equal(1 - Math.Pow(1 - 1.0 / 137_500, 1000), PirateToken.Chance(1000), 10);
        // Half the time you'd have it after ~95,300 chests.
        Assert.InRange(PirateToken.ChestsFor(0.5), 95_250, 95_350);
        Assert.InRange(PirateToken.Chance(PirateToken.ChestsFor(0.01)), 0.01, 0.01001);
    }

    [Theory]
    [InlineData(0.00727, "0.727%")]
    [InlineData(0.0000727, "0.00727%")]
    [InlineData(0.1234, "12.3%")]
    [InlineData(0, "0%")]
    public void FormatsPercent(double p, string expected) => Assert.Equal(expected, PirateToken.Percent(p));

    [Fact]
    public void TreasureHoursBoostTheOdds()
    {
        static DateTime U(int month, int day, int hour, int min = 0) => new(2026, month, day, hour, min, 0, DateTimeKind.Utc);
        Assert.Equal(1.5, PirateToken.BoostAt(U(11, 14, 19, 30)));   // Sat 11:30 AM PST
        Assert.Equal(1.5, PirateToken.BoostAt(U(11, 15, 3, 0)));     // Sat 7 PM PST
        Assert.Equal(2.0, PirateToken.BoostAt(U(11, 8, 2, 30)));     // the Nov 7 evening session
        Assert.Equal(1.0, PirateToken.BoostAt(U(11, 13, 12, 0)));    // a normal Friday
        Assert.Equal(1.0, PirateToken.BoostAt(U(11, 14, 21, 0)));    // just after the first session

        Assert.Equal(U(11, 14, 19), PirateToken.CurrentOrNextTreasureHour(U(11, 13, 12))!.Value.StartUtc);
        Assert.Equal(U(11, 14, 19), PirateToken.CurrentOrNextTreasureHour(U(11, 14, 20))!.Value.StartUtc); // running now
        Assert.Null(PirateToken.CurrentOrNextTreasureHour(U(12, 10, 0)));

        // 100 boosted chests are worth about 150 normal ones.
        double boosted = PirateToken.ChanceFromLogMiss(PirateToken.LogMiss(100, 1.5));
        Assert.Equal(PirateToken.Chance(150), boosted, 8);
        Assert.Equal(PirateToken.Chance(1234), PirateToken.ChanceFromLogMiss(PirateToken.LogMiss(1234)), 12);
    }

    [Fact]
    public void SeasonWindow()
    {
        Assert.True(PirateToken.IsActive(new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc)));
        Assert.False(PirateToken.IsActive(new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc)));
        Assert.False(PirateToken.IsActive(new DateTime(2026, 12, 10, 12, 0, 0, DateTimeKind.Utc)));
    }
}

public class SearchPromptTests
{
    static readonly ChestClassifier C = new(["chest", "chests"], ["ammo box", "ammo"], ["search"]);

    [Theory]
    [InlineData("E Search", ContainerKind.Container)]
    [InlineData("[E] SEARCH", ContainerKind.Container)]
    [InlineData("Hold E Search Chest", ContainerKind.Chest)]
    [InlineData("Search Ammo Box", ContainerKind.AmmoBox)]
    [InlineData("Seareh", ContainerKind.Container)] // one OCR slip on a 6-letter word
    public void ReadsPrompts(string text, ContainerKind expected) => Assert.Equal(expected, C.Classify(text));

    [Theory]
    [InlineData("Open")]
    [InlineData("Pick up Shotgun")]
    [InlineData("")]
    public void IgnoresOtherPrompts(string text) => Assert.Null(C.Classify(text));
}

public class ContainerLookTests
{
    /// <summary>A flat image of one colour, BGRA.</summary>
    static byte[] Image(byte r, byte g, byte b, int w = 60, int h = 40)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
        return px;
    }

    /// <summary>Mostly <paramref name="bg"/>, with a block of <paramref name="fg"/> in the middle.</summary>
    static byte[] Scene((byte R, byte G, byte B) bg, (byte R, byte G, byte B) fg, double share, int w = 80, int h = 60)
    {
        var px = Image(bg.R, bg.G, bg.B, w, h);
        int bw = (int)(w * Math.Sqrt(share)), bh = (int)(h * Math.Sqrt(share));
        for (int y = h / 2 - bh / 2; y < h / 2 + bh / 2; y++)
            for (int x = w / 2 - bw / 2; x < w / 2 + bw / 2; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = fg.B; px[i + 1] = fg.G; px[i + 2] = fg.R;
            }
        return px;
    }

    static readonly (byte, byte, byte) Grass = (60, 120, 50), Gold = (255, 200, 40), Olive = (95, 105, 60);

    [Fact]
    public void GoldGlowLooksLikeAChest()
    {
        var chest = ContainerLook.Features(Scene(Grass, Gold, 0.2), 80, 60);
        var ammo = ContainerLook.Features(Scene(Grass, Olive, 0.2), 80, 60);
        Assert.True(ContainerLook.LooksLikeChestByRule(chest));
        Assert.False(ContainerLook.LooksLikeChestByRule(ammo));
    }

    [Fact]
    public void FeaturesAreShares()
    {
        var f = ContainerLook.Features(Image(10, 10, 10), 60, 40);
        Assert.Equal(ContainerLook.Length, f.Length);
        Assert.Equal(1f, f[..(ContainerLook.HueBins + 2)].Sum(), 3); // every pixel lands in one bin
        Assert.True(f[ContainerLook.HueBins] > 0.99f); // all dark
    }

    [Fact]
    public void LearnsFromLabelledExamples()
    {
        // Say a player's chests look faint (little vivid gold) - the rule alone would miss them, the model learns.
        var faintChest = ContainerLook.Features(Scene(Grass, (180, 160, 110), 0.3), 80, 60);
        var ammo = ContainerLook.Features(Scene(Grass, Olive, 0.3), 80, 60);
        Assert.False(ContainerLook.LooksLikeChestByRule(faintChest));

        var samples = new List<LookSample>();
        for (int i = 0; i < 8; i++) samples.Add(new LookSample { F = faintChest, Chest = true });
        for (int i = 0; i < 6; i++) samples.Add(new LookSample { F = ammo, Chest = false });
        var model = new LookModel(samples);
        Assert.True(model.Trained);
        Assert.True(model.IsChest(faintChest));
        Assert.False(model.IsChest(ammo));
        Assert.Equal(1.0, model.Accuracy());
    }

    [Fact]
    public void UntrainedModelUsesTheGoldRule()
    {
        var model = new LookModel([new LookSample { F = new float[ContainerLook.Length], Chest = true }]);
        Assert.False(model.Trained);
        Assert.True(model.IsChest(ContainerLook.Features(Scene(Grass, Gold, 0.2), 80, 60)));
    }
}
