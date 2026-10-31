using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ZnKeyboard;

/// <summary>
/// The downloaded exe installs itself for the current user (no admin rights): it copies itself to
/// %LocalAppData%\Programs\ZnKeyboard, adds a Start menu shortcut and an entry under Settings → Apps,
/// then starts the installed copy. Running a newer download replaces the installed copy the same way,
/// so moving or deleting the download never breaks the app or Start with Windows.
/// </summary>
public static class Installer
{
    public const string InstalledArg = "--installed";
    public const string UpdatedArg = "--updated";
    private const string UninstallArg = "--uninstall";
    // Runs where it is, for testing a build without installing it.
    private const string PortableArg = "--portable";
    // Set by a running copy; the installer and uninstaller signal it to quit.
    public const string QuitEventName = "ZnKeyboard.Quit";

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ZnKeyboard";

    public static string InstallFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ZnKeyboard");

    public static string InstalledExe => Path.Combine(InstallFolder, "ZnKeyboard.exe");

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ZnKeyboard.lnk");

    /// <summary>True when this process should carry on as the app; false when it installed, uninstalled or handed off.</summary>
    public static bool Prepare(string[] args)
    {
        if (args.Contains(UninstallArg))
        {
            Uninstall();
            return false;
        }
        var self = Environment.ProcessPath;
        if (self == null || args.Contains(PortableArg) || SamePath(self, InstalledExe)) return true;
        return !Install(self);
    }

    /// <summary>Copies <paramref name="self"/> into place and starts it; false (run from here) when that fails.</summary>
    private static bool Install(string self)
    {
        var installedVersion = VersionOf(InstalledExe);
        var current = Version.TryParse(Updates.CurrentVersion, out var v) ? v : new Version(0, 0, 0);
        // An older download run by mistake opens the newer installed copy instead of downgrading it.
        if (installedVersion != null && installedVersion > current)
        {
            Start(InstalledExe, null);
            return true;
        }
        var fresh = installedVersion == null;
        try
        {
            QuitRunningCopies();
            Directory.CreateDirectory(InstallFolder);
            CopyWithRetry(self, InstalledExe);
            CreateShortcut();
            Register();
            // A fresh install starts with Windows, like other tray tools; an update keeps the choice
            // and moves a Run entry that pointed at the old download.
            if (fresh || Settings.StartsWithWindows) Settings.SetStartWithWindows(InstalledExe);
        }
        catch (Exception error)
        {
            Forms.Application.EnableVisualStyles();
            DebugLog.Note($"install failed: {error.GetType().Name}: {error.Message}");
            Forms.MessageBox.Show(L.InstallFailed(error.Message), "ZnKeyboard", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
            return false;
        }
        DebugLog.Note(fresh ? "installed" : $"updated from {installedVersion}");
        Start(InstalledExe, fresh ? InstalledArg : UpdatedArg);
        return true;
    }

    private static void Uninstall()
    {
        Forms.Application.EnableVisualStyles();
        var removeData = new Forms.TaskDialogVerificationCheckBox(L.UninstallRemoveData);
        var uninstall = new Forms.TaskDialogButton(L.Uninstall);
        var page = new Forms.TaskDialogPage
        {
            Caption = "ZnKeyboard",
            Heading = L.UninstallHeading,
            Text = L.UninstallText,
            Icon = Forms.TaskDialogIcon.Information,
            Verification = removeData,
            Buttons = { uninstall, Forms.TaskDialogButton.Cancel },
            DefaultButton = uninstall,
        };
        if (Forms.TaskDialog.ShowDialog(page) != uninstall) return;

        QuitRunningCopies();
        Settings.SetStartWithWindows(null);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        TryDelete(ShortcutPath);
        if (removeData.Checked)
        {
            Credentials.DeleteAll();
            TryDeleteFolder(Settings.FolderPath);
        }
        // This exe is the one being removed and can't delete itself while it runs; cmd waits for it to exit
        // (ping, because timeout refuses to run without a console).
        var folder = InstallFolder.Replace("\"", "");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{folder}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    /// <summary>Asks a running ZnKeyboard (0.5.0 or newer) to quit; older copies, which don't listen, are stopped.</summary>
    private static void QuitRunningCopies()
    {
        var others = Process.GetProcessesByName("ZnKeyboard").Where(p => p.Id != Environment.ProcessId).ToList();
        if (others.Count == 0) return;
        if (EventWaitHandle.TryOpenExisting(QuitEventName, out var quit))
        {
            using (quit) quit.Set();
        }
        foreach (var process in others)
        {
            using (process)
            {
                try
                {
                    if (!process.WaitForExit(3000)) process.Kill();
                    process.WaitForExit(3000);
                }
                catch (Exception)
                {
                    // Already gone, or not ours to stop.
                }
            }
        }
    }

    /// <summary>
    /// Copies the bytes only, so the download's "from the internet" mark stays behind and the installed
    /// copy doesn't meet SmartScreen again. Retries while the old exe is still being released.
    /// </summary>
    private static void CopyWithRetry(string from, string to)
    {
        var temp = to + ".new";
        using (var source = File.OpenRead(from))
        using (var target = File.Create(temp))
            source.CopyTo(target);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, to, overwrite: true);
                return;
            }
            catch (Exception) when (attempt < 10)
            {
                Thread.Sleep(300);
            }
        }
    }

    private static void CreateShortcut()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        var link = shell.CreateShortcut(ShortcutPath);
        link.TargetPath = InstalledExe;
        link.WorkingDirectory = InstallFolder;
        link.IconLocation = InstalledExe + ",0";
        link.Description = L.ShortcutDescription;
        link.Save();
    }

    /// <summary>The entry under Settings → Apps → Installed apps, with its Uninstall button.</summary>
    private static void Register()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "ZnKeyboard");
        key.SetValue("DisplayVersion", Updates.CurrentVersion);
        key.SetValue("Publisher", "oprfelix");
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("InstallLocation", InstallFolder);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" {UninstallArg}");
        key.SetValue("URLInfoAbout", "https://oprfelix.github.io/znkeyboard/");
        key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static Version? VersionOf(string path)
    {
        if (!File.Exists(path)) return null;
        var info = FileVersionInfo.GetVersionInfo(path);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
    }

    private static void Start(string path, string? argument) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, Arguments = argument ?? "", WorkingDirectory = InstallFolder });

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }

    private static void TryDeleteFolder(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (Exception) { }
    }
}
