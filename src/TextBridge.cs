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
        lastReadTop = null;
        var text = await Task.Run(AutomationSelectedText).WaitAsync(TimeSpan.FromMilliseconds(600)).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);
        if (!string.IsNullOrEmpty(text)) return text;
        // Copying a whole document can take a moment in some apps; a missed Ctrl+C gets one more try.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var copied = await CopySelectionAsync();
            if (!string.IsNullOrEmpty(copied)) return copied;
            DebugLog.Note($"read: Ctrl+C try {attempt} found nothing");
        }
        return null;
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
        if (!TrySetClipboard(() => Clipboard.SetDataObject(Borrowed(text), copy: true))) return;
        var ours = Native.GetClipboardSequenceNumber();
        await ReleaseModifiersAsync();
        Native.SendCtrl(Native.VK_V);
        // The target app reads the clipboard asynchronously, and a slow one (Electron, a browser just
        // woken up) can take most of a second; restoring sooner would paste the old clipboard.
        await Task.Delay(1200);
        // Something else copied meanwhile (the user, a clipboard manager): theirs stays.
        if (Native.GetClipboardSequenceNumber() != ours) return;
        saved.Restore();
    }

    /// <summary>
    /// Unknown: an editable field that can't report its selection. Elsewhere: the focused field isn't
    /// where the mouse selected (an old selection, while the user dragged a scrollbar or title bar).
    /// Failed: the app couldn't be asked (it errored, or focus moved mid-way). Reading: text selected
    /// in something that can't be edited (a web page, a document being read).
    /// </summary>
    public enum SelectionState { NotEditable, Empty, Selected, Unknown, Elsewhere, Failed, Reading }

    /// <summary>
    /// Whether the focused element in another app is an editable text box with a selection (or, with
    /// <paramref name="reading"/>, any element reporting selected text), and where the selection ends
    /// on screen (device pixels) when the app reports it. Blocking; call off the UI thread.
    /// </summary>
    /// <param name="mousePoints">Where a mouse selection began and ended (device pixels); empty for the keyboard.</param>
    internal static (SelectionState state, Rect? end) InspectFocusedSelection(Native.POINT[] mousePoints, bool reading = false)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null || focused.Current.ProcessId == Environment.ProcessId) return (SelectionState.NotEditable, null);
            var editable = IsEditable(focused);
            if (!editable && !reading)
            {
                DebugLog.Note($"  focused {focused.Current.ControlType.ProgrammaticName} '{focused.Current.ClassName}' is not editable");
                return (SelectionState.NotEditable, null);
            }
            if (mousePoints.Length > 0)
            {
                // Device pixels, like the hook's points (the app is per-monitor DPI aware).
                var bounds = focused.Current.BoundingRectangle;
                if (!bounds.IsEmpty) bounds.Inflate(4, 4);
                if (!bounds.IsEmpty && !mousePoints.Any(p => bounds.Contains(p.X, p.Y)))
                {
                    DebugLog.Note($"  focused '{focused.Current.ClassName}' is not under the pointer");
                    return (SelectionState.Elsewhere, null);
                }
            }
            // Read-only text that can't report its selection gets nothing: there is no clear sign it was text.
            var unknown = editable ? SelectionState.Unknown : SelectionState.NotEditable;
            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return (unknown, null);
            var ranges = ((TextPattern)pattern).GetSelection();
            if (ranges.Length == 0) return (unknown, null);
            var text = string.Concat(ranges.Select(r => r.GetText(200)));
            if (string.IsNullOrWhiteSpace(text)) return (editable ? SelectionState.Empty : SelectionState.NotEditable, null);
            var rects = ranges[^1].GetBoundingRectangles();
            if (!editable) DebugLog.Note($"  focused {focused.Current.ControlType.ProgrammaticName} '{focused.Current.ClassName}' is read-only, with text selected");
            return (editable ? SelectionState.Selected : SelectionState.Reading, rects.Length > 0 ? rects[^1] : null);
        }
        catch (Exception e)
        {
            DebugLog.Note($"  inspecting the selection failed: {e.GetType().Name}");
            return (SelectionState.Failed, null);
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
            // The same text can appear more than once (a repeated line, a signature): take the
            // occurrence nearest where the selection was read.
            var matches = Occurrences(text.DocumentRange, original);
            if (matches.Count == 0) matches = Occurrences(text.DocumentRange, Flat(original));
            if (matches.Count == 0) return "lost, original not found";
            var best = lastReadTop is { } top
                ? matches.OrderBy(m => m.GetBoundingRectangles() is { Length: > 0 } r ? Math.Abs(r[0].Top - top) : double.MaxValue).First()
                : matches[0];
            best.Select();
            return $"reselected ({matches.Count} found)";
        }
        catch (Exception e)
        {
            return "unknown (" + e.GetType().Name + ")";
        }
    }

    /// <summary>Every place <paramref name="needle"/> appears in <paramref name="document"/>, at most 20.</summary>
    private static List<TextPatternRange> Occurrences(TextPatternRange document, string needle)
    {
        var found = new List<TextPatternRange>();
        var rest = document.Clone();
        while (found.Count < 20 && rest.FindText(needle, backward: false, ignoreCase: false) is { } match)
        {
            found.Add(match);
            // Search on from the end of this match.
            rest.MoveEndpointByRange(TextPatternRangeEndpoint.Start, match, TextPatternRangeEndpoint.End);
            if (rest.CompareEndpoints(TextPatternRangeEndpoint.Start, rest, TextPatternRangeEndpoint.End) >= 0) break;
        }
        return found;
    }

    /// <summary>Where on screen (top, device pixels) the last selection read through UI Automation was.</summary>
    private static double? lastReadTop;

    private static string? AutomationSelectedText()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null || focused.Current.ProcessId == Environment.ProcessId) return null;
            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return null;
            var ranges = ((TextPattern)pattern).GetSelection();
            if (ranges.Length > 0 && ranges[0].GetBoundingRectangles() is { Length: > 0 } rects) lastReadTop = rects[0].Top;
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
        for (var i = 0; i < 40 && Native.GetClipboardSequenceNumber() == before; i++) await Task.Delay(25);
        if (Native.GetClipboardSequenceNumber() == before) return null;
        string? text = null;
        TrySetClipboard(() =>
        {
            // Plain text, or the text of rich content when an app copies only that.
            if (Clipboard.ContainsText()) text = Clipboard.GetText();
            else if (Clipboard.ContainsText(TextDataFormat.Rtf)) text = PlainFromRtf(Clipboard.GetText(TextDataFormat.Rtf));
            else if (Clipboard.ContainsText(TextDataFormat.Html)) text = PlainFromHtml(Clipboard.GetText(TextDataFormat.Html));
        });
        saved.Restore();
        return text;
    }

    private static string? PlainFromRtf(string rtf)
    {
        try
        {
            var document = new System.Windows.Documents.FlowDocument();
            var range = new System.Windows.Documents.TextRange(document.ContentStart, document.ContentEnd);
            using var stream = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(rtf));
            range.Load(stream, DataFormats.Rtf);
            return range.Text.TrimEnd('\r', '\n');
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The fragment of a CF_HTML clipboard entry, tags dropped and entities decoded.</summary>
    private static string? PlainFromHtml(string html)
    {
        var start = html.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        var end = html.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end > start) html = html[(start + 20)..end];
        html = System.Text.RegularExpressions.Regex.Replace(html, @"<(br|/p|/div|/li|/h\d)\b[^>]*>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        html = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", "");
        var text = System.Net.WebUtility.HtmlDecode(html).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>Waits for Alt, Shift and Win from the shortcut to be let go, then lifts any still held.</summary>
    private static async Task ReleaseModifiersAsync()
    {
        byte[] keys = [Native.VK_MENU, Native.VK_SHIFT, Native.VK_LWIN, Native.VK_RWIN, Native.VK_CONTROL];
        for (var i = 0; i < 30 && keys.Any(Native.IsDown); i++) await Task.Delay(20);
        foreach (var key in keys.Where(Native.IsDown)) Native.Release(key);
    }

    /// <summary>Another app may hold the clipboard open for a moment; try a few times.</summary>
    /// <summary>The replacement text, kept out of Win+V clipboard history and cloud clipboard: it is only passing through.</summary>
    private static DataObject Borrowed(string text)
    {
        var data = new DataObject();
        data.SetText(text);
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new System.IO.MemoryStream(new byte[] { 0 }));
        data.SetData("CanIncludeInClipboardHistory", new System.IO.MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", new System.IO.MemoryStream(BitConverter.GetBytes(0)));
        return data;
    }

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
