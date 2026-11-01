using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBlock = System.Windows.Controls.TextBlock;

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
    private readonly TextBlock chipText = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button chip;
    private readonly Button polishButton, translateButton;
    private readonly Native.HookProc mouseProc, keyboardProc; // Kept alive: the hooks call them.
    private readonly IntPtr mouseHook, keyboardHook;
    private readonly DispatcherTimer watch = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private Native.POINT downPoint;
    // Windows skips a low-level hook when the UI thread is too busy to answer in time, so a button
    // up can arrive without its down; it is then measured against nothing and ignored.
    private bool pressed;
    private bool pressedOnOwnWindow;
    private uint lastClickTime;
    private Native.POINT lastClickPoint;
    private IntPtr sourceWindow;
    private int checkGeneration;

    public SelectionPill()
    {
        // The target language as an accent chip, like the Mac and Android versions.
        chipText.SetResourceReference(TextBlock.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
        var chevron = new SymbolIcon(SymbolRegular.ChevronDown12) { FontSize = 9, Margin = new Thickness(3, 1, 0, 0) };
        chevron.SetResourceReference(Control.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
        var chipFace = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 2, 7, 3),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { chipText, chevron } },
        };
        chipFace.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
        chip = new Button
        {
            Content = chipFace,
            Appearance = ControlAppearance.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 0, 4, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = L.TranslateMenu,
        };
        chip.Click += (_, _) => ChooseLanguage();
        var polish = polishButton = Flat(L.Polish, SymbolRegular.Sparkle16, () => { Hide(); OnPolish(); });
        var translate = translateButton = Flat(L.TranslateShort, SymbolRegular.Translate16, () => { Hide(); OnTranslate(); });
        var divider = new Border { Width = 1, Height = 16, Margin = new Thickness(2, 0, 2, 0) };
        divider.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        var face = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(4, 3, 4, 3),
            Margin = new Thickness(10, 8, 10, 14),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Direction = 270, Opacity = 0.18 },
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { polish, divider, translate, chip },
            },
        };
        face.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        face.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorFlyoutBrush");
        face.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");

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
            FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI"),
            FontSize = 12,
            Content = face,
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
                // Whose window it is must be asked now: by the time OnMouse runs, a click may already
                // have hidden the pill and uncovered the app below.
                var own = Native.IsOwnWindowAt(data.Point);
                window.Dispatcher.BeginInvoke(() => OnMouse(message == Native.WM_LBUTTONDOWN, data.Point, data.Time, own));
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

    private void OnMouse(bool down, Native.POINT point, uint time, bool own)
    {
        if (down)
        {
            pressed = true;
            pressedOnOwnWindow = own;
            downPoint = point;
            // Clicks on the pill, its language list or the result card are ours to handle.
            if (!own) Hide();
            return;
        }
        if (!pressed)
        {
            DebugLog.Note("mouse up without its down (hook skipped), ignored");
            return;
        }
        pressed = false;
        // A press that started or ended on one of ours (the pill, the card, settings) selects nothing elsewhere.
        if (own || pressedOnOwnWindow) return;
        // Hook points are physical pixels; measure in DIPs so a click that wobbles a few pixels on a
        // high-DPI screen or a touchpad isn't taken for a drag.
        var scale = Native.ScaleAt(point);
        var moved = Math.Max(Math.Abs(point.X - downPoint.X), Math.Abs(point.Y - downPoint.Y)) / scale;
        var dragged = moved >= DragDistance;
        // A double or triple click: plain clicks (not drags) close together in time and place.
        var multiClick = !dragged && time - lastClickTime <= Native.GetDoubleClickTime()
            && Math.Max(Math.Abs(point.X - lastClickPoint.X), Math.Abs(point.Y - lastClickPoint.Y)) / scale <= 4;
        if (dragged)
        {
            lastClickTime = 0;
        }
        else
        {
            lastClickTime = time;
            lastClickPoint = point;
        }
        if (dragged || multiClick)
        {
            DebugLog.Note($"mouse {(dragged ? $"drag {moved:F0} dip" : "multi-click")} in {Native.Describe(Native.GetForegroundWindow())}");
            // Where the app can't say whether text is selected, only a gesture that clearly selects counts.
            Check(point, downPoint, trustGesture: multiClick || moved >= SureDragDistance);
        }
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
        if (ctrlA || shiftExtend)
        {
            DebugLog.Note($"key {(ctrlA ? "Ctrl+A" : "Shift+move")} in {Native.Describe(Native.GetForegroundWindow())}");
            Check(null, null);
        }
    }

    /// <summary>Movement (in DIPs) between button down and up that makes a click a drag.</summary>
    private const double DragDistance = 12;
    /// <summary>A drag this long selects text even where the app doesn't report the selection.</summary>
    private const double SureDragDistance = 40;

    /// <summary>
    /// After the app has updated its selection, show the pill if there is one in an editable field.
    /// [pointer], [pressedAt]: where a mouse selection ended and began; the field must be under one of them.
    /// [trustGesture]: show it at the pointer when the field can't report a selection.
    /// Anything uncertain (no answer in time, an error, focus elsewhere) shows nothing.
    /// </summary>
    private async void Check(Native.POINT? pointer, Native.POINT? pressedAt, bool trustGesture = false)
    {
        if (!Settings.Current.SelectionPill || IsSuspended())
        {
            DebugLog.Note("  skipped: " + (IsSuspended() ? "result card open" : "turned off in settings"));
            return;
        }
        var foreground = Native.GetForegroundWindow();
        if (foreground == IntPtr.Zero || Native.IsOwnWindow(foreground)) return;
        var generation = ++checkGeneration;
        await Task.Delay(150);
        var started = DateTime.Now;
        var points = new[] { pointer, pressedAt }.OfType<Native.POINT>().ToArray();
        var inspect = Task.Run(() => TextBridge.InspectFocusedSelection(points));
        // UI Automation can be slow the first time, or in big documents. No answer in time means no
        // pill: a double click on a file or a drag of a title bar looks the same from here.
        var answered = await Task.WhenAny(inspect, Task.Delay(1200)) == inspect;
        if (generation != checkGeneration) return;
        var (state, end) = answered ? inspect.Result : (TextBridge.SelectionState.Failed, (Rect?)null);
        DebugLog.Note($"  selection {state}{(answered ? "" : " (timed out)")} after {(DateTime.Now - started).TotalMilliseconds:F0} ms, position {(end.HasValue ? "known" : "unknown")}");
        // Editable fields that report no selection at all: trust a clear mouse gesture there.
        var show = state == TextBridge.SelectionState.Selected || (state == TextBridge.SelectionState.Unknown && pointer != null && trustGesture);
        if (!show || IsSuspended()) return;

        Native.POINT anchor;
        if (pointer is { } p) anchor = p;
        else if (end is { } rect && !rect.IsEmpty) anchor = new Native.POINT { X = (int)rect.Right, Y = (int)rect.Bottom };
        else return; // A keyboard selection with no reported position: nothing to anchor to.

        sourceWindow = foreground;
        chipText.Text = Settings.Current.Target.Code;
        polishButton.ToolTip = $"{L.Polish}  {Settings.Current.ShortcutFor(ShortcutAction.Polish).Display}";
        translateButton.ToolTip = $"{L.TranslateShort} {Settings.Current.Target.DisplayName}  {Settings.Current.ShortcutFor(ShortcutAction.Translate).Display}";
        var scale = Native.ScaleAt(anchor);
        window.Left = anchor.X / scale - 4;
        window.Top = anchor.Y / scale + 2;
        window.Show();
        watch.Start();
        DebugLog.Note($"  pill shown at {anchor.X},{anchor.Y} (scale {scale:F2})");
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
                chipText.Text = language.Code;
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>A borderless pill segment with an icon; the shortcut shows in its tooltip.</summary>
    private static Button Flat(string label, SymbolRegular symbol, Action action)
    {
        var button = new Button
        {
            Content = label,
            Icon = new SymbolIcon(symbol) { FontSize = 14 },
            Appearance = ControlAppearance.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 5, 10, 5),
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
