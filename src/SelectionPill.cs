using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace ZnKeyboard;

/// <summary>
/// Shows 润色 | AI 翻译为 [EN ▾] next to text the user selects in an editable field: after a mouse
/// drag, double or triple click (at the pointer), or Ctrl+A / Shift+arrows (under the selection,
/// where the app reports it). Typing, clicking elsewhere or switching windows hides it. The pill
/// never takes focus, so the selection stays where it is. Nothing is read or sent until a button
/// is clicked.
/// </summary>
public sealed class SelectionPill : IDisposable
{
    public Action OnPolish { get; set; } = () => { };
    public Action OnTranslate { get; set; } = () => { };
    /// <summary>True while the result card is up; the pill stays out of its way.</summary>
    public Func<bool> IsSuspended { get; set; } = () => false;

    private readonly Window window;
    private readonly TextBlock chipText = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
    private readonly Button chip;
    private readonly Native.HookProc mouseProc, keyboardProc; // Kept alive: the hooks call them.
    private readonly IntPtr mouseHook, keyboardHook;
    private readonly DispatcherTimer watch = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private Native.POINT downPoint;
    private uint lastUpTime;
    private Native.POINT lastUpPoint;
    private IntPtr sourceWindow;
    private int checkGeneration;

    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x23, 0x40, 0x80));

    public SelectionPill()
    {
        chip = new Button
        {
            Content = new Border { Background = Ink, CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1, 7, 2), Child = chipText },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 0, 4, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        chip.Click += (_, _) => ChooseLanguage();
        var polish = Flat("✨ " + L.Polish, () => { Hide(); OnPolish(); });
        var translate = Flat(L.TranslateShort, () => { Hide(); OnTranslate(); });

        window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 12,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xFB, 0xFB, 0xFD)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(4, 2, 4, 2),
                Margin = new Thickness(8),
                Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.2 },
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        polish,
                        new Border { Width = 1, Height = 14, Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), Margin = new Thickness(2, 0, 2, 0) },
                        translate,
                        chip,
                    },
                },
            },
        };
        // Clicking the pill must not take focus from the app whose text is selected.
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var style = Native.GetWindowLongPtr(handle, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(handle, Native.GWL_EXSTYLE, new IntPtr(style | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
        };

        mouseProc = MouseHook;
        keyboardProc = KeyboardHook;
        var module = Native.GetModuleHandle(null);
        mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, module, 0);
        keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyboardProc, module, 0);

        // Switching to another window ends it.
        watch.Tick += (_, _) =>
        {
            if (window.IsVisible && Native.GetForegroundWindow() != sourceWindow) Hide();
        };
    }

    public void Hide()
    {
        checkGeneration++;
        watch.Stop();
        if (window.IsVisible) window.Hide();
    }

    // Hooks run on the UI thread's message loop and must return fast: they only note and post.
    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = wParam.ToInt32();
            if (message is Native.WM_LBUTTONDOWN or Native.WM_LBUTTONUP)
            {
                var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                window.Dispatcher.BeginInvoke(() => OnMouse(message == Native.WM_LBUTTONDOWN, data.Point, data.Time));
            }
        }
        return Native.CallNextHookEx(mouseHook, code, wParam, lParam);
    }

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = wParam.ToInt32();
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            window.Dispatcher.BeginInvoke(() => OnKey(message is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN, data.VkCode));
        }
        return Native.CallNextHookEx(keyboardHook, code, wParam, lParam);
    }

    private void OnMouse(bool down, Native.POINT point, uint time)
    {
        if (window.IsVisible && IsOverPill(point)) return;
        if (down)
        {
            downPoint = point;
            Hide();
            return;
        }
        var dragged = Math.Abs(point.X - downPoint.X) + Math.Abs(point.Y - downPoint.Y) >= 6;
        var multiClick = time - lastUpTime <= Native.GetDoubleClickTime()
            && Math.Abs(point.X - lastUpPoint.X) + Math.Abs(point.Y - lastUpPoint.Y) <= 4;
        lastUpTime = time;
        lastUpPoint = point;
        if (dragged || multiClick) Check(point);
    }

    private static readonly uint[] Modifiers = [0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C];
    private static readonly uint[] ExtendKeys = [0x25, 0x26, 0x27, 0x28, 0x24, 0x23, 0x21, 0x22]; // arrows, Home, End, PgUp, PgDn

    private void OnKey(bool down, uint vk)
    {
        if (down)
        {
            if (!Modifiers.Contains(vk)) Hide();
            return;
        }
        var ctrlA = vk == 0x41 && Native.IsDown(Native.VK_CONTROL) && !Native.IsDown(Native.VK_MENU);
        var shiftExtend = ExtendKeys.Contains(vk) && Native.IsDown(Native.VK_SHIFT);
        if (ctrlA || shiftExtend) Check(null);
    }

    /// <summary>After the app has updated its selection, show the pill if there is one in an editable field.</summary>
    private async void Check(Native.POINT? pointer)
    {
        if (!Settings.Current.SelectionPill || IsSuspended()) return;
        var foreground = Native.GetForegroundWindow();
        if (foreground == IntPtr.Zero || Native.IsOwnWindow(foreground)) return;
        var generation = ++checkGeneration;
        await Task.Delay(150);
        var inspect = Task.Run(TextBridge.InspectFocusedSelection);
        if (await Task.WhenAny(inspect, Task.Delay(500)) != inspect || generation != checkGeneration) return;
        var (state, end) = inspect.Result;
        // Mouse selections in some apps report no selection at all; trust the gesture there.
        var show = state == TextBridge.SelectionState.Selected || (state == TextBridge.SelectionState.Unknown && pointer != null);
        if (!show || IsSuspended()) return;

        Native.POINT anchor;
        if (pointer is { } p) anchor = p;
        else if (end is { } rect && !rect.IsEmpty) anchor = new Native.POINT { X = (int)rect.Right, Y = (int)rect.Bottom };
        else return; // A keyboard selection with no reported position: nothing to anchor to.

        sourceWindow = foreground;
        chipText.Text = Settings.Current.Target.Code + " ▾";
        var scale = Native.ScaleAt(anchor);
        window.Left = anchor.X / scale - 4;
        window.Top = anchor.Y / scale + 2;
        window.Show();
        watch.Start();
    }

    private bool IsOverPill(Native.POINT point)
    {
        var scale = Native.ScaleAt(point);
        var x = point.X / scale;
        var y = point.Y / scale;
        return x >= window.Left && x <= window.Left + window.ActualWidth && y >= window.Top && y <= window.Top + window.ActualHeight;
    }

    private void ChooseLanguage()
    {
        var menu = new ContextMenu { PlacementTarget = chip, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var language in Languages.All)
        {
            var item = new MenuItem { Header = language.Label, IsCheckable = true, IsChecked = language.Code == Settings.Current.TranslateTarget };
            item.Click += (_, _) =>
            {
                Settings.Current.TranslateTarget = language.Code;
                Settings.Current.Save();
                chipText.Text = language.Code + " ▾";
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private static Button Flat(string label, Action action)
    {
        var button = new Button
        {
            Content = label,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4, 8, 4),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        button.Click += (_, _) => action();
        return button;
    }

    public void Dispose()
    {
        if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
        if (keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboardHook);
        window.Close();
    }
}
