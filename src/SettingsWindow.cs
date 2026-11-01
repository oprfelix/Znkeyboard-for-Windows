using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using PasswordBox = Wpf.Ui.Controls.PasswordBox;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace ZnKeyboard;

/// <summary>
/// Settings in the Windows 11 style: a Mica window with a navigation list on the left (AI service,
/// usage, shortcuts, general) and a page of setting cards on the right. Follows Windows' light or
/// dark mode; rebuilt whole when the UI language or the provider changes.
/// </summary>
public sealed class SettingsWindow : FluentWindow
{
    private enum Page { Ai, Usage, Shortcuts, General }

    private readonly App app;
    private readonly TitleBar titleBar = new() { ShowMaximize = false, ShowMinimize = true };
    private readonly StackPanel nav = new() { Margin = new Thickness(8, 4, 8, 8) };
    private readonly StackPanel page = new() { Margin = new Thickness(28, 4, 36, 32), MaxWidth = 760 };
    private readonly ScrollViewer pageScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private Page current = Page.Ai;
    private CancellationTokenSource? modelLoad;
    /// <summary>The line under the key: a link to get one, or the result of checking it.</summary>
    private TextBlock? keyStatus;
    private ComboBox? modelBox;
    private readonly System.Windows.Threading.DispatcherTimer keySaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    /// <summary>Saves the key field of the current build of the page.</summary>
    private Action? keySaveTick;

    public SettingsWindow(App app)
    {
        this.app = app;
        keySaveTimer.Tick += (_, _) => keySaveTick?.Invoke();
        Width = 900;
        Height = 660;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI");
        Icon = App.IconImage();
        titleBar.Icon = new ImageIcon { Source = App.IconImage(), Width = 16, Height = 16 };

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(nav);
        pageScroll.Content = page;
        Grid.SetColumn(pageScroll, 1);
        body.Children.Add(pageScroll);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(titleBar);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Content = root;

        Loaded += (_, _) => SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
        Build();
    }

    private void Build()
    {
        Title = L.SettingsTitle;
        titleBar.Title = L.SettingsTitle;
        nav.Children.Clear();
        nav.Children.Add(NavItem(Page.Ai, SymbolRegular.BrainCircuit24, L.AiSection));
        nav.Children.Add(NavItem(Page.Usage, SymbolRegular.DesktopCursor24, L.UsageSection));
        nav.Children.Add(NavItem(Page.Shortcuts, SymbolRegular.Keyboard24, L.ShortcutsSection));
        nav.Children.Add(NavItem(Page.General, SymbolRegular.Settings24, L.GeneralSection));

        keySaveTimer.Stop();
        keySaveTick = null;
        keyStatus = null;
        modelBox = null;
        page.Children.Clear();
        switch (current)
        {
            case Page.Ai: BuildAi(); break;
            case Page.Usage: BuildUsage(); break;
            case Page.Shortcuts: BuildShortcuts(); break;
            default: BuildGeneral(); break;
        }
        pageScroll.ScrollToTop();
    }

    private void Rebuild() => Dispatcher.BeginInvoke(Build);

    // ---- Pages ----

    private void BuildAi()
    {
        var settings = Settings.Current;
        PageTitle(L.AiSection);

        var provider = Combo(new[] { "Google Gemini", L.OpenAICompatible }, (int)settings.Provider);
        provider.SelectionChanged += (_, _) =>
        {
            settings.Provider = (Provider)provider.SelectedIndex;
            settings.Save();
            Rebuild();
        };
        page.Children.Add(Card(SymbolRegular.PlugConnected24, L.Provider, L.ProviderNote, provider));

        if (settings.Provider == Provider.OpenAICompatible)
        {
            var preset = Combo(new[] { "DeepSeek", L.CustomPreset }, (int)settings.EffectivePreset);
            preset.SelectionChanged += (_, _) =>
            {
                settings.Preset = (ApiPreset)preset.SelectedIndex;
                // A model typed for another service would not exist on DeepSeek.
                if (settings.Preset == ApiPreset.DeepSeek && !settings.OpenAIModel.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase))
                    settings.OpenAIModel = Settings.DefaultOpenAIModel;
                settings.Save();
                Rebuild();
            };
            page.Children.Add(Card(SymbolRegular.Globe24, L.ApiPreset, L.ApiPresetNote, preset));
        }

        if (settings.Provider == Provider.OpenAICompatible && settings.EffectivePreset == ApiPreset.Custom)
        {
            var baseUrl = new TextBox { Text = settings.OpenAIBaseUrl, Width = 320, PlaceholderText = "https://" };
            baseUrl.LostFocus += (_, _) => { settings.OpenAIBaseUrl = baseUrl.Text.Trim(); settings.Save(); LoadModels(); };
            page.Children.Add(Card(SymbolRegular.Globe24, L.ApiBaseUrl, L.ApiBaseUrlNote, baseUrl));
        }

        // The key saves itself shortly after typing or pasting stops, then gets checked.
        var key = new PasswordBox { Password = Credentials.Read(settings.Provider) ?? "", Width = 320, PlaceholderText = L.ApiKey };
        keyStatus = new TextBlock { FontSize = 12, Margin = new Thickness(2, 6, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
        void SaveKey()
        {
            keySaveTimer.Stop();
            if (key.Password.Trim() == (Credentials.Read(settings.Provider) ?? "")) return;
            Credentials.Write(settings.Provider, key.Password);
            LoadModels();
        }
        keySaveTick = SaveKey;
        key.PasswordChanged += (_, _) => { keySaveTimer.Stop(); keySaveTimer.Start(); };
        key.KeyDown += (_, e) => { if (e.Key == Key.Enter) SaveKey(); };
        key.LostFocus += (_, _) => SaveKey();
        page.Children.Add(Card(SymbolRegular.ShieldLock24, L.ApiKey, L.KeyStoredNote, new StackPanel { Children = { key, keyStatus } }));

        var model = new ComboBox { IsEditable = settings.Provider == Provider.OpenAICompatible, Width = 320 };
        modelBox = model;
        FillModels(Array.Empty<string>());
        model.SelectionChanged += (_, _) => { if (model.SelectedItem is string id) SaveModel(id); };
        model.LostFocus += (_, _) => { if (model.IsEditable) SaveModel(model.Text); };
        page.Children.Add(Card(SymbolRegular.Bot24, L.Model, L.ModelNote, model));

        Footnote(settings.Provider == Provider.Gemini ? L.GeminiNote : settings.EffectivePreset == ApiPreset.DeepSeek ? L.DeepSeekNote : L.OpenAINote);
        LoadModels();
    }

    private void BuildUsage()
    {
        var settings = Settings.Current;
        PageTitle(L.UsageSection);

        var target = Combo(Languages.All.Select(l => l.Label).ToArray(), Languages.All.ToList().FindIndex(l => l.Code == settings.TranslateTarget));
        target.SelectionChanged += (_, _) =>
        {
            settings.TranslateTarget = Languages.All[target.SelectedIndex].Code;
            settings.Save();
        };
        page.Children.Add(Card(SymbolRegular.Translate24, L.TranslateMenu, L.TranslateTargetNote, target));

        var reading = Combo(Languages.All.Select(l => l.Label).ToArray(), Languages.All.ToList().FindIndex(l => l.Code == settings.ScreenshotTarget));
        reading.SelectionChanged += (_, _) =>
        {
            settings.ScreenshotTarget = Languages.All[reading.SelectedIndex].Code;
            settings.Save();
        };
        page.Children.Add(Card(SymbolRegular.ScanText24, L.ScreenshotTarget, L.ScreenshotTargetNote, reading));

        var pill = new ToggleSwitch { IsChecked = settings.SelectionPill };
        pill.Click += (_, _) => { settings.SelectionPill = pill.IsChecked == true; settings.Save(); };
        page.Children.Add(Card(SymbolRegular.DesktopCursor24, L.SelectionPill, L.SelectionPillNote, pill));
    }

    private void BuildShortcuts()
    {
        PageTitle(L.ShortcutsSection);
        page.Children.Add(Card(SymbolRegular.Sparkle24, L.PolishShortcut, L.PolishShortcutNote, Recorder(ShortcutAction.Polish)));
        page.Children.Add(Card(SymbolRegular.Translate24, L.TranslateShortcut, L.TranslateShortcutNote, Recorder(ShortcutAction.Translate)));
        page.Children.Add(Card(SymbolRegular.ScanText24, L.ScreenshotShortcut, L.ScreenshotShortcutNote, Recorder(ShortcutAction.Screenshot)));
        Footnote(L.ShortcutsNote);
    }

    private void BuildGeneral()
    {
        var settings = Settings.Current;
        PageTitle(L.GeneralSection);

        var language = Combo(new[] { L.FollowSystem, "中文", "English" }, settings.UiLanguage switch { "zh" => 1, "en" => 2, _ => 0 });
        language.SelectionChanged += (_, _) =>
        {
            settings.UiLanguage = language.SelectedIndex switch { 1 => "zh", 2 => "en", _ => "system" };
            settings.Save();
            app.RefreshTray();
            // Rebuild after this event finishes, so the combo box isn't torn down mid-change.
            Rebuild();
        };
        page.Children.Add(Card(SymbolRegular.LocalLanguage24, L.UiLanguage, L.UiLanguageNote, language));

        var startup = new ToggleSwitch { IsChecked = Settings.StartsWithWindows };
        startup.Click += (_, _) => Settings.StartsWithWindows = startup.IsChecked == true;
        page.Children.Add(Card(SymbolRegular.Power24, L.StartWithWindows, L.StartWithWindowsNote, startup));

        var log = new Button { Content = L.Open, Icon = new SymbolIcon(SymbolRegular.Open24) };
        log.Click += (_, _) => DebugLog.Open();
        page.Children.Add(Card(SymbolRegular.Wrench24, L.DiagnosticLog, L.DiagnosticLogNote, log));

        var update = new Button { Content = L.CheckForUpdates, Icon = new SymbolIcon(SymbolRegular.ArrowDownload24) };
        update.Click += (_, _) => Updates.Check();
        page.Children.Add(Card(SymbolRegular.Info24, L.Version, "ZnKeyboard for Windows " + Updates.CurrentVersion, update));

        var automatic = new ToggleSwitch { IsChecked = settings.AutomaticUpdateChecks };
        automatic.Click += (_, _) =>
        {
            settings.AutomaticUpdateChecks = automatic.IsChecked == true;
            settings.Save();
        };
        page.Children.Add(Card(SymbolRegular.ArrowSync24, L.AutomaticUpdates, L.AutomaticUpdatesNote, automatic));
    }

    // ---- AI service helpers ----

    private void SaveModel(string id)
    {
        var settings = Settings.Current;
        if (string.IsNullOrWhiteSpace(id)) return;
        if (settings.Provider == Provider.Gemini) settings.GeminiModel = id; else settings.OpenAIModel = id.Trim();
        settings.Save();
    }

    /// <summary>The live list once loaded; until then the saved model (and the recommended one for Gemini).</summary>
    private void FillModels(string[] loaded)
    {
        if (modelBox == null) return;
        var settings = Settings.Current;
        var current = settings.ModelFor(settings.Provider);
        var options = loaded.ToList();
        if (settings.Provider == Provider.Gemini && !options.Contains(Settings.DefaultGeminiModel)) options.Insert(0, Settings.DefaultGeminiModel);
        if (!options.Contains(current)) options.Add(current);
        modelBox.ItemsSource = options;
        modelBox.SelectedItem = current;
        if (modelBox.IsEditable) modelBox.Text = current;
    }

    /// <summary>A coloured dot and a short line under the key.</summary>
    private void ShowKeyCheck(string brushKey, string text)
    {
        if (keyStatus == null) return;
        keyStatus.Inlines.Clear();
        var dot = new System.Windows.Documents.Run("● ");
        dot.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, brushKey);
        var line = new System.Windows.Documents.Run(text);
        line.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, brushKey == "SystemFillColorSuccessBrush" ? brushKey : brushKey);
        keyStatus.Inlines.Add(dot);
        keyStatus.Inlines.Add(line);
    }

    /// <summary>No key yet: where to get one for the current provider.</summary>
    private void ShowGetKeyLink()
    {
        if (keyStatus == null) return;
        keyStatus.Inlines.Clear();
        var settings = Settings.Current;
        if (settings.Provider == Provider.OpenAICompatible && settings.EffectivePreset == ApiPreset.Custom) return;
        var gemini = settings.Provider == Provider.Gemini;
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(gemini ? L.NoKeyYet : L.NoDeepSeekKeyYet))
        {
            NavigateUri = new Uri(gemini ? "https://aistudio.google.com/apikey" : "https://platform.deepseek.com/api_keys"),
        };
        link.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        link.RequestNavigate += (_, e) => Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        keyStatus.Inlines.Add(link);
    }

    /// <summary>Loads the model list, which also checks the key and reports it under the field.</summary>
    private async void LoadModels()
    {
        modelLoad?.Cancel();
        var cancel = new CancellationTokenSource();
        modelLoad = cancel;
        var client = Settings.Current.CreateClient();
        if (client.ApiKey.Length == 0)
        {
            ShowGetKeyLink();
            return;
        }
        ShowKeyCheck("TextFillColorTertiaryBrush", L.KeyChecking);
        try
        {
            var result = await client.ListModelsAsync(cancel.Token);
            if (cancel.IsCancellationRequested) return;
            if (result.Status is >= 200 and < 300)
            {
                ShowKeyCheck("SystemFillColorSuccessBrush", L.KeyWorks);
                if (result.Models.Length > 0) FillModels(result.Models);
            }
            else if (result.Reason?.Contains("location is not supported", StringComparison.OrdinalIgnoreCase) == true)
                ShowKeyCheck("SystemFillColorCriticalBrush", L.RegionUnsupported);
            else if (result.Status is 400 or 401 or 403)
                ShowKeyCheck("SystemFillColorCriticalBrush", L.KeyInvalid);
            else
                ShowKeyCheck("SystemFillColorCriticalBrush", L.KeyUnverified(result.Status == 0 ? L.NetworkFailed : result.Reason ?? $"HTTP {result.Status}"));
        }
        catch (Exception)
        {
            // Cancelled by a newer check.
        }
    }

    // ---- Shortcuts ----

    /// <summary>A key cap that, once clicked, takes the next key combination. Esc cancels.</summary>
    private UIElement Recorder(ShortcutAction action)
    {
        var button = new Button { MinWidth = 150, Padding = new Thickness(14, 6, 14, 6), FontWeight = FontWeights.SemiBold };
        var reset = new Button { Content = L.ResetDefault, Appearance = ControlAppearance.Transparent, Margin = new Thickness(0, 0, 8, 0) };
        reset.SetResourceReference(ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        var error = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        error.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        var recording = false;

        void Refresh()
        {
            button.Content = recording ? L.PressShortcut : Settings.Current.ShortcutFor(action).Display;
            button.Appearance = recording ? ControlAppearance.Primary : ControlAppearance.Secondary;
            reset.Visibility = !recording && Settings.Current.ShortcutFor(action) != Shortcut.Default(action) ? Visibility.Visible : Visibility.Collapsed;
            error.Visibility = string.IsNullOrEmpty(error.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
        void Stop()
        {
            recording = false;
            app.HotKeysPaused = false;
            Refresh();
        }
        void Apply(Shortcut? shortcut)
        {
            error.Text = app.ApplyShortcut(action, shortcut) switch
            {
                ShortcutResult.UsedByOther => L.ShortcutUsedByOther,
                ShortcutResult.Taken => L.ShortcutTaken,
                _ => "",
            };
            Refresh();
        }

        button.Click += (_, _) =>
        {
            recording = !recording;
            app.HotKeysPaused = recording;
            error.Text = "";
            Refresh();
        };
        button.PreviewKeyDown += (_, e) =>
        {
            if (!recording) return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { Stop(); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            var modifiers = Keyboard.Modifiers;
            if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
            {
                error.Text = L.NeedModifier;
                Refresh();
                return;
            }
            var flags = 0;
            if (modifiers.HasFlag(ModifierKeys.Control)) flags |= Shortcut.Control;
            if (modifiers.HasFlag(ModifierKeys.Alt)) flags |= Shortcut.Alt;
            if (modifiers.HasFlag(ModifierKeys.Shift)) flags |= Shortcut.Shift;
            if (modifiers.HasFlag(ModifierKeys.Windows)) flags |= Shortcut.Win;
            Stop();
            Apply(new Shortcut(flags, KeyInterop.VirtualKeyFromKey(key), KeyName(key)));
        };
        button.LostFocus += (_, _) => { if (recording) Stop(); };
        reset.Click += (_, _) => Apply(null);
        Refresh();

        var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { reset, button } };
        return new StackPanel { Children = { line, error } };
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (key - Key.NumPad0),
        Key.Space => "Space",
        Key.Return => "Enter",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        _ => key.ToString(),
    };

    // ---- Building blocks ----

    /// <summary>A navigation entry: icon and label, with the accent bar and fill of the Windows 11 Settings app when selected.</summary>
    private UIElement NavItem(Page target, SymbolRegular symbol, string label)
    {
        var selected = target == current;
        var bar = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 10, 0), Opacity = selected ? 1 : 0 };
        bar.SetResourceReference(Border.BackgroundProperty, "AccentTextFillColorPrimaryBrush");
        var icon = new SymbolIcon(symbol) { FontSize = 18, Margin = new Thickness(0, 0, 12, 0), Filled = selected };
        var text = new TextBlock { Text = label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { bar, icon, text } };
        var item = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 9, 12, 9), Margin = new Thickness(0, 1, 0, 1), Child = row, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        if (selected) item.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        item.MouseEnter += (_, _) => { if (!selected) item.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush"); };
        item.MouseLeave += (_, _) => { if (!selected) item.Background = Brushes.Transparent; };
        item.MouseLeftButtonUp += (_, _) =>
        {
            if (selected) return;
            current = target;
            Rebuild();
        };
        return item;
    }

    private void PageTitle(string text) => page.Children.Add(new TextBlock
    {
        Text = text,
        FontSize = 28,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 6, 0, 20),
    });

    /// <summary>A settings card: icon, title and one-line description on the left, the control on the right.</summary>
    private static CardControl Card(SymbolRegular symbol, string title, string? description, UIElement control)
    {
        var header = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        if (description != null)
        {
            var note = new TextBlock { Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 16, 0) };
            note.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            header.Children.Add(note);
        }
        return new CardControl
        {
            Icon = new SymbolIcon(symbol) { FontSize = 22 },
            Header = header,
            Content = control,
            Margin = new Thickness(0, 0, 0, 4),
            Padding = new Thickness(16, 14, 16, 14),
        };
    }

    private void Footnote(string text)
    {
        var note = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 12, 4, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        page.Children.Add(note);
    }

    private static ComboBox Combo(string[] items, int selected) => new()
    {
        ItemsSource = items,
        SelectedIndex = selected,
        MinWidth = 220,
    };
}
