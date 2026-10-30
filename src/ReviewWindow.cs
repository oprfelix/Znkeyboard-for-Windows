using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ZnKeyboard;

/// <summary>
/// The small card near the pointer: working, then the result (Enter replaces, Esc cancels,
/// Ctrl+C copies) or an error.
/// </summary>
public sealed class ReviewWindow : Window
{
    private readonly TextBlock title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
    private readonly StackPanel body = new();
    private readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
    private string? result;

    public Action<string> OnReplace { get; set; } = _ => { };
    public Action OnOpenSettings { get; set; } = () => { };

    public ReviewWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 13;

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFB, 0xFB, 0xFD)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 14, 18, 14),
            Margin = new Thickness(14),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.22 },
            Child = new StackPanel { Children = { title, body, buttons } },
        };
        Content = card;
        card.MouseLeftButtonDown += (_, _) => DragMove();
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => { if (result == null) Hide(); };
    }

    public void ShowLoading(string heading)
    {
        result = null;
        title.Text = heading;
        body.Children.Clear();
        body.Children.Add(new TextBlock { Text = L.Working, Foreground = Brushes.Gray });
        SetButtons((L.Cancel, Hide, false));
        PlaceAtCursor();
        Show();
        Activate();
    }

    public void ShowResult(string text)
    {
        result = text;
        body.Children.Clear();
        body.Children.Add(new ScrollViewer
        {
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
            },
        });
        SetButtons((L.Copy, CopyResult, false), (L.Cancel, Hide, false), (L.Replace + "  ↵", Replace, true));
        Activate();
    }

    public void ShowError(string message, bool offerSettings)
    {
        result = null;
        body.Children.Clear();
        body.Children.Add(new TextBlock { Text = message, Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)), TextWrapping = TextWrapping.Wrap });
        if (offerSettings) SetButtons((L.OpenSettings, () => { Hide(); OnOpenSettings(); }, false), (L.Cancel, Hide, true));
        else SetButtons((L.Cancel, Hide, true));
        if (!IsVisible)
        {
            PlaceAtCursor();
            Show();
        }
        Activate();
    }

    private void Replace()
    {
        if (result is not { } text) return;
        result = null;
        Hide();
        OnReplace(text);
    }

    private void CopyResult()
    {
        if (result is { } text) TextBridge.TrySetClipboard(() => Clipboard.SetText(text));
        result = null;
        Hide();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { result = null; Hide(); e.Handled = true; }
        else if (e.Key == Key.Enter && result != null) { Replace(); e.Handled = true; }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && result != null) { CopyResult(); e.Handled = true; }
    }

    private void SetButtons(params (string label, Action action, bool primary)[] items)
    {
        buttons.Children.Clear();
        foreach (var (label, action, primary) in items)
        {
            var button = new Button
            {
                Content = label,
                MinWidth = 72,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = primary,
            };
            if (primary)
            {
                button.Background = new SolidColorBrush(Color.FromRgb(0x23, 0x40, 0x80));
                button.Foreground = Brushes.White;
            }
            button.Click += (_, _) => action();
            buttons.Children.Add(button);
        }
    }

    /// <summary>Just below and right of the pointer, kept inside the monitor's work area.</summary>
    private void PlaceAtCursor()
    {
        var (cursor, scale, work) = Native.CursorAndMonitor();
        UpdateLayout();
        var height = ActualHeight > 0 ? ActualHeight : 180;
        var left = cursor.X / scale + 8;
        var top = cursor.Y / scale + 12;
        Left = Math.Min(Math.Max(left, work.Left / scale), work.Right / scale - Width);
        Top = Math.Min(Math.Max(top, work.Top / scale), work.Bottom / scale - height - 40);
    }
}
