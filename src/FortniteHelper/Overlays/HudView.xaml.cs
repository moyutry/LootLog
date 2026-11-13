using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FortniteHelper.Core;

namespace FortniteHelper.Overlays;

public partial class HudView : UserControl
{
    HudModel? _model;

    public HudView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_model is not null) _model.ChestCounted -= OnCounted;
            _model = e.NewValue as HudModel;
            if (_model is not null) _model.ChestCounted += OnCounted;
        };
        Unloaded += (_, _) => { if (_model is not null) _model.ChestCounted -= OnCounted; };
        Loaded += (_, _) => { if (_model is not null) { _model.ChestCounted -= OnCounted; _model.ChestCounted += OnCounted; } };
    }

    public bool EditMode
    {
        set => EditFrame.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnCounted(ContainerKind kind, int delta)
    {
        if (delta <= 0 || _model?.Settings.Animate != true) return;
        foreach (var img in new[] { ChestCard, ChestCompact, ChestMinimal })
        {
            if (img.RenderTransform is not ScaleTransform st || st.IsFrozen) img.RenderTransform = st = new ScaleTransform();
            var pop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(700) };
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.38, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700)), new ElasticEase { Oscillations = 2, Springiness = 5, EasingMode = EasingMode.EaseOut }));
            st.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }

        foreach (var glow in new UIElement[] { GlowCard, GlowCompact })
        {
            var flash = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1100) };
            flash.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100))));
            flash.KeyFrames.Add(new EasingDoubleKeyFrame(0.33, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100)), new CubicEase { EasingMode = EasingMode.EaseIn }));
            glow.BeginAnimation(OpacityProperty, flash);
        }

        PlusCard.Text = "+1";
        if (PlusCard.RenderTransform is not TranslateTransform tt || tt.IsFrozen) PlusCard.RenderTransform = tt = new TranslateTransform();
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, -26, TimeSpan.FromMilliseconds(900)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(900) };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))));
        PlusCard.BeginAnimation(OpacityProperty, fade);
    }
}

/// <summary>The HUD window: click-through while playing, draggable in edit mode.</summary>
public sealed class HudOverlay : OverlayWindow
{
    public HudView View { get; } = new();

    public event Action? Dragged;

    public HudOverlay(HudModel model)
    {
        Title = "LootLog HUD";
        SizeToContent = SizeToContent.WidthAndHeight;
        Width = double.NaN;
        Height = double.NaN;
        View.DataContext = model;
        View.Margin = new Thickness(10, 10, 10, 26);
        Content = View;
        MouseLeftButtonDown += (_, e) =>
        {
            if (ClickThrough) return;
            try { DragMove(); } catch (InvalidOperationException) { }
            Dragged?.Invoke();
        };
    }

    public void SetEditMode(bool on)
    {
        View.EditMode = on;
        ClickThrough = !on;
        Cursor = on ? System.Windows.Input.Cursors.SizeAll : null;
    }
}
