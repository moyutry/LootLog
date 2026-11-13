using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FortniteHelper.Core;
using FortniteHelper.Services;
using FortniteHelper.Views.Controls;

namespace FortniteHelper.Views.Pages;

public partial class CrosshairPage : UserControl, IDisposable
{
    static readonly string[] SwatchColors =
    [
        "#FFFFFF", "#00FF00", "#7FFF00", "#DFFF00", "#FFFF00", "#00FFFF", "#FF00FF", "#FF0000",
        "#FFD23F", "#FF9A2E", "#B44CFF", "#2E9BFF", "#5FC234", "#2CE6FF", "#FF4D6D", "#000000",
    ];

    CrosshairSettings Cs => AppServices.Settings.Crosshair;

    public CrosshairPage()
    {
        InitializeComponent();
        DataContext = AppServices.Settings;
        ProfileList.ItemsSource = Cs.Profiles;
        BuildSwatches();
        BuildPresets();
        BuildSceneButtons();
        Cs.PropertyChanged += OnCrosshairSettings;
        SelectActive();
    }

    void OnCrosshairSettings(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CrosshairSettings.ActiveIndex)) SelectActive();
    }

    void SelectActive()
    {
        var p = Cs.Active;
        if (ProfileList.SelectedItem != p) ProfileList.SelectedItem = p;
        Editor.DataContext = p;
        Preview.Profile = p;
        ProfileName.Text = p.Name.ToUpperInvariant();
        HexBox.Text = p.Color;
        CodeBox.Text = ValorantCode.Export(p);
    }

    void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileList.SelectedItem is CrosshairProfile p)
        {
            int idx = Cs.Profiles.IndexOf(p);
            if (idx >= 0 && idx != Cs.ActiveIndex) Cs.ActiveIndex = idx;
            else SelectActive();
            AppServices.Overlays.UpdateCrosshair();
        }
    }

    void BuildSwatches()
    {
        foreach (var hex in SwatchColors)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var b = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(0, 0, 7, 7),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = hex,
            };
            b.MouseLeftButtonUp += (_, _) => { Cs.Active.Color = hex; HexBox.Text = hex; CodeBox.Text = ValorantCode.Export(Cs.Active); };
            Swatches.Children.Add(b);
        }
    }

    void BuildPresets()
    {
        foreach (var preset in CrosshairProfile.Presets())
        {
            var preview = new CrosshairPreview { Profile = preset, Zoom = 2, ShowScene = false, Width = 62, Height = 62, IsHitTestVisible = false };
            var tile = new Border
            {
                Width = 66,
                Height = 66,
                Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = new CornerRadius(10),
                Background = new LinearGradientBrush(Color.FromRgb(0x3C, 0x6E, 0xA8), Color.FromRgb(0x2A, 0x4A, 0x30), 90),
                BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = preset.Name,
                Child = preview,
                ClipToBounds = true,
            };
            tile.MouseEnter += (_, _) => tile.BorderBrush = (Brush)FindResource("Accent");
            tile.MouseLeave += (_, _) => tile.BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
            tile.MouseLeftButtonUp += (_, _) =>
            {
                Cs.Active.CopyFrom(preset);
                SelectActive();
                AppServices.ShowToast($"Applied \"{preset.Name}\"");
            };
            Presets.Children.Add(tile);
        }
    }

    void BuildSceneButtons()
    {
        foreach (var scene in Enum.GetValues<PreviewScene>())
        {
            var rb = new RadioButton { Content = scene.ToString(), Style = (Style)FindResource("Segment"), GroupName = "Scene", IsChecked = scene == PreviewScene.Sky, Padding = new Thickness(10, 4, 10, 4) };
            rb.Checked += (_, _) => Preview.Scene = scene;
            SceneButtons.Children.Add(rb);
        }
        foreach (int z in new[] { 1, 2, 3, 5 })
        {
            var rb = new RadioButton { Content = $"{z}x", Style = (Style)FindResource("Segment"), GroupName = "Zoom", IsChecked = z == 3, Padding = new Thickness(10, 4, 10, 4) };
            rb.Checked += (_, _) => Preview.Zoom = z;
            ZoomButtons.Children.Add(rb);
        }
    }

    void ApplyHex()
    {
        var text = HexBox.Text.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (Regex.IsMatch(text, "^#[0-9A-Fa-f]{6}$"))
        {
            Cs.Active.Color = text.ToUpperInvariant();
            CodeBox.Text = ValorantCode.Export(Cs.Active);
        }
        HexBox.Text = Cs.Active.Color;
    }

    void HexBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ApplyHex(); }
    void HexBox_LostFocus(object sender, RoutedEventArgs e) => ApplyHex();

    void New_Click(object sender, RoutedEventArgs e)
    {
        var name = Dialogs.Prompt(Window.GetWindow(this), "New crosshair", "Name your new crosshair:", $"Crosshair {Cs.Profiles.Count + 1}");
        if (name is null) return;
        Cs.Profiles.Add(CrosshairProfile.Presets()[0].Clone(name));
        Cs.ActiveIndex = Cs.Profiles.Count - 1;
    }

    void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        Cs.Profiles.Add(Cs.Active.Clone(Cs.Active.Name + " copy"));
        Cs.ActiveIndex = Cs.Profiles.Count - 1;
    }

    void Rename_Click(object sender, RoutedEventArgs e)
    {
        var name = Dialogs.Prompt(Window.GetWindow(this), "Rename", "New name:", Cs.Active.Name);
        if (name is null) return;
        Cs.Active.Name = name;
        ProfileList.Items.Refresh();
        SelectActive();
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Cs.Profiles.Count <= 1)
        {
            AppServices.ShowToast("You need at least one crosshair");
            return;
        }
        if (!Dialogs.Confirm(Window.GetWindow(this), "Delete crosshair?", $"\"{Cs.Active.Name}\" will be deleted.", "DELETE")) return;
        int idx = Cs.ActiveIndex;
        Cs.Profiles.RemoveAt(idx);
        Cs.ActiveIndex = Math.Max(0, idx - 1);
        SelectActive();
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var parsed = ValorantCode.Parse(CodeBox.Text);
            Cs.Active.CopyFrom(parsed);
            SelectActive();
            AppServices.ShowToast("Crosshair code applied!");
        }
        catch (FormatException ex)
        {
            Dialogs.Confirm(Window.GetWindow(this), "Can't read that code", ex.Message + "\n\nA Valorant code looks like: 0;P;c;5;h;0;0l;4;0o;2;0a;1;0f;0;1b;0");
        }
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        var code = ValorantCode.Export(Cs.Active);
        CodeBox.Text = code;
        try { Clipboard.SetText(code); AppServices.ShowToast("Code copied to clipboard"); }
        catch { AppServices.ShowToast("Couldn't access the clipboard"); }
    }

    public void Dispose() => Cs.PropertyChanged -= OnCrosshairSettings;
}
