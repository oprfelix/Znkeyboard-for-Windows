using System;
using System.IO;

namespace ZnKeyboard;

/// <summary>
/// What the selection pill and replacement saw, one line per event, in
/// %APPDATA%\ZnKeyboard\debug.log, for colleagues to send when something doesn't work. Never any
/// selected or typed text. Starts over once it passes 200 KB.
/// </summary>
public static class DebugLog
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZnKeyboard", "debug.log");
    private static readonly object Gate = new();

    /// <summary>Opens the log in Notepad (or the folder when there is no log yet).</summary>
    public static void Open()
    {
        var target = File.Exists(FilePath) ? FilePath : Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
    }

    public static void Note(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 200_000) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Logging must never break the app.
        }
    }
}
