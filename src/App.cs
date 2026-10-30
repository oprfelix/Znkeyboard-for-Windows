using System;
using System.Drawing;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace ZnKeyboard;

public enum ShortcutResult { Ok, UsedByOther, Taken }

/// <summary>Tray icon, global shortcuts and the settings window. No main window.</summary>
public sealed class App : Application
{
    private readonly Controller controller = new();
    private HotKeys? hotKeys;
    private Forms.NotifyIcon? tray;
    private SettingsWindow? settingsWindow;

    [STAThread]
    public static void Main()
    {
        // One copy at a time: a second launch would fight over the shortcuts.
        using var mutex = new Mutex(initiallyOwned: true, "ZnKeyboard.SingleInstance", out var first);
        if (!first) return;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        controller.OpenSettings = ShowSettings;
        hotKeys = new HotKeys();
        foreach (var action in new[] { ShortcutAction.Polish, ShortcutAction.Translate })
            RegisterHotKey(action, Settings.Current.ShortcutFor(action));

        tray = new Forms.NotifyIcon
        {
            Icon = TrayIcon(),
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        tray.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        tray.DoubleClick += (_, _) => ShowSettings();
        RefreshTray();

        // First run: nothing works without a key, so start where it goes.
        if (string.IsNullOrEmpty(Credentials.Read(Settings.Current.Provider))) ShowSettings();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        hotKeys?.Dispose();
        if (tray != null) tray.Visible = false;
        tray?.Dispose();
        base.OnExit(e);
    }

    public bool HotKeysPaused
    {
        set { if (hotKeys != null) hotKeys.IsPaused = value; }
    }

    private bool RegisterHotKey(ShortcutAction action, Shortcut shortcut) =>
        hotKeys!.Register(action == ShortcutAction.Polish ? 1 : 2, shortcut, () =>
            controller.Run(action == ShortcutAction.Polish ? RewriteMode.Polish : RewriteMode.Translate(Settings.Current.Target)));

    /// <summary>Switches <paramref name="action"/> to <paramref name="shortcut"/> (its default when null). On failure the old one stays.</summary>
    public ShortcutResult ApplyShortcut(ShortcutAction action, Shortcut? shortcut)
    {
        var next = shortcut ?? Shortcut.Default(action);
        var other = action == ShortcutAction.Polish ? ShortcutAction.Translate : ShortcutAction.Polish;
        if (next.Collides(Settings.Current.ShortcutFor(other))) return ShortcutResult.UsedByOther;
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
        menu.Items.Add(new Forms.ToolStripMenuItem(L.PolishSelection, null, (_, _) => RunFromMenu(RewriteMode.Polish))
        {
            ShortcutKeyDisplayString = settings.ShortcutFor(ShortcutAction.Polish).Display,
        });
        menu.Items.Add(new Forms.ToolStripMenuItem(L.TranslateTo(settings.Target), null, (_, _) => RunFromMenu(RewriteMode.Translate(Settings.Current.Target)))
        {
            ShortcutKeyDisplayString = settings.ShortcutFor(ShortcutAction.Translate).Display,
        });
        var languages = new Forms.ToolStripMenuItem(L.TranslateMenu);
        foreach (var language in Languages.All)
        {
            languages.DropDownItems.Add(new Forms.ToolStripMenuItem(language.Label, null, (_, _) =>
            {
                Settings.Current.TranslateTarget = language.Code;
                Settings.Current.Save();
            }) { Checked = language.Code == settings.TranslateTarget });
        }
        menu.Items.Add(languages);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem(L.SettingsMenu, null, (_, _) => ShowSettings()));
        menu.Items.Add(new Forms.ToolStripMenuItem(L.Quit, null, (_, _) => Shutdown()));
    }

    /// <summary>The menu took focus from the app with the selection; give it a moment to get it back.</summary>
    private async void RunFromMenu(RewriteMode mode)
    {
        await System.Threading.Tasks.Task.Delay(250);
        controller.Run(mode);
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
        using var stream = typeof(App).Assembly.GetManifestResourceStream("AppIcon.ico")!;
        // The tray uses the 16/32 px frames of the app icon.
        return new Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public static BitmapFrame IconImage()
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream("AppIcon.ico")!;
        return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }
}
