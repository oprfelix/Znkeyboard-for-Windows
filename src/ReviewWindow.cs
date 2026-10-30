using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ZnKeyboard;

/// <summary>
/// The small card near the pointer: working, then the result (Enter replaces, Esc cancels,
/// Ctrl+C copies) or an error. It never takes focus: the app with the selection keeps it, so the
/// selection is still there to be replaced. Its keys come from a keyboard hook while it is shown;
/// a click anywhere else closes it.
/// </summary>
public sealed class ReviewWindow : Window
{
    private readonly TextBlock title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox language = new() { Margin = new Thickness(8, 0, 0, 0), MinWidth = 150, VerticalAlignment = VerticalAlignment.Center };
    private bool updatingLanguage;
    private readonly StackPanel body = new();
    private readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
    private string? result;
    private Native.HookProc? keyboardProc, mouseProc; // Kept alive while the hooks use them.
    private IntPtr keyboardHook, mouseHook;

    public Action<string> OnReplace { get; set; } = _ => { };
    public Action OnOpenSettings { get; set; } = () => { };
    /// <summary>The user picked another target language in the header; translate again.</summary>
    public Action<TranslateLanguage> OnLanguageChange { get; set; } = _ => { };

    public ReviewWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
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
            Child = new StackPanel
            {
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10), Children = { title, language } },
                    body,
                    buttons,
                },
            },
        };
        language.SelectionChanged += (_, _) =>
        {
            if (!updatingLanguage && language.SelectedIndex >= 0) OnLanguageChange(Languages.All[language.SelectedIndex]);
        };
        Content = card;
        card.MouseLeftButtonDown += (_, _) => DragMove();
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
                Dispatcher.BeginInvoke(() => { result = null; Hide(); });
                return new IntPtr(1);
            }
            if (vk == 0x0D && hasResult && !language.IsDropDownOpen)
            {
                Dispatcher.BeginInvoke(Replace);
                return new IntPtr(1);
            }
            if (vk == 0x43 && hasResult && Native.IsDown(Native.VK_CONTROL) && !Native.IsDown(Native.VK_MENU))
            {
                Dispatcher.BeginInvoke(CopyResult);
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
            if (!Native.IsOwnWindowAt(point)) Dispatcher.BeginInvoke(() => { result = null; Hide(); });
        }
        return Native.CallNextHookEx(mouseHook, code, wParam, lParam);
    }

    /// <summary>[target] shows "AI 翻译为 [language ▾]" instead of a plain title.</summary>
    public void ShowLoading(string heading, TranslateLanguage? target = null)
    {
        result = null;
        title.Text = target == null ? heading : L.TranslateShort;
        updatingLanguage = true;
        language.Visibility = target == null ? Visibility.Collapsed : Visibility.Visible;
        language.ItemsSource = Languages.All.Select(l => l.Label).ToList();
        if (target != null) language.SelectedIndex = Languages.All.ToList().FindIndex(l => l.Code == target.Code);
        updatingLanguage = false;
        body.Children.Clear();
        body.Children.Add(new TextBlock { Text = L.Working, Foreground = Brushes.Gray });
        SetButtons((L.Cancel, Hide, false));
        if (!IsVisible)
        {
            PlaceAtCursor();
            Show();
        }
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
