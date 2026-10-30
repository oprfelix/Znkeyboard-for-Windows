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

    /// <summary>
    /// Puts <paramref name="text"/> where <paramref name="original"/> is selected in <paramref name="target"/>.
    /// If the app dropped the selection meanwhile, it is found and selected again first, so the
    /// paste replaces the original instead of landing after it.
    /// </summary>
    public static async Task ReplaceSelectionAsync(IntPtr target, string original, string text)
    {
        if (target != IntPtr.Zero && Native.GetForegroundWindow() != target) Native.SetForegroundWindow(target);
        await Task.Delay(80);
        var check = Task.Run(() => EnsureSelected(original));
        var outcome = await Task.WhenAny(check, Task.Delay(1500)) == check ? check.Result : "timeout";
        DebugLog.Note($"replace in {Native.Describe(Native.GetForegroundWindow())}: selection {outcome}");
        var saved = ClipboardSnapshot.Take();
        if (!TrySetClipboard(() => Clipboard.SetText(text))) return;
        await ReleaseModifiersAsync();
        Native.SendCtrl(Native.VK_V);
        // The target app reads the clipboard asynchronously; give it time before restoring.
        await Task.Delay(600);
        saved.Restore();
    }

    public enum SelectionState { NotEditable, Empty, Selected, Unknown }

    /// <summary>
    /// Whether the focused element in another app is an editable text box with a selection, and
    /// where the selection ends on screen (device pixels) when the app reports it. Blocking; call
    /// off the UI thread.
    /// </summary>
    public static (SelectionState state, Rect? end) InspectFocusedSelection()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null || focused.Current.ProcessId == Environment.ProcessId) return (SelectionState.NotEditable, null);
            if (!IsEditable(focused))
            {
                DebugLog.Note($"  focused {focused.Current.ControlType.ProgrammaticName} '{focused.Current.ClassName}' is not editable");
                return (SelectionState.NotEditable, null);
            }
            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return (SelectionState.Unknown, null);
            var ranges = ((TextPattern)pattern).GetSelection();
            if (ranges.Length == 0) return (SelectionState.Unknown, null);
            var text = string.Concat(ranges.Select(r => r.GetText(200)));
            if (text.Length == 0) return (SelectionState.Empty, null);
            var rects = ranges[^1].GetBoundingRectangles();
            return (SelectionState.Selected, rects.Length > 0 ? rects[^1] : null);
        }
        catch (Exception)
        {
            return (SelectionState.Unknown, null);
        }
    }

    /// <summary>Text inputs, and documents or other elements whose value can be edited (Word, rich editors).</summary>
    private static bool IsEditable(AutomationElement element)
    {
        if (element.Current.ControlType == ControlType.Edit) return true;
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return !((ValuePattern)value).Current.IsReadOnly;
        return element.Current.ControlType == ControlType.Document && element.Current.IsKeyboardFocusable
            && element.Current.ClassName is "_WwG" or "RichEditD2DPT" or "RICHEDIT50W";
    }

    /// <summary>"kept", "reselected", or why neither (the paste then goes wherever the caret is).</summary>
    private static string EnsureSelected(string original)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null) return "no focus";
            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return "unknown (no text pattern)";
            var text = (TextPattern)pattern;
            var selected = string.Concat(text.GetSelection().Select(r => r.GetText(-1)));
            static string Flat(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');
            if (Flat(selected) == Flat(original)) return "kept";
            var found = text.DocumentRange.FindText(original, backward: false, ignoreCase: false)
                ?? text.DocumentRange.FindText(Flat(original), backward: false, ignoreCase: false);
            if (found == null) return "lost, original not found";
            found.Select();
            return "reselected";
        }
        catch (Exception e)
        {
            return "unknown (" + e.GetType().Name + ")";
        }
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
