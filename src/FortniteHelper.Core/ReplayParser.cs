using System.Security.Cryptography;
using System.Text;

namespace FortniteHelper.Core;

public sealed class ReplayStats
{
    public float Accuracy { get; init; }
    public uint Assists { get; init; }
    public uint Eliminations { get; init; }
    public uint WeaponDamage { get; init; }
    public uint OtherDamage { get; init; }
    public uint Revives { get; init; }
    public uint DamageTaken { get; init; }
    public uint DamageToStructures { get; init; }
    public uint MaterialsGathered { get; init; }
    public uint MaterialsUsed { get; init; }
    public uint TotalTraveled { get; init; }
}

public sealed class ReplayInfo
{
    public required string Path { get; init; }
    public uint FileVersion { get; init; }
    public int LengthMs { get; init; }
    public DateTime TimestampUtc { get; init; }
    public string FriendlyName { get; init; } = "";
    public bool IsLive { get; init; }
    public bool IsEncrypted { get; init; }
    public ReplayStats? Stats { get; set; }
    public int? Placement { get; set; }
    public int? TotalPlayers { get; set; }
    public int EliminationEvents { get; set; }

    public DateTime EndUtc => TimestampUtc.AddMilliseconds(LengthMs);
}

/// <summary>
/// Minimal reader for Unreal Engine local replay files (.replay) as written by Fortnite.
/// Reads the file header and the (AES-encrypted) event chunks only; network data is skipped.
/// </summary>
public static class ReplayParser
{
    const uint Magic = 0x1CA2E27F;
    const int ChunkEvent = 3;

    public static ReplayInfo Parse(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        using var r = new BinaryReader(fs, Encoding.ASCII, leaveOpen: false);

        if (r.ReadUInt32() != Magic) throw new InvalidDataException("Not an Unreal replay file.");
        uint fileVersion = r.ReadUInt32();

        if (fileVersion >= 7) // HISTORY_CUSTOM_VERSIONS
        {
            int count = r.ReadInt32();
            if (count is < 0 or > 1000) throw new InvalidDataException("Bad custom version count.");
            fs.Seek(count * 20L, SeekOrigin.Current); // FGuid (16) + int32
        }

        int lengthMs = r.ReadInt32();
        r.ReadUInt32(); // network version
        r.ReadUInt32(); // changelist
        string friendly = ReadFString(r);
        bool isLive = r.ReadUInt32() != 0;
        DateTime ts = default;
        if (fileVersion >= 3)
        {
            long ticks = r.ReadInt64();
            // Fortnite writes the recording time in local time (it matches the "UnsavedReplay-<date>" file name).
            if (ticks > 0 && ticks < DateTime.MaxValue.Ticks) ts = new DateTime(ticks, DateTimeKind.Local).ToUniversalTime();
        }
        if (fileVersion >= 2) r.ReadUInt32(); // compressed
        bool encrypted = false;
        byte[] key = [];
        if (fileVersion >= 6)
        {
            encrypted = r.ReadUInt32() != 0;
            int keyLen = r.ReadInt32();
            if (keyLen is < 0 or > 64) throw new InvalidDataException("Bad key length.");
            key = r.ReadBytes(keyLen);
        }

        var info = new ReplayInfo
        {
            Path = path,
            FileVersion = fileVersion,
            LengthMs = lengthMs,
            TimestampUtc = ts,
            FriendlyName = friendly,
            IsLive = isLive,
            IsEncrypted = encrypted,
        };

        using Aes? aes = encrypted && key.Length == 32 ? Aes.Create() : null;
        if (aes is not null) aes.Key = key;

        long fileLen = fs.Length;
        while (fs.Position + 8 <= fileLen)
        {
            uint type = r.ReadUInt32();
            int size = r.ReadInt32();
            long start = fs.Position;
            if (size < 0 || start + size > fileLen) break;

            if (type == ChunkEvent)
            {
                try { ReadEvent(r, info, aes); }
                catch (Exception) { /* a malformed event must not break the whole replay */ }
            }
            fs.Position = start + size;
        }
        return info;
    }

    static void ReadEvent(BinaryReader r, ReplayInfo info, Aes? aes)
    {
        string id = ReadFString(r);
        string group = ReadFString(r);
        string metadata = ReadFString(r);
        r.ReadUInt32(); // start time
        r.ReadUInt32(); // end time
        int size = r.ReadInt32();
        if (size is < 0 or > 1 << 20) return;

        if (group == "playerElim") { info.EliminationEvents++; return; }

        // Fortnite stores these as group "AthenaReplayBrowserEvents" with the event type in the metadata.
        string kind = metadata is "AthenaMatchStats" or "AthenaMatchTeamStats" ? metadata
                    : group is "AthenaMatchStats" or "AthenaMatchTeamStats" ? group : "";
        if (kind.Length == 0) return;

        byte[] data = r.ReadBytes(size);
        if (aes is not null) data = Decrypt(aes, data);
        using var br = new BinaryReader(new MemoryStream(data));

        if (kind == "AthenaMatchStats" && data.Length >= 48)
        {
            br.ReadUInt32(); // unknown / version
            info.Stats = new ReplayStats
            {
                Accuracy = br.ReadSingle(),
                Assists = br.ReadUInt32(),
                Eliminations = br.ReadUInt32(),
                WeaponDamage = br.ReadUInt32(),
                OtherDamage = br.ReadUInt32(),
                Revives = br.ReadUInt32(),
                DamageTaken = br.ReadUInt32(),
                DamageToStructures = br.ReadUInt32(),
                MaterialsGathered = br.ReadUInt32(),
                MaterialsUsed = br.ReadUInt32(),
                TotalTraveled = br.ReadUInt32(),
            };
        }
        else if (kind == "AthenaMatchTeamStats" && data.Length >= 12)
        {
            br.ReadUInt32();
            info.Placement = (int)br.ReadUInt32();
            info.TotalPlayers = (int)br.ReadUInt32();
        }
    }

    static byte[] Decrypt(Aes aes, byte[] data)
    {
        int len = data.Length - data.Length % 16;
        if (len <= 0) return data;
        try
        {
            return aes.DecryptEcb(data.AsSpan(0, len), PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            return aes.DecryptEcb(data.AsSpan(0, len), PaddingMode.None);
        }
    }

    static string ReadFString(BinaryReader r)
    {
        int len = r.ReadInt32();
        if (len == 0) return "";
        if (len < 0)
        {
            if (len < -100_000) throw new InvalidDataException("Bad string length.");
            var bytes = r.ReadBytes(-len * 2);
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }
        if (len > 100_000) throw new InvalidDataException("Bad string length.");
        return Encoding.Latin1.GetString(r.ReadBytes(len)).TrimEnd('\0');
    }

    /// <summary>Copies the replay's stats onto a match record.</summary>
    public static void Apply(ReplayInfo info, MatchRecord m)
    {
        m.ReplayFile = info.Path;
        m.ReplayParsed = true;
        if (info.Placement is { } pl and > 0)
        {
            m.Placement = pl;
            if (pl == 1) m.Won = true;
        }
        if (info.TotalPlayers is { } tp and > 0) m.TotalPlayers = tp;
        if (info.Stats is { } s)
        {
            m.Kills = (int)s.Eliminations;
            m.Assists = (int)s.Assists;
            m.Accuracy = Math.Clamp(s.Accuracy, 0f, 1f);
            m.DamageDealt = (int)(s.WeaponDamage + s.OtherDamage);
            m.DamageTaken = (int)s.DamageTaken;
            m.DamageToStructures = (int)s.DamageToStructures;
            m.Revives = (int)s.Revives;
            m.MaterialsGathered = (int)s.MaterialsGathered;
            m.MaterialsUsed = (int)s.MaterialsUsed;
            m.DistanceMeters = (int)Math.Round(s.TotalTraveled / 100.0);
        }
    }
}
