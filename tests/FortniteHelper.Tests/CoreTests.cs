using FortniteHelper.Core;
using Xunit;

namespace FortniteHelper.Tests;

public class LogParserTests
{
    [Fact]
    public void ParsesServerJoin()
    {
        var e = LogParser.Parse("[2026.11.11-21.25.12:839][673]LogNet: Welcomed by server (Level: /Delilah/Maps/Delilah_Terrain?VerseURI=/Fortnite.com/GameFeatures/BRRoot, Game: /Game/Athena/Athena_GameMode.Athena_GameMode_C)");
        var j = Assert.IsType<ServerJoinedEvent>(e);
        Assert.Equal("/Delilah/Maps/Delilah_Terrain", j.Map);
        Assert.Equal(new DateTime(2026, 11, 11, 21, 25, 12, 839, DateTimeKind.Utc), j.Time);
    }

    [Theory]
    [InlineData("[2026.11.11-21.25.16:989][721]LogFort: PLAYLIST: Playlist Object replicated to client in AFortGameStateAthena::OnRep_CurrentPlaylistInfo() PlaylistName is Playlist_DefaultSolo (Client Only)", "Playlist_DefaultSolo")]
    [InlineData("[2026.11.11-21.25.17:143][722]LogFort: PLAYLIST: Playlist Object finished loading its assets in AFortGameStateAthena::OnPlaylistDataLoadCompleted(), PlaylistName is Playlist_HabaneroSolo (Server AND Client)", "Playlist_HabaneroSolo")]
    public void ParsesPlaylist(string line, string expected) =>
        Assert.Equal(expected, Assert.IsType<PlaylistEvent>(LogParser.Parse(line)).Playlist);

    [Fact]
    public void ParsesPhasesAndSteps()
    {
        var gp = Assert.IsType<GamePhaseEvent>(LogParser.Parse("[2026.11.11-21.26.02:469][136]LogBattleRoyaleGamePhaseLogic: HandleGamePhaseChanged. OldPhase = EAthenaGamePhase::Warmup. NewPhase = EAthenaGamePhase::Aircraft"));
        Assert.Equal("Warmup", gp.OldPhase);
        Assert.Equal("Aircraft", gp.NewPhase);

        var st = Assert.IsType<PhaseStepEvent>(LogParser.Parse("[2026.11.11-21.29.28:000][  5]LogBattleRoyaleGamePhaseLogic: UpdateGamePhaseStep. Phase = EAthenaGamePhase::SafeZones. PhaseStep = EAthenaGamePhaseStep::StormHolding"));
        Assert.Equal("SafeZones", st.Phase);
        Assert.Equal("StormHolding", st.Step);
    }

    [Fact]
    public void ParsesMiscEvents()
    {
        Assert.IsType<LobbyLoadedEvent>(LogParser.Parse("[2026.11.11-21.43.56:679][  1]LogLoad: LoadMap: /Game/Maps/Frontend?closed"));
        Assert.True(Assert.IsType<AircraftEvent>(LogParser.Parse("[2026.11.11-21.26.02:459][136]LogFort: EnterAircraft: Athena_PlayerController_C_2147482646")).Entered);
        Assert.False(Assert.IsType<AircraftEvent>(LogParser.Parse("[2026.11.11-21.26.31:695][  5]LogFort: ExitAircraft: Athena_PlayerController_C_2147482646 (Pawn PlayerPawn_Athena_C_2147482630)")).Entered);
        Assert.IsType<WinEvent>(LogParser.Parse("[2026.11.11-21.42.55:652][631]LogFortPostScreen: [3cdb]  Blocked by HUDContext:  ShouldDisplayEliminationScreen 1  GetLocalPlayerHasWinningPlacement 1"));
        var ms = Assert.IsType<MatchStateEvent>(LogParser.Parse("[2026.11.11-21.25.18:452][723]LogFort: MatchState changed previous=WaitingToStart current=InProgress"));
        Assert.Equal("InProgress", ms.Current);
        var net = Assert.IsType<NetworkStatsEvent>(LogParser.Parse("[2026.11.11-21.35.17:064][366]LogFortAnalyticsEvent: Display: Core.EndGamePhase_ClientMetrics PingVariance Stats: BaselinePing: 33.50 AveragePingVariance: 34.32 MaxPingVariance: 52.50"));
        Assert.Equal(33.5, net.PingMs);
        Assert.Equal("someone", Assert.IsType<PlayerNameEvent>(LogParser.Parse("[2026.11.11-21.25.18:452][723]LogPlayerKills: HandlePlayerStateChanged : someone")).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\tPlaylistName [NoPlaylist]]")]
    [InlineData("[2026.11.11-21.24.39:810][768]LogNet: Browse: /Game/Maps/Frontend?Name=Player")]
    [InlineData("[2026.11.11-21.24.39:810][768]LogGameFeatures: Loading 4156 BuiltIn GameFeaturePlugins.")]
    public void IgnoresOtherLines(string line) => Assert.Null(LogParser.Parse(line));
}

public class MatchTrackerTests
{
    static readonly DateTime T0 = new(2026, 11, 11, 21, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TracksAFullMatch()
    {
        var t = new MatchTracker();
        MatchRecord? ended = null;
        var storms = new List<StormObservation>();
        t.MatchEnded += m => ended = m;
        t.StormStepObserved += storms.Add;

        t.Process(new ServerJoinedEvent(T0, "/Delilah/Maps/Delilah_Terrain"));
        Assert.Equal(MatchPhase.Loading, t.Phase);
        t.Process(new PlaylistEvent(T0.AddSeconds(4), "Playlist_DefaultSolo"));
        t.Process(new MatchStateEvent(T0.AddSeconds(6), "WaitingToStart", "InProgress"));
        Assert.Equal(MatchPhase.Warmup, t.Phase);
        t.Process(new AircraftEvent(T0.AddSeconds(50), true));
        Assert.Equal(MatchPhase.Bus, t.Phase);
        t.Process(new AircraftEvent(T0.AddSeconds(70), false));
        Assert.Equal(MatchPhase.Playing, t.Phase);
        t.Process(new PhaseStepEvent(T0.AddSeconds(150), "SafeZones", "StormForming"));
        t.Process(new PhaseStepEvent(T0.AddSeconds(250), "SafeZones", "StormHolding"));
        Assert.Equal(new StormStatus(1, StormStep.Holding, T0.AddSeconds(250)), t.Storm);
        t.Process(new PhaseStepEvent(T0.AddSeconds(340), "SafeZones", "StormShrinking"));
        t.Process(new PhaseStepEvent(T0.AddSeconds(410), "SafeZones", "StormHolding"));
        Assert.Equal(2, t.Storm!.Index);
        t.Process(new WinEvent(T0.AddSeconds(500)));
        t.Process(new LobbyLoadedEvent(T0.AddSeconds(600)));

        Assert.NotNull(ended);
        Assert.Equal("Playlist_DefaultSolo", ended!.Playlist);
        Assert.Equal(T0.AddSeconds(70), ended.DropUtc);
        Assert.True(ended.Won);
        Assert.Equal(2, ended.StormReached);
        Assert.Equal(TimeSpan.FromSeconds(600), ended.Duration);
        Assert.Equal(MatchPhase.Lobby, t.Phase);
        Assert.Equal([(StormStep.Forming, 0, 100.0), (StormStep.Holding, 1, 90.0), (StormStep.Shrinking, 1, 70.0)],
            storms.Select(s => (s.Step, s.Index, s.Seconds)));
    }

    [Fact]
    public void MatchThatNeverStartedIsNotRecorded()
    {
        var t = new MatchTracker();
        int ended = 0;
        t.MatchEnded += _ => ended++;
        t.Process(new ServerJoinedEvent(T0, "/Delilah/Maps/Delilah_Terrain"));
        t.Process(new LobbyLoadedEvent(T0.AddSeconds(10)));
        Assert.Equal(0, ended);
    }

    [Fact]
    public void StormEstimatesUseDefaultsAndLearn()
    {
        var s = new StormTimings();
        Assert.Equal(100, s.Estimate(StormStep.Forming, 0));
        Assert.Equal(90, s.Estimate(StormStep.Holding, 1));
        Assert.Equal(50, s.Estimate(StormStep.Shrinking, 3));
        Assert.Equal(20, s.Estimate(StormStep.Shrinking, 99));
        s.Learn(StormStep.Holding, 1, 120);
        Assert.Equal(112.5, s.Estimate(StormStep.Holding, 1));
    }
}

public class ValorantCodeTests
{
    [Fact]
    public void ParsesCommonProCode()
    {
        var p = ValorantCode.Parse("0;P;c;5;h;0;0l;4;0o;2;0a;1;0f;0;1b;0");
        Assert.Equal("#00FFFF", p.Color);
        Assert.False(p.Outlines);
        Assert.True(p.InnerLines);
        Assert.Equal(4, p.InnerLength);
        Assert.Equal(2, p.InnerOffset);
        Assert.Equal(1.0, p.InnerOpacity);
        Assert.Equal(2, p.InnerThickness); // default kept
        Assert.False(p.OuterLines);
    }

    [Fact]
    public void ParsesCustomColorAndStopsAtAdsSection()
    {
        var p = ValorantCode.Parse("0;s;1;P;c;8;u;FF8800FF;d;1;z;3;0b;0;1b;0;A;0l;20;0b;1");
        Assert.Equal("#FF8800", p.Color);
        Assert.True(p.CenterDot);
        Assert.Equal(3, p.DotThickness);
        Assert.False(p.InnerLines); // the ADS section must not override
        Assert.Equal(6, p.InnerLength);
    }

    [Fact]
    public void DefaultsMatchValorantWhenCodeIsMinimal()
    {
        var p = ValorantCode.Parse("0;P;c;1");
        Assert.True(p.Outlines);
        Assert.True(p.InnerLines);
        Assert.True(p.OuterLines);
        Assert.Equal(0.35, p.OuterOpacity);
    }

    [Fact]
    public void ExportRoundTrips()
    {
        var original = new CrosshairProfile { Color = "#12AB34", CenterDot = true, InnerLength = 9, InnerOffset = 5, OuterLines = false, OutlineOpacity = 1 };
        var back = ValorantCode.Parse(ValorantCode.Export(original));
        Assert.Equal(original.Color, back.Color);
        Assert.Equal(original.CenterDot, back.CenterDot);
        Assert.Equal(original.InnerLength, back.InnerLength);
        Assert.Equal(original.InnerOffset, back.InnerOffset);
        Assert.Equal(original.OuterLines, back.OuterLines);
        Assert.Equal(original.OutlineOpacity, back.OutlineOpacity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    public void RejectsGarbage(string code) => Assert.Throws<FormatException>(() => ValorantCode.Parse(code));
}

public class ChestClassifierTests
{
    readonly ChestClassifier _c = new(["chest"], ["ammo box"]);

    [Theory]
    [InlineData("Chest\nE Search", ContainerKind.Chest)]
    [InlineData("RARE CHEST [E] SEARCH", ContainerKind.Chest)]
    [InlineData("Chcst Search", ContainerKind.Chest)]
    [InlineData("Ch est", ContainerKind.Chest)]
    [InlineData("Ammo Box  Search", ContainerKind.AmmoBox)]
    [InlineData("AMM0 B0X", ContainerKind.AmmoBox)]
    [InlineData("AmmoBox", ContainerKind.AmmoBox)]
    public void Recognizes(string text, ContainerKind expected) => Assert.Equal(expected, _c.Classify(text));

    [Theory]
    [InlineData("")]
    [InlineData("Open Door")]
    [InlineData("Pick up Assault Rifle")]
    [InlineData("best")]
    [InlineData("Box")]
    public void IgnoresOtherPrompts(string text) => Assert.Null(_c.Classify(text));
}

public class PlaylistNameTests
{
    [Theory]
    [InlineData("Playlist_DefaultSolo", "Solo")]
    [InlineData("Playlist_DefaultSquad", "Squads")]
    [InlineData("Playlist_HabaneroSolo", "Ranked Solo")]
    [InlineData("Playlist_BlastberrySquad", "Reload Squads")]
    [InlineData(null, "Battle Royale")]
    public void Prettifies(string? raw, string expected) => Assert.Equal(expected, PlaylistNames.Pretty(raw));
}

public class RealDataTests
{
    [Fact]
    public void ImportsLocalHistoryWhenAvailable()
    {
        var logs = HistoryImporter.LogFilesOldestFirst().ToList();
        if (logs.Count == 0) return; // no Fortnite on this machine
        var matches = HistoryImporter.ImportLogs(logs);
        var replays = HistoryImporter.ScanReplays(FortnitePaths.DemosDir);
        var all = new List<MatchRecord>();
        HistoryImporter.Merge(all, matches, replays);
        Assert.All(all, m => Assert.True(m.StartUtc.Year > 2015));
        // Wins detected in the log must agree with the replay placement when both exist.
        Assert.All(all.Where(m => m.Source == "log" && m.Won && m.Placement is not null), m => Assert.Equal(1, m.Placement));
    }
}
