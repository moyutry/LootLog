using FortniteReplayReader.Models;
using FortniteReplayReader.Models.NetFieldExports;
using Unreal.Core;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace FortniteHelper.Replays;

/// <summary>
/// Reads a finished Fortnite replay (.replay) and works out exactly which containers the player opened
/// (chests carry the pawn that searched or broke them), plus landing, storm, fights and the path.
/// Replays only become readable after the match: Fortnite writes the decryption key when it finishes.
/// </summary>
public static class ReplayAnalyzer
{
    /// <summary>Bump when the analysis gets better, so stored results are recomputed.</summary>
    public const int Version = 4;

    static readonly object ParseLock = new();

    /// <summary>Analyzes a replay. Returns null when the file is not a finished, readable replay.</summary>
    public static MatchAnalysis? Analyze(string path)
    {
        lock (ParseLock)
        {
            var result = Run(path);
            // A container class we didn't know yet: register it and read the replay again so it's counted.
            if (result is { NewContainerClasses.Count: > 0 } && ContainerTypes.Register(result.NewContainerClasses) > 0)
            {
                var again = Run(path);
                if (again is not null) { again.NewContainerClasses = result.NewContainerClasses; result = again; }
            }
            return result;
        }
    }

    static MatchAnalysis? Run(string path)
    {
        ContainerTypes.EnsureInitialized();
        var reader = new Reader();
        FortniteReplay replay;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            replay = reader.ReadReplay(fs);
        }
        catch (Exception)
        {
            return null;
        }
        if (replay.Info is null || replay.Info.IsLive) return null;
        return reader.Build(path, replay);
    }

    sealed class PawnInfo
    {
        public uint State;
        public bool Mine;
        public FVector? Pos;
        public float PosT = -999;
        public bool Sky, Para, WasAirborne, InStorm;
        public float StormSince;
    }

    sealed class StateInfo
    {
        public int? PlayerId;
        public int? Team;
    }

    sealed class Cont
    {
        public uint Guid;
        public string Id = "";
        public string Class = "";
        public bool? FirstSearched;
        public uint By;
        public float? OpenT;
        public FVector? ByPos;
    }

    sealed record Tossed(float T, FVector Pos, string Item)
    {
        public bool Used { get; set; }
    }

    sealed class Reader : FortniteReplayReader.ReplayReader
    {
        public Reader() : base(null, ParseMode.Full) { }

        float _t;
        uint _recorder;
        readonly List<double> _worldOffsets = [];
        readonly Dictionary<uint, PawnInfo> _pawns = [];
        readonly Dictionary<uint, StateInfo> _states = [];
        readonly Dictionary<uint, Cont> _openContainers = [];
        readonly List<Cont> _containers = [];
        readonly List<Tossed> _tossed = [];
        readonly List<float> _mySearchStarts = [];
        readonly List<MapPoint> _path = [];
        readonly List<string> _newClasses = [];
        float? _busJump, _land;
        MapPoint? _landPos;
        int _enemiesNearLanding;
        float _stormSeconds;

        public override void ReadDemoFrameIntoPlaybackPackets(FArchive archive)
        {
            // Peek at the frame's time stamp (the base class reads it again).
            int pos = archive.Position;
            if (archive.NetworkVersion >= NetworkVersionHistory.HISTORY_MULTIPLE_LEVELS) archive.ReadInt32();
            _t = archive.ReadSingle();
            archive.Seek(pos, SeekOrigin.Begin);
            base.ReadDemoFrameIntoPlaybackPackets(archive);
        }

        // Some event types are unknown to the library; they must not stop the whole replay.
        public override void ReadEvent(FArchive archive)
        {
            try { base.ReadEvent(archive); } catch (Exception) { }
        }

        protected override void OnChannelClosed(uint channelIndex, NetworkGUID actor)
        {
            _openContainers.Remove(channelIndex);
            base.OnChannelClosed(channelIndex, actor);
        }

        uint ActorGuid(uint channel) => Channels[channel]?.Actor?.ActorNetGUID?.Value ?? 0;

        protected override void OnExportRead(uint channelIndex, INetFieldExportGroup? exportGroup)
        {
            base.OnExportRead(channelIndex, exportGroup);
            switch (exportGroup)
            {
                case SearchableContainer c: OnContainer(channelIndex, c); break;
                case PlayerPawn p: OnPawn(channelIndex, p); break;
                case FortPickup p: OnPickup(channelIndex, p); break;
                case FortPlayerState s: OnPlayerState(channelIndex, s); break;
                case GameState g:
                    if (g.RecorderPlayerState?.Value is uint r && r != 0) _recorder = r;
                    if (g.ReplicatedWorldTimeSecondsDouble is double wd) _worldOffsets.Add(wd - _t);
                    else if (g.ReplicatedWorldTimeSeconds is float w) _worldOffsets.Add(w - _t);
                    break;
            }
        }

        void OnPlayerState(uint ch, FortPlayerState s)
        {
            uint g = ActorGuid(ch);
            if (g == 0) return;
            if (!_states.TryGetValue(g, out var st)) _states[g] = st = new StateInfo();
            if (s.PlayerID is int id) st.PlayerId = id;
            if (s.TeamIndex is int team) st.Team = team;
        }

        void OnContainer(uint ch, SearchableContainer c)
        {
            uint g = ActorGuid(ch);
            if (!_openContainers.TryGetValue(ch, out var k) || k.Guid != g)
            {
                string id = "";
                if (g != 0) _netGuidCache.TryGetPathName(g, out id);
                k = new Cont { Guid = g, Id = id ?? "", Class = c.ClassPath, FirstSearched = c.bAlreadySearched };
                _openContainers[ch] = k;
                _containers.Add(k);
            }
            k.FirstSearched ??= c.bAlreadySearched;
            if (c.SearchingPawn is uint by && by != 0 && k.By == 0)
            {
                k.By = by;
                k.OpenT ??= _t;
                if (_pawns.TryGetValue(by, out var p) && p.Pos is not null && _t - p.PosT < 6) k.ByPos = p.Pos;
            }
            else if (c.bAlreadySearched == true && k.OpenT is null && k.FirstSearched == false)
            {
                k.OpenT = _t;
            }
        }

        void OnPickup(uint ch, FortPickup p)
        {
            if (p.bTossedFromContainer != true) return;
            if (Channels[ch]?.Actor?.Location is not FVector loc) return;
            string item = "";
            if (p.ItemDefinition is not null) _netGuidCache.TryGetPathName(p.ItemDefinition.Value, out item);
            _tossed.Add(new Tossed(_t, loc, item ?? ""));
        }

        void OnPawn(uint ch, PlayerPawn pp)
        {
            uint g = ActorGuid(ch);
            if (g == 0) return;
            if (!_pawns.TryGetValue(g, out var pw)) _pawns[g] = pw = new PawnInfo();
            if (pp.PlayerState is uint st && st != 0) pw.State = st;
            // Only the recording player's controller is replicated to their client: a pawn with a controller is "me".
            if ((pp.Controller is uint ctl && ctl != 0) || (pp.Owner?.Value is uint own && own != 0)) pw.Mine = true;
            if (_recorder != 0 && pw.State == _recorder) pw.Mine = true;

            if (pp.ReplicatedMovement is FRepMovement m && m.Location is FVector loc)
            {
                pw.Pos = loc;
                pw.PosT = _t;
                if (pw.Mine) SamplePath(loc);
            }
            if (!pw.Mine) return;

            if (pp.bStartedInteractSearch == true) _mySearchStarts.Add(_t);
            if (pp.bIsSkydivingFromBus == true) _busJump ??= _t;
            if (pp.bIsSkydiving is bool sky) pw.Sky = sky;
            if (pp.bIsParachuteOpen is bool para) pw.Para = para;
            if (pw.Sky || pw.Para) pw.WasAirborne = true;
            if (_busJump is not null && _land is null && pw.WasAirborne && !pw.Sky && !pw.Para && pw.Pos is not null && _t > _busJump + 3)
            {
                _land = _t;
                _landPos = new MapPoint(_t, pw.Pos.X, pw.Pos.Y, pw.Pos.Z);
                _enemiesNearLanding = CountEnemiesNear(pw.Pos, 15000);
            }
            if (pp.bIsInAnyStorm is bool storm && storm != pw.InStorm)
            {
                if (storm) pw.StormSince = _t;
                else _stormSeconds += _t - pw.StormSince;
                pw.InStorm = storm;
            }
        }

        void SamplePath(FVector loc)
        {
            if (_path.Count > 0)
            {
                var last = _path[^1];
                double dx = loc.X / 100 - last.X, dy = loc.Y / 100 - last.Y;
                if (_t - last.T < 2 && dx * dx + dy * dy < 40 * 40) return;
            }
            _path.Add(new MapPoint(_t, loc.X, loc.Y, loc.Z));
        }

        int? TeamOf(uint pawn) =>
            _pawns.TryGetValue(pawn, out var p) && _states.TryGetValue(p.State, out var s) ? s.Team : null;

        int? MyTeam => _states.TryGetValue(_recorder, out var s) ? s.Team : null;

        string? NameOfPawn(uint pawn, IList<PlayerData> players)
        {
            if (!_pawns.TryGetValue(pawn, out var p) || !_states.TryGetValue(p.State, out var s) || s.PlayerId is not int id) return null;
            return players.FirstOrDefault(x => x.Id == id)?.PlayerName;
        }

        int CountEnemiesNear(FVector pos, double radiusCm)
        {
            int n = 0;
            var myTeam = MyTeam;
            foreach (var (g, p) in _pawns)
            {
                if (p.Mine || p.Pos is null || _t - p.PosT > 15) continue;
                if (myTeam is not null && TeamOf(g) == myTeam) continue;
                if (p.Pos.DistanceTo(pos) <= radiusCm) n++;
            }
            return n;
        }

        protected override void Cleanup()
        {
            // Look for searchable container classes we don't have a parser class for yet.
            try
            {
                foreach (var (path, group) in _netGuidCache.NetFieldExportGroupMap)
                {
                    if (path.EndsWith("_ClassNetCache", StringComparison.Ordinal) || group?.NetFieldExports is null) continue;
                    bool searched = false, searcher = false;
                    foreach (var f in group.NetFieldExports)
                    {
                        if (f?.Name == "bAlreadySearched") searched = true;
                        else if (f?.Name == "SearchingPawn") searcher = true;
                    }
                    if (searched && searcher && !ContainerTypes.IsRegistered(path)) _newClasses.Add(path);
                }
            }
            catch (Exception) { }
            base.Cleanup();
        }

        public MatchAnalysis Build(string path, FortniteReplay replay)
        {
            var info = replay.Info;
            var a = new MatchAnalysis
            {
                Version = ReplayAnalyzer.Version,
                ReplayFile = path,
                RecordedUtc = DateTime.SpecifyKind(info.Timestamp, DateTimeKind.Local).ToUniversalTime(),
                LengthSec = info.LengthInMs / 1000f,
                NewContainerClasses = [.. _newClasses],
                BusJumpT = _busJump,
                LandT = _land,
                Land = _landPos,
                EnemiesNearLanding = _enemiesNearLanding,
                Path = _path,
            };

            double offset = 0;
            if (_worldOffsets.Count > 0)
            {
                _worldOffsets.Sort();
                offset = _worldOffsets[_worldOffsets.Count / 2];
            }
            float ToDemo(double world) => (float)Math.Max(0, world - offset);

            // ---- Who am I ----
            var myPawns = _pawns.Where(p => p.Value.Mine).Select(p => p.Key).ToHashSet();
            int? myTeam = MyTeam;
            int? myPlayerId = _states.TryGetValue(_recorder, out var myState) ? myState.PlayerId : null;
            var players = replay.PlayerData?.ToList() ?? [];
            var me = players.FirstOrDefault(p => myPlayerId is not null && p.Id == myPlayerId)
                     ?? players.OrderByDescending(p => p.Locations?.Count ?? 0).FirstOrDefault();
            if (me is not null)
            {
                a.PlayerName = me.PlayerName;
                myTeam ??= me.TeamIndex;
                a.Placement = me.Placement;
            }
            a.TeamIndex = myTeam;
            if (myTeam is not null)
                a.Squad = players.Where(p => p.TeamIndex == myTeam && p != me && !string.IsNullOrEmpty(p.PlayerName))
                                 .Select(p => p.PlayerName!).Distinct().ToList();
            if (replay.TeamStats is { } ts)
            {
                if (ts.Position > 0) a.Placement = (int)ts.Position;
                if (ts.TotalPlayers > 0) a.TotalPlayers = (int)ts.TotalPlayers;
            }
            a.Won = a.Placement == 1 || (myTeam is not null && replay.GameData?.WinningTeam is uint wt && wt == myTeam);

            // ---- Containers: one entry per placed actor, opened at the first time we saw who opened it ----
            var unique = new Dictionary<string, Cont>();
            foreach (var c in _containers)
            {
                if (c.Id.Length == 0) continue;
                if (!unique.TryGetValue(c.Id, out var prev)) { unique[c.Id] = c; continue; }
                if (prev.By == 0 && c.By != 0) unique[c.Id] = c;
            }

            float warmupCutoff = _busJump is { } bj ? bj - 2 : float.MinValue;
            var opened = unique.Values.Where(c => c.OpenT is not null).OrderBy(c => c.OpenT).ToList();
            var tossed = _tossed.OrderBy(t => t.T).ToList();
            foreach (var c in opened)
            {
                bool mine = c.By != 0 && myPawns.Contains(c.By);
                var cluster = FindLoot(c, tossed);
                FVector? pos = cluster is { Count: > 0 }
                    ? new FVector(cluster.Average(x => x.Pos.X), cluster.Average(x => x.Pos.Y), cluster.Average(x => x.Pos.Z))
                    : mine ? c.ByPos : null;
                var type = ContainerTypes.TypeOf(c.Class);
                bool rare = ContainerTypes.IsRare(c.Class);

                if (pos is not null)
                    a.ChestSpots.Add(new ChestSpot { Id = c.Id, Type = type, Rare = rare, X = (float)Math.Round(pos.X / 100, 1), Y = (float)Math.Round(pos.Y / 100, 1), Z = (float)Math.Round(pos.Z / 100, 1) });

                if (c.OpenT < warmupCutoff) continue;
                if (type == ContainerType.Chest && c.By != 0 && (mine || (myTeam is not null && TeamOf(c.By) == myTeam)))
                {
                    a.SquadChests++;
                    string who = mine ? a.PlayerName ?? "You" : NameOfPawn(c.By, players) ?? "Teammate";
                    var member = a.SquadMembers.FirstOrDefault(s => s.Name == who);
                    if (member is null) a.SquadMembers.Add(member = new SquadMember { Name = who, IsMe = mine });
                    member.Chests++;
                }
                if (!mine) continue;

                float t = c.OpenT!.Value;
                bool searched = _mySearchStarts.Any(s => s >= t - 3.5f && s <= t + 0.3f);
                a.Opened.Add(new OpenedContainer
                {
                    Id = c.Id,
                    Class = ContainerTypes.ShortName(c.Class),
                    Type = type,
                    Rare = rare,
                    T = MathF.Round(t, 1),
                    Pos = pos is null ? null : new MapPoint(t, pos.X, pos.Y, pos.Z),
                    Searched = searched,
                    Loot = cluster?.Select(x => ItemName(x.Item)).Where(n => n.Length > 0).ToList() ?? [],
                });
            }
            a.Chests = a.Opened.Count(o => o.Type == ContainerType.Chest);
            a.ChestsSearched = a.Opened.Count(o => o.Type == ContainerType.Chest && o.Searched);
            a.ChestsBroken = a.Chests - a.ChestsSearched;
            a.AmmoBoxes = a.Opened.Count(o => o.Type == ContainerType.AmmoBox);
            a.OtherContainers = a.Opened.Count(o => o.Type == ContainerType.Other);
            a.FirstChestT = a.Opened.FirstOrDefault(o => o.Type == ContainerType.Chest)?.T;

            // ---- Fights (the kill feed uses world time) ----
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in players)
            {
                if (string.IsNullOrEmpty(p.PlayerName)) continue;
                if (!string.IsNullOrEmpty(p.PlayerId)) names[p.PlayerId] = p.PlayerName;
                if (!string.IsNullOrEmpty(p.EpicId)) names[p.EpicId] = p.PlayerName;
                if (!string.IsNullOrEmpty(p.BotId)) names[p.BotId] = p.PlayerName;
            }
            string? Name(string? id, bool bot) => id is null ? null : names.TryGetValue(id, out var n) ? n : bot ? "a bot" : null;

            if (me?.Id is int myId && replay.KillFeed is { } feed)
            {
                foreach (var e in feed)
                {
                    float t = e.ReplicatedWorldTimeSecondsDouble is double d ? ToDemo(d) : e.ReplicatedWorldTimeSeconds is float f ? ToDemo(f) : 0;
                    var pos = e.DeathLocation is { } dl ? new MapPoint(t, dl.X, dl.Y, dl.Z) : null;
                    string? weapon = WeaponFromTags(e.DeathTags);
                    if (e.FinisherOrDowner == myId && e.PlayerId != myId && !e.IsRevived)
                    {
                        a.Fights.Add(new FightEvent { T = t, Kind = e.IsDowned ? "Knock" : "Elim", Other = Name(e.PlayerName, e.PlayerIsBot), OtherIsBot = e.PlayerIsBot, DistanceM = Meters(e.Distance), Weapon = weapon, Pos = pos, Raw = e.Distance });
                    }
                    else if (e.PlayerId == myId && !e.IsRevived)
                    {
                        a.Fights.Add(new FightEvent { T = t, Kind = e.IsDowned ? "KnockedBy" : "ElimBy", Other = Name(e.FinisherOrDownerName, e.FinisherOrDownerIsBot), OtherIsBot = e.FinisherOrDownerIsBot, DistanceM = Meters(e.Distance), Weapon = weapon, Pos = pos });
                    }
                }
                a.Fights.Sort((x, y) => x.T.CompareTo(y.T));
                a.Knocks = a.Fights.Count(f => f.Kind == "Knock");
                a.Elims = a.Fights.Count(f => f.Kind == "Elim");
                if (a.Fights.LastOrDefault(f => f.Kind == "ElimBy") is { } death)
                {
                    a.DeathT = death.T;
                    a.DeathPos = death.Pos;
                    a.KilledBy = death.Other;
                    a.KilledByBot = death.OtherIsBot;
                    a.KilledFromM = death.DistanceM;
                    a.DeathWeapon = death.Weapon;
                }
            }
            if (a.DeathT is null && me?.DeathTime is float dt && dt > 0)
            {
                a.DeathT = ToDemo(dt);
                if (me.DeathLocation is { } dl) a.DeathPos = new MapPoint(a.DeathT.Value, dl.X, dl.Y, dl.Z);
                a.DeathWeapon ??= WeaponFromTags(me.DeathTags);
            }

            // ---- Storm ----
            foreach (var p in _pawns.Values.Where(p => p.Mine && p.InStorm)) _stormSeconds += Math.Max(0, (a.DeathT ?? a.LengthSec) - p.StormSince);
            a.SecondsInStorm = MathF.Round(_stormSeconds, 1);
            int phase = 0;
            foreach (var z in replay.MapData?.SafeZones ?? [])
            {
                if (z.NextCenter is null || z.NextRadius <= 0) continue;
                a.Storm.Add(new StormCircle
                {
                    Phase = ++phase,
                    ShrinkStart = ToDemo(z.StartShrinkTime),
                    ShrinkEnd = ToDemo(z.FinishShrinkTime),
                    X = (float)Math.Round(z.NextCenter.X / 100, 1),
                    Y = (float)Math.Round(z.NextCenter.Y / 100, 1),
                    Radius = (float)Math.Round(z.NextRadius / 100, 1),
                });
            }

            float start = a.LandT ?? a.BusJumpT ?? 0;
            a.SecondsAlive = Math.Max(0, (a.DeathT ?? a.LengthSec) - start);
            return a;
        }

        /// <summary>The loot that sprang out of a container when it opened: pickups tossed within ~2 s, close together.</summary>
        static List<Tossed>? FindLoot(Cont c, List<Tossed> tossed)
        {
            float t = c.OpenT!.Value;
            var near = tossed.Where(x => !x.Used && x.T >= t - 0.6f && x.T <= t + 2.0f).ToList();
            if (near.Count == 0) return null;
            var clusters = new List<List<Tossed>>();
            foreach (var x in near)
            {
                var cl = clusters.FirstOrDefault(cl => cl[0].Pos.DistanceTo(x.Pos) < 500);
                if (cl is null) clusters.Add([x]); else cl.Add(x);
            }
            List<Tossed>? best = null;
            if (c.ByPos is { } by)
            {
                double bestD = 1500;
                foreach (var cl in clusters)
                {
                    double d = cl[0].Pos.DistanceTo(by);
                    if (d < bestD) { bestD = d; best = cl; }
                }
            }
            else if (clusters.Count == 1)
            {
                best = clusters[0];
            }
            if (best is not null) foreach (var x in best) x.Used = true;
            return best;
        }
    }

    /// <summary>The kill feed stores distances in centimeters.</summary>
    static float? Meters(float? d) => d is { } v and > 0 ? MathF.Round(v / 100, 1) : null;

    static string ItemName(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        int slash = path.LastIndexOf('/');
        var s = slash >= 0 ? path[(slash + 1)..] : path;
        int dot = s.IndexOf('.');
        return dot >= 0 ? s[..dot] : s;
    }

    /// <summary>Turns death gameplay tags into a weapon family ("Shotgun", "SMG" ...).</summary>
    public static string? WeaponFromTags(IEnumerable<string>? tags)
    {
        if (tags is null) return null;
        var all = string.Join(" ", tags);
        if (all.Length == 0) return null;
        (string Key, string Name)[] map =
        [
            ("Shotgun", "Shotgun"), ("SMG", "SMG"), ("Sniper", "Sniper"), ("DMR", "DMR"), ("Assault", "Assault Rifle"),
            ("Rifle", "Rifle"), ("Pistol", "Pistol"), ("Bow", "Bow"), ("Rocket", "Explosive"), ("Grenade", "Explosive"),
            ("Explosive", "Explosive"), ("Melee", "Melee"), ("Pickaxe", "Pickaxe"), ("Storm", "Storm"), ("Fall", "Fall damage"),
            ("Vehicle", "Vehicle"),
        ];
        foreach (var (key, name) in map)
            if (all.Contains(key, StringComparison.OrdinalIgnoreCase)) return name;
        return null;
    }
}
