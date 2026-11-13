using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FortniteHelper.Views;

/// <summary>Themed confirm / text-input dialogs (instead of the plain Windows message boxes).</summary>
public static class Dialogs
{
    public static bool Confirm(Window? owner, string title, string message, string okText = "OK")
    {
        bool result = false;
        var win = Build(owner, title, out var body, out var buttons);
        body.Children.Add(new TextBlock { Text = message, Style = (Style)Application.Current.FindResource("Body"), MaxWidth = 380 });
        var ok = new Button { Content = okText, Style = (Style)Application.Current.FindResource("PrimaryBtn"), Margin = new Thickness(10, 0, 0, 0), IsDefault = true };
        ok.Click += (_, _) => { result = true; win.Close(); };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => win.Close();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        win.ShowDialog();
        return result;
    }

    public static string? Prompt(Window? owner, string title, string label, string initial = "")
    {
        string? result = null;
        var win = Build(owner, title, out var body, out var buttons);
        body.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.FindResource("Body"), Margin = new Thickness(0, 0, 0, 8) });
        var box = new TextBox { Text = initial, MinWidth = 340 };
        body.Children.Add(box);
        var ok = new Button { Content = "OK", Style = (Style)Application.Current.FindResource("PrimaryBtn"), Margin = new Thickness(10, 0, 0, 0), IsDefault = true };
        ok.Click += (_, _) => { result = box.Text.Trim(); win.Close(); };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => win.Close();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        win.ShowDialog();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    static Window Build(Window? owner, string title, out StackPanel body, out StackPanel buttons)
    {
        var win = new Window
        {
            Owner = owner,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Title = title,
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
        };
        body = new StackPanel { Margin = new Thickness(0, 12, 0, 18) };
        buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), Style = (Style)Application.Current.FindResource("H2"), HorizontalAlignment = HorizontalAlignment.Left });
        root.Children.Add(body);
        root.Children.Add(buttons);
        var card = new Border
        {
            Margin = new Thickness(16),
            Padding = new Thickness(24, 20, 24, 20),
            CornerRadius = new CornerRadius(18),
            Background = new LinearGradientBrush(Color.FromRgb(0x22, 0x28, 0x66), Color.FromRgb(0x10, 0x12, 0x36), 90),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 4, Opacity = 0.6 },
            Child = root,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.94, 0.94),
        };
        card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
        win.Content = card;
        win.Loaded += (_, _) =>
        {
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
            ((ScaleTransform)card.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            ((ScaleTransform)card.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        };
        return win;
    }
}
