using System.IO;
using System.Media;
using System.Windows.Threading;
using FortniteHelper.Core;
using FortniteHelper.Infrastructure;
using Microsoft.Win32;

namespace FortniteHelper.Services;

/// <summary>The two optional hotkeys (toggle crosshair / HUD). They only react while Fortnite is the active window,
/// and inputs are only observed, never blocked.</summary>
public sealed class HotkeyService
{
    readonly SettingsService _settings;
    readonly GameWatcher _watcher;
    readonly Dispatcher _ui;

    public HotkeyService(SettingsService settings, GameWatcher watcher, RawInputService input, Dispatcher ui)
    {
        _settings = settings;
        _watcher = watcher;
        _ui = ui;
        input.Input += OnInput;
    }

    /// <summary>While a key-binding box is listening, hotkeys are paused.</summary>
    public bool Suspended { get; set; }

    public event Action? ToggleCrosshair, ToggleHud;
    /// <summary>Every key/button press (UI thread) - used by binding boxes to capture mouse buttons.</summary>
    public event Action<InputEvent>? AnyPress;

    void OnInput(InputEvent e)
    {
        if (!e.Down) return;
        _ui.BeginInvoke(() =>
        {
            AnyPress?.Invoke(e);
            if (Suspended || !_watcher.IsFocused) return;
            var h = _settings.Settings.Hotkeys;
            bool M(KeyBind b) => b.Matches(e.Code, e.IsMouse, e.Ctrl, e.Shift, e.Alt);
            if (M(h.ToggleCrosshair)) ToggleCrosshair?.Invoke();
            if (M(h.ToggleHud)) ToggleHud?.Invoke();
        });
    }

    /// <summary>Mouse raw input is only needed when a binding uses a mouse button or "hide while aiming" is on.</summary>
    public bool NeedsMouse()
    {
        var s = _settings.Settings;
        return s.Detection.InteractKey.IsMouse || s.Crosshair.HideWhileAiming || s.Hotkeys.ToggleCrosshair.IsMouse || s.Hotkeys.ToggleHud.IsMouse;
    }
}

/// <summary>A short synthesized "loot" chime, generated in memory (no audio files shipped).</summary>
public sealed class SoundService
{
    byte[]? _wav;
    double _builtFor = -1;

    public void PlayChest(double volume)
    {
        if (volume <= 0) return;
        if (_wav is null || Math.Abs(_builtFor - volume) > 0.01) { _wav = BuildChime(volume); _builtFor = volume; }
        var data = _wav;
        Task.Run(() =>
        {
            try { using var ms = new MemoryStream(data); using var p = new SoundPlayer(ms); p.PlaySync(); }
            catch { /* no audio device */ }
        });
    }

    static byte[] BuildChime(double volume)
    {
        const int rate = 44100;
        double[] notes = [987.77, 1318.51, 1975.53]; // B5, E6, B6 - sparkly arpeggio
        int total = (int)(rate * 0.55);
        var samples = new short[total];
        for (int n = 0; n < notes.Length; n++)
        {
            int start = (int)(rate * 0.07 * n);
            for (int i = start; i < total; i++)
            {
                double t = (i - start) / (double)rate;
                double env = Math.Exp(-t * 7) * Math.Min(1, t * 400);
                double v = Math.Sin(2 * Math.PI * notes[n] * t) * 0.6 + Math.Sin(2 * Math.PI * notes[n] * 2 * t) * 0.15;
                samples[i] = (short)Math.Clamp(samples[i] + v * env * 9000 * volume, short.MinValue, short.MaxValue);
            }
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
        return ms.ToArray();
    }
}

/// <summary>"Start with Windows" via the per-user Run key.</summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "LootLog";
    const string LegacyName = "FortniteHelper";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key.GetValue(LegacyName) is not null) key.DeleteValue(LegacyName, false); // from before the rename
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (exe is null) return;
                key.SetValue(Name, $"\"{exe}\" --minimized");
            }
            else if (key.GetValue(Name) is not null)
            {
                key.DeleteValue(Name, false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Autostart: " + ex.Message);
        }
    }
}

public static class Format
{
    public static string Duration(TimeSpan? t)
    {
        if (t is not { } d) return "-";
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes:00}m";
        return $"{(int)d.TotalMinutes}:{d.Seconds:00}";
    }

    /// <summary>Human length of time: "1h 05m", "19m", "45s".</summary>
    public static string Long(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m" : $"{t.Seconds}s";

    public static string Clock(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    public static string Number(long n) => n >= 10_000 ? (n / 1000.0).ToString("0.#") + "K" : n.ToString("N0");

    public static string Ordinal(int n) => n switch
    {
        _ when n % 100 is 11 or 12 or 13 => n + "th",
        _ when n % 10 == 1 => n + "st",
        _ when n % 10 == 2 => n + "nd",
        _ when n % 10 == 3 => n + "rd",
        _ => n + "th",
    };

    public static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays}d ago";
        return utc.ToLocalTime().ToString("MMM d");
    }
}
