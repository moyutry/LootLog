using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using FortniteHelper.Core;
using FortniteHelper.Infrastructure;

namespace FortniteHelper.Services;

/// <summary>Loads settings, watches every change (including crosshair profiles) and saves with a short debounce.</summary>
public sealed class SettingsService
{
    const string FileName = "settings.json";
    readonly DispatcherTimer _saveTimer;

    public AppSettings Settings { get; }
    public event Action? CrosshairChanged;

    public SettingsService()
    {
        Settings = JsonStore.Load<AppSettings>(FileName);
        if (!Settings.General.LegacyImported)
        {
            ImportLegacyProfiles();
            Settings.General.LegacyImported = true;
        }
        if (Settings.Version < 3)
        {
            // v3: the chest hotkeys are gone; the two that remain start unbound and only work inside Fortnite.
            Settings.Hotkeys.ToggleCrosshair = KeyBind.None;
            Settings.Hotkeys.ToggleHud = KeyBind.None;
            Settings.Version = 3;
        }
        if (Settings.Version < 4)
        {
            // v4: a bigger area around the crosshair (prompts were missed at the edges) and enough time for hold-to-search.
            var d = Settings.Detection;
            d.RegionWidth = 0.40;
            d.RegionHeight = 0.50;
            d.RegionOffsetY = 0;
            d.ConfirmWindowMs = 3500;
            d.CooldownMs = 700;
            Settings.Version = 4;
        }
        if (Settings.Crosshair.Profiles.Count == 0)
            Settings.Crosshair.Profiles.Add(CrosshairProfile.Presets()[0].Clone("My Crosshair"));
        _ = Settings.Crosshair.Active; // clamps the index

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };

        foreach (var section in Settings.Sections()) section.PropertyChanged += OnChanged;
        Settings.Crosshair.Profiles.CollectionChanged += OnProfilesChanged;
        foreach (var p in Settings.Crosshair.Profiles) p.PropertyChanged += OnProfileChanged;
        SaveNow();
    }

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == Settings.Crosshair) CrosshairChanged?.Invoke();
        QueueSave();
    }

    void OnProfileChanged(object? sender, PropertyChangedEventArgs e)
    {
        CrosshairChanged?.Invoke();
        QueueSave();
    }

    void OnProfilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (CrosshairProfile p in e.OldItems) p.PropertyChanged -= OnProfileChanged;
        if (e.NewItems is not null) foreach (CrosshairProfile p in e.NewItems) p.PropertyChanged += OnProfileChanged;
        CrosshairChanged?.Invoke();
        QueueSave();
    }

    public void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        try { JsonStore.Save(FileName, Settings); }
        catch (Exception ex) { Log.Error("Saving settings failed", ex); }
    }

    /// <summary>Brings over profiles from the old Python version (%APPDATA%\ProCrosshairApp).</summary>
    void ImportLegacyProfiles()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProCrosshairApp", "crosshair_profiles.json");
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("profiles", out var profiles)) return;

            int added = 0;
            foreach (var p in profiles.EnumerateArray())
            {
                double D(string k, double def) => p.TryGetProperty(k, out var v) && v.TryGetDouble(out var d) ? d : def;
                bool B(string k, bool def) => p.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;
                int I(string k, int def) => (int)Math.Round(D(k, def));

                var prof = new CrosshairProfile
                {
                    Name = p.TryGetProperty("name", out var n) ? n.GetString() ?? "Imported" : "Imported",
                    Color = p.TryGetProperty("color", out var c) && c.GetString() is { Length: 7 } col ? col.ToUpperInvariant() : "#00FFFF",
                    Outlines = B("outlines_on", true), OutlineOpacity = D("outline_opacity", 0.5), OutlineThickness = Math.Max(1, I("outline_thickness", 1)),
                    CenterDot = B("dot_on", false), DotOpacity = D("dot_opacity", 1), DotThickness = Math.Max(1, I("dot_thickness", 2)),
                    InnerLines = B("inner_on", true), InnerOpacity = D("inner_opacity", 0.8), InnerLength = I("inner_length", 6),
                    InnerThickness = I("inner_thickness", 2), InnerOffset = I("inner_offset", 3),
                    OuterLines = B("outer_on", false), OuterOpacity = D("outer_opacity", 0.35), OuterLength = I("outer_length", 2),
                    OuterThickness = I("outer_thickness", 2), OuterOffset = I("outer_offset", 10),
                };
                Settings.Crosshair.Profiles.Add(prof);
                added++;

                if (added == 1 && p.TryGetProperty("crosshair_mode", out var mode))
                {
                    Settings.Crosshair.Mode = mode.GetString() switch
                    {
                        "Off" => CrosshairMode.Off,
                        "On" => CrosshairMode.Always,
                        _ => CrosshairMode.FortniteOnly,
                    };
                }
            }
            if (doc.RootElement.TryGetProperty("current_index", out var idx) && idx.TryGetInt32(out var i))
                Settings.Crosshair.ActiveIndex = Math.Clamp(i, 0, Math.Max(0, Settings.Crosshair.Profiles.Count - 1));
            Log.Info($"Imported {added} crosshair profile(s) from the old app.");
        }
        catch (Exception ex)
        {
            Log.Warn("Legacy profile import failed: " + ex.Message);
        }
    }
}
