using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using FortniteHelper.Infrastructure;

namespace FortniteHelper.Services;

/// <summary>A named location. X/Y are world coordinates in meters (the same system the replays use).</summary>
public sealed record MapPoi(string Id, string Name, double X, double Y, bool IsLandmark);
public sealed record MapData(string BlankImage, string PoiImage, IReadOnlyList<MapPoi> Pois);

/// <summary>The island map from fortnite-api.com (free, no key). Cached on disk so the map works offline.</summary>
public sealed class FortniteApi
{
    const string CacheFile = "map.json";
    static readonly HttpClient Http = CreateClient();
    MapData? _map;
    DateTime _mapAt;

    static HttpClient CreateClient()
    {
        var c = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        { Timeout = TimeSpan.FromSeconds(25) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("LootLog/3.1");
        return c;
    }

    public static HttpClient Client => Http;

    /// <summary>The last map we have (memory or disk), without going online.</summary>
    public MapData? CachedMap => _map ??= LoadDisk();

    public async Task<MapData?> GetMapAsync(bool force = false)
    {
        if (!force && _map is not null && DateTime.UtcNow - _mapAt < TimeSpan.FromHours(6)) return _map;
        try
        {
            var json = await Http.GetStringAsync("https://fortnite-api.com/v1/map?language=en");
            var map = Parse(json);
            if (map.Pois.Count > 0)
            {
                _map = map;
                _mapAt = DateTime.UtcNow;
                try { File.WriteAllText(JsonStore.PathOf(CacheFile), json); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Map download: " + ex.Message);
        }
        return CachedMap;
    }

    static MapData? LoadDisk()
    {
        try
        {
            var path = JsonStore.PathOf(CacheFile);
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }

    static MapData Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        var images = data.GetProperty("images");
        var pois = new List<MapPoi>();
        foreach (var p in data.GetProperty("pois").EnumerateArray())
        {
            var id = p.GetProperty("id").GetString() ?? "";
            var name = p.GetProperty("name").GetString() ?? "";
            var loc = p.GetProperty("location");
            if (string.IsNullOrWhiteSpace(name)) continue;
            // The API uses centimeters.
            pois.Add(new MapPoi(id, name.Trim(), loc.GetProperty("x").GetDouble() / 100, loc.GetProperty("y").GetDouble() / 100,
                !id.Contains(".POI.", StringComparison.OrdinalIgnoreCase)));
        }
        return new MapData(images.GetProperty("blank").GetString() ?? "", images.GetProperty("pois").GetString() ?? "", pois);
    }
}

/// <summary>Downloads images once to a disk cache and decodes them at the size they are shown.</summary>
public static class ImageCache
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteHelper", "cache");
    static readonly SemaphoreSlim Gate = new(4);
    static readonly Dictionary<string, WeakReference<BitmapSource>> Memory = [];

    static ImageCache()
    {
        Directory.CreateDirectory(Dir);
        Task.Run(() =>
        {
            try
            {
                foreach (var f in new DirectoryInfo(Dir).GetFiles())
                    if (f.LastAccessTimeUtc < DateTime.UtcNow.AddDays(-60)) f.Delete();
            }
            catch { }
        });
    }

    public static async Task<BitmapSource?> GetAsync(string? url, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        string key = url + "|" + decodeWidth;
        lock (Memory)
            if (Memory.TryGetValue(key, out var wr) && wr.TryGetTarget(out var hit)) return hit;

        var file = Path.Combine(Dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..20] + ".img");
        await Gate.WaitAsync();
        try
        {
            if (!File.Exists(file))
            {
                var bytes = await FortniteApi.Client.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(file + ".part", bytes);
                File.Move(file + ".part", file, overwrite: true);
            }
            var bmp = await Task.Run(() =>
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodeWidth > 0) b.DecodePixelWidth = decodeWidth;
                b.UriSource = new Uri(file);
                b.EndInit();
                b.Freeze();
                return (BitmapSource)b;
            });
            lock (Memory) Memory[key] = new WeakReference<BitmapSource>(bmp);
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warn($"Image {url}: {ex.Message}");
            try { File.Delete(file); } catch { }
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }
}
