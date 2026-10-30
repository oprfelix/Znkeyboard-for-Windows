using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace ZnKeyboard;

/// <summary>
/// Reads the selected text in the foreground app and replaces it. Reading asks UI Automation first
/// and falls back to a simulated Ctrl+C; replacing pastes, which works everywhere. The user's
/// clipboard is put back afterwards. No permission is needed, but windows running as
/// administrator ignore input from a normal app.
/// </summary>
public static class TextBridge
{
    public static async Task<string?> SelectedTextAsync()
    {
        var text = await Task.Run(AutomationSelectedText).WaitAsync(TimeSpan.FromMilliseconds(600)).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);
        if (!string.IsNullOrEmpty(text)) return text;
        return await CopySelectionAsync();
    }

    /// <summary>Puts <paramref name="text"/> where the selection was in <paramref name="target"/>.</summary>
    public static async Task ReplaceSelectionAsync(IntPtr target, string text)
    {
        if (target != IntPtr.Zero) Native.SetForegroundWindow(target);
        await Task.Delay(120);
        var saved = ClipboardSnapshot.Take();
        if (!TrySetClipboard(() => Clipboard.SetText(text))) return;
        await ReleaseModifiersAsync();
        Native.SendCtrl(Native.VK_V);
        // The target app reads the clipboard asynchronously; give it time before restoring.
        await Task.Delay(600);
        saved.Restore();
    }

    private static string? AutomationSelectedText()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null || focused.Current.ProcessId == Environment.ProcessId) return null;
            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return null;
            var ranges = ((TextPattern)pattern).GetSelection();
            return string.Concat(ranges.Select(r => r.GetText(-1)));
        }
        catch (Exception)
        {
            // Many apps implement UI Automation partly or not at all; copying is the fallback.
            return null;
        }
    }

    /// <summary>Browsers, Office and most apps answer Ctrl+C even when UI Automation doesn't.</summary>
    private static async Task<string?> CopySelectionAsync()
    {
        var saved = ClipboardSnapshot.Take();
        var before = Native.GetClipboardSequenceNumber();
        await ReleaseModifiersAsync();
        Native.SendCtrl(Native.VK_C);
        for (var i = 0; i < 24 && Native.GetClipboardSequenceNumber() == before; i++) await Task.Delay(25);
        if (Native.GetClipboardSequenceNumber() == before) return null;
        string? text = null;
        TrySetClipboard(() => text = Clipboard.ContainsText() ? Clipboard.GetText() : null);
        saved.Restore();
        return text;
    }

    /// <summary>Waits for Alt, Shift and Win from the shortcut to be let go, then lifts any still held.</summary>
    private static async Task ReleaseModifiersAsync()
    {
        byte[] keys = [Native.VK_MENU, Native.VK_SHIFT, Native.VK_LWIN, Native.VK_RWIN, Native.VK_CONTROL];
        for (var i = 0; i < 30 && keys.Any(Native.IsDown); i++) await Task.Delay(20);
        foreach (var key in keys.Where(Native.IsDown)) Native.Release(key);
    }

    /// <summary>Another app may hold the clipboard open for a moment; try a few times.</summary>
    internal static bool TrySetClipboard(Action action)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                System.Threading.Thread.Sleep(30);
            }
        }
        return false;
    }

    /// <summary>Every format on the clipboard that can be read, so a borrowed clipboard goes back as it was.</summary>
    private sealed class ClipboardSnapshot
    {
        private readonly List<(string format, object data)> items = new();

        public static ClipboardSnapshot Take()
        {
            var snapshot = new ClipboardSnapshot();
            TrySetClipboard(() =>
            {
                var current = Clipboard.GetDataObject();
                if (current == null) return;
                foreach (var format in current.GetFormats(autoConvert: false))
                {
                    try
                    {
                        if (current.GetData(format, autoConvert: false) is { } data) snapshot.items.Add((format, data));
                    }
                    catch (Exception)
                    {
                        // Some private formats can't be read back; skip them.
                    }
                }
            });
            return snapshot;
        }

        public void Restore()
        {
            TrySetClipboard(() =>
            {
                if (items.Count == 0)
                {
                    Clipboard.Clear();
                    return;
                }
                var data = new DataObject();
                foreach (var (format, value) in items) data.SetData(format, value);
                Clipboard.SetDataObject(data, copy: true);
            });
        }
    }
}
