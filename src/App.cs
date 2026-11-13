using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Forms = System.Windows.Forms;

namespace ZnKeyboard;

public enum ShortcutResult { Ok, UsedByOther, Taken }

/// <summary>Tray icon, global shortcuts and the settings window. No main window.</summary>
public sealed class App : Application
{
    private Controller? controller;
    private HotKeys? hotKeys;
    private Forms.NotifyIcon? tray;
    private SelectionPill? pill;
    private SettingsWindow? settingsWindow;
    private EventWaitHandle? quitSignal;
    private EventWaitHandle? showSignal;
    private bool pendingUpdateBalloon;

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => DebugLog.Note($"crashed: {e.ExceptionObject}");
        if (!Installer.Prepare(args)) return;
        // One copy at a time: a second launch would fight over the shortcuts.
        using var mutex = new Mutex(initiallyOwned: true, "ZnKeyboard.SingleInstance", out var first);
        if (!first)
        {
            // Opened by hand, it may be running with its tray icon out of sight: show that it is.
            if (!args.Contains(Installer.StartupArg) && EventWaitHandle.TryOpenExisting(Installer.ShowEventName, out var show))
            {
                Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                using (show) show.Set();
            }
            return;
        }
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var atSignIn = e.Args.Contains(Installer.StartupArg);
        DebugLog.Note($"started {Updates.CurrentVersion}" + (atSignIn ? " at sign-in" : ""));
        DispatcherUnhandledException += (_, error) => DebugLog.Note($"crashed: {error.Exception}");
        // WPF-UI's Fluent styles and colours for every window, following Windows' light or dark mode.
        var theme = ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = theme });
        Resources.MergedDictionaries.Add(new ControlsDictionary());
        ApplicationThemeManager.Apply(theme, Wpf.Ui.Controls.WindowBackdropType.Mica, true);

        controller = new Controller { OpenSettings = ShowSettings };
        hotKeys = new HotKeys();
        foreach (var action in Enum.GetValues<ShortcutAction>())
            RegisterHotKey(action, Settings.Current.ShortcutFor(action));

        pill = new SelectionPill
        {
            // A pinned card sits beside what's being read, so selections there still get the pill.
            IsSuspended = () => controller.IsPanelVisible && !controller.IsPanelPinned,
            OnTranslate = () => controller.Run(RewriteMode.Translate(Settings.Current.Target)),
            OnRead = () => controller.Run(RewriteMode.Read(Settings.Current.ReadTarget)),
        };

        // WPF makes the first window it creates (the hidden result card) the main window, and dialogs
        // would attach to it; there is no main window.
        MainWindow = null;

        tray = new Forms.NotifyIcon
        {
            Icon = TrayIcon(),
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip { ShowImageMargin = true },
        };
        tray.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        // On release, as Windows' own tray icons do; a second click of a double click only finds it open.
        tray.MouseUp += (_, click) =>
        {
            if (click.Button == Forms.MouseButtons.Left) ShowSettings();
        };
        RefreshTray();
        if (atSignIn) AddTrayIconAgain();

        // A newer download installing over this copy asks it to step aside.
        quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Installer.QuitEventName);
        ThreadPool.RegisterWaitForSingleObject(quitSignal, (_, _) => Dispatcher.BeginInvoke(() => Shutdown()), null, Timeout.Infinite, executeOnlyOnce: true);
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Installer.ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => Dispatcher.BeginInvoke(() => ShowSettings()), null, Timeout.Infinite, executeOnlyOnce: false);

        if (e.Args.Contains(Installer.InstalledArg)) tray.ShowBalloonTip(5000, L.InstalledTitle, L.InstalledNote, Forms.ToolTipIcon.Info);
        else if (e.Args.Contains(Installer.UpdatedArg)) tray.ShowBalloonTip(5000, L.UpdatedTitle(Updates.CurrentVersion), L.UpdatedNote, Forms.ToolTipIcon.Info);
        else if (e.Args.Contains(Installer.AutoUpdatedArg)) tray.ShowBalloonTip(5000, L.UpdatedTitle(Updates.CurrentVersion), L.AutoUpdatedNote, Forms.ToolTipIcon.Info);

        // A new version is announced in a tray notification; clicking it installs.
        Updates.Notify = (title, text) =>
        {
            pendingUpdateBalloon = true;
            tray.ShowBalloonTip(10000, title, text, Forms.ToolTipIcon.Info);
        };
        tray.BalloonTipClicked += (_, _) =>
        {
            if (pendingUpdateBalloon && Updates.Available is { } release) Updates.Install(release);
            pendingUpdateBalloon = false;
        };
        tray.BalloonTipClosed += (_, _) => pendingUpdateBalloon = false;
        Updates.StartAutomaticChecks();

        // First run: nothing works without a key, so start where it goes. A reinstall may still have one.
        if (e.Args.Contains(Installer.InstalledArg) || string.IsNullOrEmpty(Credentials.Read(Settings.Current.Provider))) ShowSettings();
    }

    /// <summary>
    /// A busy taskbar can drop the icon at sign-in without saying so, and NotifyIcon only adds it
    /// again after Explorer restarts. Done before the first update check can show a notification.
    /// </summary>
    private async void AddTrayIconAgain()
    {
        foreach (var seconds in new[] { 5, 10 })
        {
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(seconds));
            tray!.Visible = false;
            tray.Visible = true;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        hotKeys?.Dispose();
        pill?.Dispose();
        if (tray != null) tray.Visible = false;
        tray?.Dispose();
        base.OnExit(e);
    }

    public bool HotKeysPaused
    {
        set { if (hotKeys != null) hotKeys.IsPaused = value; }
    }

    private bool RegisterHotKey(ShortcutAction action, Shortcut shortcut) =>
        hotKeys!.Register((int)action + 1, shortcut, () => Run(action));

    private void Run(ShortcutAction action)
    {
        switch (action)
        {
            case ShortcutAction.Translate: controller!.RunTranslate(); break;
            default: controller!.RunScreenshot(); break;
        }
    }

    /// <summary>Switches <paramref name="action"/> to <paramref name="shortcut"/> (its default when null). On failure the old one stays.</summary>
    public ShortcutResult ApplyShortcut(ShortcutAction action, Shortcut? shortcut)
    {
        var next = shortcut ?? Shortcut.Default(action);
        if (Enum.GetValues<ShortcutAction>().Any(other => other != action && next.Collides(Settings.Current.ShortcutFor(other))))
            return ShortcutResult.UsedByOther;
        var previous = Settings.Current.ShortcutFor(action);
        if (!RegisterHotKey(action, next))
        {
            RegisterHotKey(action, previous);
            return ShortcutResult.Taken;
        }
        Settings.Current.SetShortcut(action, next);
        return ShortcutResult.Ok;
    }

    public void RefreshTray()
    {
        if (tray != null) tray.Text = L.TrayHint.Length > 63 ? L.TrayHint[..63] : L.TrayHint;
    }

    /// <summary>Rebuilt on every open so labels follow the language, target and shortcuts.</summary>
    private void BuildMenu()
    {
        var menu = tray!.ContextMenuStrip!;
        menu.Items.Clear();
        var settings = Settings.Current;
        if (Updates.Available is { } release)
        {
            menu.Items.Add(Item(L.NewVersionMenu(release.Version), '\uE896', () => Updates.Install(release)));
            menu.Items.Add(new Forms.ToolStripSeparator());
        }
        menu.Items.Add(Item(L.ScreenshotTranslate, '', () => RunFromMenu(ShortcutAction.Screenshot), settings.ShortcutFor(ShortcutAction.Screenshot)));
        menu.Items.Add(Item(L.TranslateTo(settings.Target), '', () => RunFromMenu(ShortcutAction.Translate), settings.ShortcutFor(ShortcutAction.Translate)));
        // Two lists: what text boxes are translated into, and what reading and screenshots are.
        var languages = new Forms.ToolStripMenuItem(L.TranslateMenu);
        foreach (var reading in new[] { false, true })
        {
            if (reading) languages.DropDownItems.Add(new Forms.ToolStripSeparator());
            languages.DropDownItems.Add(new Forms.ToolStripMenuItem(reading ? L.ScreenshotTarget : L.TranslateMenu) { Enabled = false });
            var current = reading ? settings.ScreenshotTarget : settings.TranslateTarget;
            foreach (var language in Languages.All)
            {
                languages.DropDownItems.Add(new Forms.ToolStripMenuItem(language.Label, null, (_, _) =>
                {
                    if (reading) Settings.Current.ScreenshotTarget = language.Code; else Settings.Current.TranslateTarget = language.Code;
                    Settings.Current.Save();
                }) { Checked = language.Code == current });
            }
        }
        menu.Items.Add(languages);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item(L.SettingsMenu, '', ShowSettings));
        menu.Items.Add(Item(L.CheckForUpdates, '', Updates.Check));
        menu.Items.Add(Item(L.About, '', Updates.ShowAbout));
        menu.Items.Add(Item(L.OpenLog, null, DebugLog.Open));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item(L.Quit, '', Shutdown));
    }

    private static Forms.ToolStripMenuItem Item(string text, char? glyph, Action action, Shortcut? shortcut = null) =>
        new(text, glyph is { } g ? Glyph(g) : null, (_, _) => action()) { ShortcutKeyDisplayString = shortcut?.Display };

    // The menu is rebuilt on every open, on the thread that also serves the mouse hook, and listing
    // the installed fonts is slow: drawn once.
    private static readonly System.Collections.Generic.Dictionary<char, Bitmap?> Glyphs = new();

    /// <summary>A Fluent icon from Windows' own icon font (Segoe Fluent Icons on 11, Segoe MDL2 Assets on 10).</summary>
    private static Bitmap? Glyph(char glyph)
    {
        if (!Glyphs.TryGetValue(glyph, out var bitmap)) Glyphs[glyph] = bitmap = DrawGlyph(glyph);
        return bitmap;
    }

    private static Bitmap? DrawGlyph(char glyph)
    {
        try
        {
            var size = Forms.SystemInformation.SmallIconSize.Height;
            var family = FontFamily.Families.FirstOrDefault(f => f.Name == "Segoe Fluent Icons")
                ?? FontFamily.Families.FirstOrDefault(f => f.Name == "Segoe MDL2 Assets");
            if (family == null) return null;
            var bitmap = new Bitmap(size, size);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Font(family, size * 0.75f, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(System.Drawing.SystemColors.MenuText);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(glyph.ToString(), font, brush, new RectangleF(0, 0, size, size), format);
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The menu took focus from the app with the selection (or would be in the screenshot); let it close first.</summary>
    private async void RunFromMenu(ShortcutAction action)
    {
        await System.Threading.Tasks.Task.Delay(250);
        Run(action);
    }

    private void ShowSettings()
    {
        if (settingsWindow == null)
        {
            settingsWindow = new SettingsWindow(this);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
        }
        settingsWindow.Show();
        if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
        settingsWindow.Activate();
    }

    private static Icon TrayIcon()
    {
        // A simplified version of the app icon that stays legible at 16-32 px.
        using var stream = typeof(App).Assembly.GetManifestResourceStream("TrayIcon.ico")!;
        return new Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public static BitmapFrame IconImage()
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream("AppIcon.ico")!;
        return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }
}
