using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace ZnKeyboard;

/// <summary>
/// The Fluent card near the pointer: working, then the result (Enter replaces, Esc closes, Ctrl+C
/// copies) or an error. For screenshot translation it shows the recognised text above the
/// translation, and Enter copies instead. It never takes focus: the app with the selection keeps
/// it, so the selection is still there to be replaced. Its keys come from a keyboard hook while it
/// is shown; a click anywhere else closes it.
/// </summary>
public sealed class ReviewWindow : Window
{
    private readonly SymbolIcon icon = new() { FontSize = 18, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox language = new() { Margin = new Thickness(8, 0, 0, 0), MinWidth = 140, VerticalAlignment = VerticalAlignment.Center };
    private bool updatingLanguage;
    private readonly StackPanel body = new();
    private readonly StackPanel leftButtons = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel rightButtons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Border card;
    private RewriteMode mode = RewriteMode.Polish;
    private string? result;
    private string? original;
    private Native.HookProc? keyboardProc, mouseProc; // Kept alive while the hooks use them.
    private IntPtr keyboardHook, mouseHook;

    public Action<string> OnReplace { get; set; } = _ => { };
    public Action OnOpenSettings { get; set; } = () => { };
    /// <summary>The user picked another target language in the header; translate again.</summary>
    public Action<TranslateLanguage> OnLanguageChange { get; set; } = _ => { };
    /// <summary>Run the same text again (↻, or Try Again after an error).</summary>
    public Action OnRetry { get; set; } = () => { };

    public ReviewWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI");
        FontSize = 14;

        var close = new Button
        {
            Icon = new SymbolIcon(SymbolRegular.Dismiss16),
            Appearance = ControlAppearance.Transparent,
            Padding = new Thickness(6),
            ToolTip = L.CloseHint,
            VerticalAlignment = VerticalAlignment.Center,
        };
        close.Click += (_, _) => Close(clearResult: true);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = false };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(icon);
        header.Children.Add(title);
        header.Children.Add(language);

        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.Children.Add(leftButtons);
        footer.Children.Add(rightButtons);

        card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 14, 14, 16),
            Margin = new Thickness(16, 12, 16, 24),
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = 0.22 },
            RenderTransformOrigin = new Point(0.5, 0),
            RenderTransform = new ScaleTransform(1, 1),
            Child = new StackPanel { Children = { header, body, footer } },
        };
        card.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorFlyoutBrush");
        card.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        icon.SetResourceReference(ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        Content = card;

        language.SelectionChanged += (_, _) =>
        {
            if (!updatingLanguage && language.SelectedIndex >= 0) OnLanguageChange(Languages.All[language.SelectedIndex]);
        };
        card.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is Border or StackPanel or DockPanel or Grid) DragMove(); };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = Native.GetWindowLongPtr(handle, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(handle, Native.GWL_EXSTYLE, new IntPtr(style | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) InstallHooks(); else RemoveHooks(); };
    }

    private void InstallHooks()
    {
        if (keyboardHook != IntPtr.Zero) return;
        keyboardProc = KeyboardHook;
        mouseProc = MouseHook;
        var module = Native.GetModuleHandle(null);
        keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyboardProc, module, 0);
        mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, module, 0);
    }

    private void RemoveHooks()
    {
        if (keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboardHook);
        if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
        keyboardHook = mouseHook = IntPtr.Zero;
    }

    /// <summary>Takes Enter, Esc and Ctrl+C for the card (so they don't reach the app); lets everything else through.</summary>
    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam.ToInt32() is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN)
        {
            var vk = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam).VkCode;
            var hasResult = result != null;
            if (vk == 0x1B)
            {
                Dispatcher.BeginInvoke(() => Close(clearResult: true));
                return new IntPtr(1);
            }
            if (vk == 0x0D && hasResult && !language.IsDropDownOpen)
            {
                Dispatcher.BeginInvoke(Primary);
                return new IntPtr(1);
            }
            if (vk == 0x43 && hasResult && Native.IsDown(Native.VK_CONTROL) && !Native.IsDown(Native.VK_MENU))
            {
                Dispatcher.BeginInvoke(() => CopyAndClose(result));
                return new IntPtr(1);
            }
        }
        return Native.CallNextHookEx(keyboardHook, code, wParam, lParam);
    }

    /// <summary>A click outside the card (and its language list) closes it; clicking into the text would move the selection anyway.</summary>
    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam.ToInt32() is Native.WM_LBUTTONDOWN or 0x204 /* WM_RBUTTONDOWN */)
        {
            var point = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam).Point;
            if (!Native.IsOwnWindowAt(point)) Dispatcher.BeginInvoke(() => Close(clearResult: true));
        }
        return Native.CallNextHookEx(mouseHook, code, wParam, lParam);
    }

    /// <summary>Back to the placeholder for <paramref name="next"/>; the card stays where the user put it.</summary>
    public void ShowLoading(RewriteMode next)
    {
        mode = next;
        result = null;
        original = null;
        var target = next.Target;
        icon.Symbol = next.IsRead ? SymbolRegular.ScanText24 : target == null ? SymbolRegular.Sparkle24 : SymbolRegular.Translate24;
        title.Text = next.IsRead ? L.ScreenshotTranslateTo : target == null ? L.Polish : L.TranslateShort;
        updatingLanguage = true;
        language.Visibility = target == null ? Visibility.Collapsed : Visibility.Visible;
        language.ItemsSource = Languages.All.Select(l => l.Label).ToList();
        if (target != null) language.SelectedIndex = Languages.All.ToList().FindIndex(l => l.Code == target.Code);
        language.IsEnabled = false;
        updatingLanguage = false;

        body.Children.Clear();
        body.Children.Add(Skeleton());
        leftButtons.Children.Clear();
        rightButtons.Children.Clear();
        Appear();
    }

    /// <summary><paramref name="recognised"/>: the screenshot's text, shown above the translation.</summary>
    public void ShowResult(string text, string? recognised = null)
    {
        result = text;
        original = recognised;
        language.IsEnabled = true;
        body.Children.Clear();
        if (recognised != null)
        {
            body.Children.Add(Caption(L.OriginalText));
            body.Children.Add(ReadOnlyText(recognised, maxHeight: 120, secondary: true));
            var divider = new Border { Height = 1, Margin = new Thickness(0, 10, 0, 10) };
            divider.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
            body.Children.Add(divider);
        }
        body.Children.Add(ReadOnlyText(text, maxHeight: 360, secondary: false));

        leftButtons.Children.Clear();
        rightButtons.Children.Clear();
        if (mode.IsRead)
        {
            leftButtons.Children.Add(TextButton(L.CopyOriginal, SymbolRegular.TextQuote20, () => CopyAndClose(original)));
            leftButtons.Children.Add(IconButton(SymbolRegular.ArrowClockwise20, L.Regenerate, () => OnRetry()));
            rightButtons.Children.Add(PrimaryButton(L.CopyTranslation + "   ↵", Primary));
        }
        else
        {
            leftButtons.Children.Add(IconButton(SymbolRegular.Copy20, L.CopyHint, () => CopyAndClose(result)));
            leftButtons.Children.Add(IconButton(SymbolRegular.ArrowClockwise20, L.Regenerate, () => OnRetry()));
            rightButtons.Children.Add(PrimaryButton(L.Replace + "   ↵", Primary));
        }
        Appear();
    }

    public void ShowError(string message, bool offerSettings, bool canRetry = false)
    {
        result = null;
        language.IsEnabled = true;
        body.Children.Clear();
        var warning = new SymbolIcon(SymbolRegular.Warning20) { FontSize = 18, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        warning.SetResourceReference(ForegroundProperty, "SystemFillColorCautionBrush");
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var row = new DockPanel();
        row.Children.Add(warning);
        row.Children.Add(text);
        body.Children.Add(row);

        leftButtons.Children.Clear();
        rightButtons.Children.Clear();
        if (canRetry) rightButtons.Children.Add(TextButton(L.Retry, SymbolRegular.ArrowClockwise20, () => OnRetry()));
        if (offerSettings) rightButtons.Children.Add(PrimaryButton(L.OpenSettings, () => { Close(clearResult: true); OnOpenSettings(); }));
        Appear();
    }

    public void Close(bool clearResult)
    {
        if (clearResult) result = null;
        Hide();
    }

    private void Primary()
    {
        if (result is not { } text) return;
        if (mode.IsRead)
        {
            CopyAndClose(text);
            return;
        }
        result = null;
        Hide();
        OnReplace(text);
    }

    private void CopyAndClose(string? text)
    {
        if (text != null) TextBridge.TrySetClipboard(() => Clipboard.SetText(text));
        Close(clearResult: true);
    }

    /// <summary>Shows the card near the pointer with a short fade and grow; already visible, it only re-lays out.</summary>
    private void Appear()
    {
        if (IsVisible) return;
        PlaceAtCursor();
        card.Opacity = 0;
        var scale = (ScaleTransform)card.RenderTransform;
        scale.ScaleX = scale.ScaleY = 0.96;
        Show();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
    }

    /// <summary>Three pulsing placeholder lines while the AI works.</summary>
    private static UIElement Skeleton()
    {
        var lines = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        foreach (var (width, delay) in new[] { (1.0, 0), (0.9, 120), (0.6, 240) })
        {
            var bar = new Border { Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Left };
            bar.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
            bar.Loaded += (_, _) => bar.Width = Math.Max(40, (bar.Parent as FrameworkElement)?.ActualWidth * width ?? 300);
            bar.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(700))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(delay),
            });
            lines.Children.Add(bar);
        }
        lines.SetValue(AutomationProperties.NameProperty, L.Working);
        return lines;
    }

    private static TextBlock Caption(string text)
    {
        var caption = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        return caption;
    }

    /// <summary>Selectable text without the Fluent text box frame.</summary>
    private static UIElement ReadOnlyText(string text, double maxHeight, bool secondary)
    {
        var box = new TextBox
        {
            Style = new Style(typeof(TextBox)),
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            FontSize = secondary ? 13 : 14,
        };
        box.SetResourceReference(ForegroundProperty, secondary ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");
        box.SetResourceReference(TextBox.CaretBrushProperty, "TextFillColorPrimaryBrush");
        return new ScrollViewer { MaxHeight = maxHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = box };
    }

    private static Button IconButton(SymbolRegular symbol, string tip, Action action)
    {
        var button = new Button { Icon = new SymbolIcon(symbol), ToolTip = tip, Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 0, 6, 0) };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button TextButton(string label, SymbolRegular symbol, Action action)
    {
        var button = new Button { Content = label, Icon = new SymbolIcon(symbol), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 6, 0) };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button PrimaryButton(string label, Action action)
    {
        var button = new Button { Content = label, Appearance = ControlAppearance.Primary, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(6, 0, 0, 0) };
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>Just below and right of the pointer, kept inside the monitor's work area.</summary>
    private void PlaceAtCursor()
    {
        var (cursor, scale, work) = Native.CursorAndMonitor();
        UpdateLayout();
        var height = ActualHeight > 0 ? ActualHeight : 200;
        var left = cursor.X / scale - 8;
        var top = cursor.Y / scale + 4;
        Left = Math.Min(Math.Max(left, work.Left / scale), work.Right / scale - Width);
        Top = Math.Min(Math.Max(top, work.Top / scale), work.Bottom / scale - height - 40);
    }
}
