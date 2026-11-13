using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FortniteHelper.Infrastructure;
using FortniteHelper.Services;

namespace FortniteHelper.Views.Controls;

/// <summary>A 24x24 line icon drawn from a Geometry resource, colored by Foreground.</summary>
public class Icon : Control
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(Icon));
    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(Icon), new PropertyMetadata(2.0));
    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(Icon));

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
}

/// <summary>A number that counts up/down smoothly when its value changes.</summary>
public class AnimatedNumber : TextBlock
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(AnimatedNumber),
        new PropertyMetadata(0.0, (d, e) => ((AnimatedNumber)d).Animate((double)e.NewValue)));
    static readonly DependencyProperty ShownProperty = DependencyProperty.Register("Shown", typeof(double), typeof(AnimatedNumber),
        new PropertyMetadata(0.0, (d, e) => ((AnimatedNumber)d).UpdateText()));
    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(nameof(Format), typeof(string), typeof(AnimatedNumber),
        new PropertyMetadata("N0", (d, _) => ((AnimatedNumber)d).UpdateText()));

    bool _first = true;

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }

    public AnimatedNumber() => Text = "0";

    void Animate(double to)
    {
        if (_first || !IsLoaded)
        {
            _first = false;
            BeginAnimation(ShownProperty, null);
            SetValue(ShownProperty, to);
            return;
        }
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(Math.Abs(to - (double)GetValue(ShownProperty)) < 3 ? 350 : 750))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(ShownProperty, anim);
    }

    void UpdateText()
    {
        double v = (double)GetValue(ShownProperty);
        Text = Format == "N0" ? Math.Round(v).ToString("N0", CultureInfo.CurrentCulture) : v.ToString(Format, CultureInfo.CurrentCulture);
    }
}

public sealed record BarPoint(string Label, double Value, string? Tip = null);

/// <summary>Simple animated bar chart (rounded bars with a gradient, value on hover).</summary>
public class BarChart : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(IReadOnlyList<BarPoint>), typeof(BarChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((BarChart)d).Grow()));
    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(nameof(BarBrush), typeof(Brush), typeof(BarChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    static readonly DependencyProperty ProgressProperty = DependencyProperty.Register("Progress", typeof(double), typeof(BarChart),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    int _hover = -1;
    static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public IReadOnlyList<BarPoint>? Items { get => (IReadOnlyList<BarPoint>?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public Brush? BarBrush { get => (Brush?)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }

    public BarChart()
    {
        MouseMove += (_, e) => { int h = HitIndex(e.GetPosition(this).X); if (h != _hover) { _hover = h; InvalidateVisual(); } };
        MouseLeave += (_, _) => { _hover = -1; InvalidateVisual(); };
        Loaded += (_, _) => Grow();
    }

    void Grow()
    {
        if (!IsLoaded) return;
        BeginAnimation(ProgressProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(650)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    int HitIndex(double x)
    {
        var items = Items;
        if (items is null || items.Count == 0) return -1;
        double slot = ActualWidth / items.Count;
        return Math.Clamp((int)(x / slot), 0, items.Count - 1);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var items = Items;
        if (items is null || items.Count == 0) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double max = Math.Max(1, items.Max(i => i.Value));
        double labelH = 18, valueH = 18;
        double chartH = Math.Max(10, ActualHeight - labelH - valueH);
        double slot = ActualWidth / items.Count;
        double barW = Math.Min(34, slot * 0.62);
        double progress = (double)GetValue(ProgressProperty);
        var brush = BarBrush ?? new LinearGradientBrush(Color.FromRgb(0x2C, 0xE6, 0xFF), Color.FromRgb(0x8B, 0x5C, 0xFF), 90);
        var dim = new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255));
        var labelBrush = new SolidColorBrush(Color.FromRgb(0x6E, 0x75, 0xB5));
        var valueBrush = Brushes.White;

        for (int i = 0; i < items.Count; i++)
        {
            double x = i * slot + (slot - barW) / 2;
            double h = Math.Max(items[i].Value > 0 ? 4 : 0, items[i].Value / max * chartH * progress);
            var track = new Rect(x, valueH, barW, chartH);
            dc.DrawRoundedRectangle(dim, null, track, 6, 6);
            if (h > 0)
            {
                var bar = new Rect(x, valueH + chartH - h, barW, h);
                dc.PushOpacity(_hover < 0 || _hover == i ? 1 : 0.55);
                dc.DrawRoundedRectangle(brush, null, bar, 6, 6);
                dc.Pop();
            }
            bool showValue = _hover == i || (items.Count <= 14 && items[i].Value > 0);
            if (showValue)
            {
                var text = items[i].Tip ?? items[i].Value.ToString("0.#", CultureInfo.CurrentCulture);
                var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11.5, _hover == i ? valueBrush : labelBrush, dpi);
                dc.DrawText(ft, new Point(x + barW / 2 - ft.Width / 2, Math.Max(0, valueH + chartH - h - ft.Height - 2)));
            }
            int labelEvery = Math.Max(1, (int)Math.Ceiling(38 / slot));
            if (i % labelEvery == 0)
            {
                var lt = new FormattedText(items[i].Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10.5, labelBrush, dpi);
                dc.DrawText(lt, new Point(x + barW / 2 - lt.Width / 2, valueH + chartH + 3));
            }
        }
    }
}

/// <summary>Click, then press any key or mouse button to bind it. Esc cancels, Backspace/Delete clears.</summary>
public class KeyBindingBox : Button
{
    public static readonly DependencyProperty BindingValueProperty = DependencyProperty.Register(nameof(BindingValue), typeof(KeyBind), typeof(KeyBindingBox),
        new FrameworkPropertyMetadata(KeyBind.None, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((KeyBindingBox)d).Refresh()));
    public static readonly DependencyProperty AllowMouseProperty = DependencyProperty.Register(nameof(AllowMouse), typeof(bool), typeof(KeyBindingBox), new PropertyMetadata(true));

    bool _listening;
    DateTime _listenStart;

    public KeyBind BindingValue { get => (KeyBind)GetValue(BindingValueProperty); set => SetValue(BindingValueProperty, value); }
    public bool AllowMouse { get => (bool)GetValue(AllowMouseProperty); set => SetValue(AllowMouseProperty, value); }

    public KeyBindingBox()
    {
        MinWidth = 150;
        Click += (_, _) => StartListening();
        Loaded += (_, _) => Refresh();
        LostKeyboardFocus += (_, _) => StopListening();
        Unloaded += (_, _) => StopListening();
    }

    void Refresh() => Content = _listening ? "Press a key or button..." : BindingValue.Display;

    void StartListening()
    {
        if (_listening) return;
        _listening = true;
        _listenStart = DateTime.UtcNow;
        AppServices.Hotkeys.Suspended = true;
        AppServices.Hotkeys.AnyPress += OnGlobalPress;
        AppServices.Input.SetMouseNeeded(true);
        Focus();
        Refresh();
    }

    void StopListening()
    {
        if (!_listening) return;
        _listening = false;
        AppServices.Hotkeys.Suspended = false;
        AppServices.Hotkeys.AnyPress -= OnGlobalPress;
        AppServices.RefreshMouseNeeds();
        Refresh();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_listening) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { StopListening(); return; }
        if (key is Key.Back or Key.Delete) { BindingValue = KeyBind.None; StopListening(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt) return; // wait for the real key
        var mods = Keyboard.Modifiers;
        BindingValue = KeyBind.Key(key, mods.HasFlag(ModifierKeys.Control), mods.HasFlag(ModifierKeys.Shift), mods.HasFlag(ModifierKeys.Alt));
        StopListening();
    }

    void OnGlobalPress(InputEvent e)
    {
        // Keyboard keys come through OnPreviewKeyDown (we have focus); here we only take mouse buttons.
        if (!_listening || !e.IsMouse || !AllowMouse) return;
        if ((DateTime.UtcNow - _listenStart).TotalMilliseconds < 250) return; // the click that started listening
        var code = (MouseCode)e.Code;
        if (code == MouseCode.Left) return; // left click is never a sensible binding
        BindingValue = KeyBind.Mouse(code);
        StopListening();
    }
}

/// <summary>Label + live value + slider, bound two-way to <see cref="Value"/>.</summary>
public class SliderRow : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(SliderRow),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((SliderRow)d).UpdateText()));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(SliderRow),
        new PropertyMetadata("", (d, e) => ((SliderRow)d)._label.Text = (string)e.NewValue));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(SliderRow),
        new PropertyMetadata(0.0, (d, e) => ((SliderRow)d)._slider.Minimum = (double)e.NewValue));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(SliderRow),
        new PropertyMetadata(100.0, (d, e) => ((SliderRow)d)._slider.Maximum = (double)e.NewValue));
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(double), typeof(SliderRow),
        new PropertyMetadata(1.0, (d, e) => { var s = ((SliderRow)d)._slider; s.TickFrequency = (double)e.NewValue; s.SmallChange = (double)e.NewValue; }));
    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(nameof(Suffix), typeof(string), typeof(SliderRow),
        new PropertyMetadata("", (d, _) => ((SliderRow)d).UpdateText()));
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(nameof(Percent), typeof(bool), typeof(SliderRow),
        new PropertyMetadata(false, (d, _) => ((SliderRow)d).UpdateText()));

    readonly TextBlock _label = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _value = new() { FontSize = 12.5, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    readonly Slider _slider = new() { IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(0, 2, 0, 0) };

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }
    /// <summary>Shows a 0..1 value as 0-100%.</summary>
    public bool Percent { get => (bool)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }

    public SliderRow()
    {
        Margin = new Thickness(0, 4, 0, 8);
        _label.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        _value.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        _slider.Minimum = 0;
        _slider.Maximum = 100;
        _slider.SetBinding(RangeBase.ValueProperty, new Binding(nameof(Value)) { Source = this, Mode = BindingMode.TwoWay });
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.Children.Add(_label);
        g.Children.Add(_value);
        Grid.SetRow(_slider, 1);
        g.Children.Add(_slider);
        Content = g;
        UpdateText();
    }

    void UpdateText()
    {
        double v = Value;
        _value.Text = Percent ? $"{Math.Round(v * 100)}%" : (Step < 1 ? v.ToString("0.##", CultureInfo.CurrentCulture) : Math.Round(v).ToString(CultureInfo.CurrentCulture)) + Suffix;
    }
}

// ---------------- Converters ----------------
public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c) => (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is Visibility.Visible ^ Invert;
}

public sealed class NullToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c) => (value is null || value as string == "") ^ Invert ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Binds an enum to RadioButtons: IsChecked="{Binding Mode, Converter={StaticResource EnumEq}, ConverterParameter=Auto}".</summary>
public sealed class EnumEquals : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value?.ToString() == p?.ToString();
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is true && p is string s ? Enum.Parse(t, s) : Binding.DoNothing;
}

public sealed class ColorToBrush : IValueConverter
{
    public static ColorToBrush Instance { get; } = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value is Color col ? new SolidColorBrush(col) : Brushes.Transparent;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class EnumToVisibility : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value?.ToString() == p?.ToString() ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Multiplies a 0..1 value by 100 for percentage sliders.</summary>
public sealed class Percent : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is double d ? Math.Round(d * 100) : 0.0;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is double d ? d / 100.0 : 0.0;
}

public sealed class HexToBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value as string ?? "#FFFFFF")); }
        catch { return Brushes.White; }
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
