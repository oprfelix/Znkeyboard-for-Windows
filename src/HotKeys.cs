using System;
using System.Collections.Generic;
using System.Windows.Interop;

namespace ZnKeyboard;

/// <summary>System-wide shortcuts through RegisterHotKey, delivered to a hidden message window.</summary>
public sealed class HotKeys : IDisposable
{
    private readonly HwndSource window;
    private readonly Dictionary<int, Action> actions = new();

    /// <summary>Set while a new shortcut is being recorded, so pressing an existing one doesn't fire it.</summary>
    public bool IsPaused { get; set; }

    public HotKeys()
    {
        // HWND_MESSAGE parent: a window that never shows and only receives messages.
        window = new HwndSource(new HwndSourceParameters("ZnKeyboardHotKeys") { ParentWindow = new IntPtr(-3) });
        window.AddHook(WndProc);
    }

    /// <summary>Replaces whatever <paramref name="id"/> had. False if another app holds the combination.</summary>
    public bool Register(int id, Shortcut shortcut, Action action)
    {
        Unregister(id);
        if (!Native.RegisterHotKey(window.Handle, id, shortcut.Modifiers | Native.MOD_NOREPEAT, shortcut.Key)) return false;
        actions[id] = action;
        return true;
    }

    public void Unregister(int id)
    {
        if (actions.Remove(id)) Native.UnregisterHotKey(window.Handle, id);
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Native.WM_HOTKEY && actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            if (!IsPaused) action();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in new List<int>(actions.Keys)) Unregister(id);
        window.Dispose();
    }
}
