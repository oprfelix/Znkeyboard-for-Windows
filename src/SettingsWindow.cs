using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace ZnKeyboard;

/// <summary>
/// Settings in four cards: AI service (provider, key, model), usage (target language, shortcuts),
/// general (language, start with Windows, version). Rebuilt whole when the UI language changes.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly App app;
    private readonly StackPanel root = new() { Margin = new Thickness(20, 16, 20, 20) };
    private CancellationTokenSource? modelLoad;

    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x6B, 0x6F, 0x7A));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x23, 0x40, 0x80));

    public SettingsWindow(App app)
    {
        this.app = app;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 13;
        Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF7));
        Icon = App.IconImage();
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        Build();
    }

    private void Build()
    {
        Title = L.SettingsTitle;
        root.Children.Clear();
        var settings = Settings.Current;

        // AI service
        var ai = Card(L.AiSection);
        var provider = new ComboBox { ItemsSource = new[] { "Google Gemini", L.OpenAICompatible }, SelectedIndex = (int)settings.Provider };
        provider.SelectionChanged += (_, _) =>
        {
            settings.Provider = (Provider)provider.SelectedIndex;
            settings.Save();
            Build();
        };
        ai.Children.Add(Row(L.Provider, provider));

        if (settings.Provider == Provider.OpenAICompatible)
        {
            var baseUrl = new TextBox { Text = settings.OpenAIBaseUrl, Padding = new Thickness(4, 3, 4, 3) };
            baseUrl.LostFocus += (_, _) => { settings.OpenAIBaseUrl = baseUrl.Text.Trim(); settings.Save(); LoadModels(); };
            ai.Children.Add(Row(L.ApiBaseUrl, baseUrl));
        }

        var key = new PasswordBox { Password = Credentials.Read(settings.Provider) ?? "", Padding = new Thickness(4, 3, 4, 3) };
        var save = new Button { Content = L.Save, Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(8, 0, 0, 0) };
        var keyStatus = new TextBlock { FontSize = 12, Margin = new Thickness(0, 0, 0, 10) };
        void ShowKeyStatus()
        {
            keyStatus.Inlines.Clear();
            if (!string.IsNullOrEmpty(Credentials.Read(settings.Provider)))
            {
                keyStatus.Inlines.Add(new Run(L.KeyStored) { Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)) });
            }
            else if (settings.Provider == Provider.Gemini)
            {
                var link = new Hyperlink(new Run(L.NoKeyYet)) { NavigateUri = new Uri("https://aistudio.google.com/apikey") };
                link.RequestNavigate += (_, e) => Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                keyStatus.Inlines.Add(link);
            }
        }
        void SaveKey()
        {
            Credentials.Write(settings.Provider, key.Password);
            ShowKeyStatus();
            LoadModels();
        }
        save.Click += (_, _) => SaveKey();
        key.KeyDown += (_, e) => { if (e.Key == Key.Enter) SaveKey(); };
        var keyRow = new DockPanel();
        DockPanel.SetDock(save, Dock.Right);
        keyRow.Children.Add(save);
        keyRow.Children.Add(key);
        ai.Children.Add(Row(L.ApiKey, keyRow));
        ShowKeyStatus();
        ai.Children.Add(Indented(keyStatus));

        var model = new ComboBox { IsEditable = settings.Provider == Provider.OpenAICompatible };
        modelBox = model;
        FillModels(Array.Empty<string>());
        model.SelectionChanged += (_, _) => { if (model.SelectedItem is string id) SaveModel(id); };
        model.LostFocus += (_, _) => { if (model.IsEditable) SaveModel(model.Text); };
        ai.Children.Add(Row(L.Model, model));
        ai.Children.Add(Note((settings.Provider == Provider.Gemini ? L.GeminiNote : L.OpenAINote) + " " + L.PrivacyNote));
        LoadModels();

        // Usage
        var usage = Card(L.UsageSection);
        var target = new ComboBox
        {
            ItemsSource = Languages.All.Select(l => l.Label).ToList(),
            SelectedIndex = Languages.All.ToList().FindIndex(l => l.Code == settings.TranslateTarget),
        };
        target.SelectionChanged += (_, _) =>
        {
            settings.TranslateTarget = Languages.All[target.SelectedIndex].Code;
            settings.Save();
        };
        usage.Children.Add(Row(L.TranslateMenu, target));
        usage.Children.Add(Row(L.PolishShortcut, Recorder(ShortcutAction.Polish)));
        usage.Children.Add(Row(L.TranslateShortcut, Recorder(ShortcutAction.Translate)));
        usage.Children.Add(Note(L.ShortcutsNote));

        // General
        var general = Card(L.GeneralSection);
        var language = new ComboBox
        {
            ItemsSource = new[] { L.FollowSystem, "中文", "English" },
            SelectedIndex = settings.UiLanguage switch { "zh" => 1, "en" => 2, _ => 0 },
        };
        language.SelectionChanged += (_, _) =>
        {
            settings.UiLanguage = language.SelectedIndex switch { 1 => "zh", 2 => "en", _ => "system" };
            settings.Save();
            app.RefreshTray();
            // Rebuild after this event finishes, so the combo box isn't torn down mid-change.
            Dispatcher.BeginInvoke(Build);
        };
        general.Children.Add(Row(L.UiLanguage, language));
        var startup = new CheckBox { Content = L.StartWithWindows, IsChecked = Settings.StartsWithWindows, Margin = new Thickness(0, 4, 0, 8) };
        startup.Click += (_, _) => Settings.StartsWithWindows = startup.IsChecked == true;
        general.Children.Add(Indented(startup));
        general.Children.Add(Row(L.Version, new TextBlock { Text = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "–", VerticalAlignment = VerticalAlignment.Center }));
    }

    private ComboBox? modelBox;

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

    private async void LoadModels()
    {
        modelLoad?.Cancel();
        var cancel = new CancellationTokenSource();
        modelLoad = cancel;
        try
        {
            var models = await Settings.Current.CreateClient().ModelsAsync(cancel.Token);
            if (!cancel.IsCancellationRequested && models.Length > 0) FillModels(models);
        }
        catch (Exception)
        {
            // The list is a convenience; the field keeps the saved model.
        }
    }

    /// <summary>A key cap that, once clicked, takes the next key combination. Esc cancels.</summary>
    private UIElement Recorder(ShortcutAction action)
    {
        var button = new Button { Content = Settings.Current.ShortcutFor(action).Display, MinWidth = 140, Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left };
        var reset = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var resetLink = new Hyperlink(new Run(L.ResetDefault));
        reset.Inlines.Add(resetLink);
        var error = new TextBlock { Foreground = Brushes.Firebrick, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var recording = false;

        void Refresh()
        {
            button.Content = recording ? L.PressShortcut : Settings.Current.ShortcutFor(action).Display;
            reset.Visibility = !recording && Settings.Current.ShortcutFor(action) != Shortcut.Default(action) ? Visibility.Visible : Visibility.Collapsed;
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
        resetLink.Click += (_, _) => Apply(null);
        Refresh();

        var line = new StackPanel { Orientation = Orientation.Horizontal, Children = { button, reset } };
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

    private StackPanel Card(string heading)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = heading, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Accent, Margin = new Thickness(0, 0, 0, 10) });
        root.Children.Add(new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = content,
        });
        return content;
    }

    private static UIElement Row(string label, UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(text);
        Grid.SetColumn((FrameworkElement)control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private static UIElement Indented(FrameworkElement element)
    {
        element.Margin = new Thickness(120, element.Margin.Top, 0, element.Margin.Bottom);
        return element;
    }

    private static UIElement Note(string text) => new TextBlock
    {
        Text = text,
        Foreground = Muted,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 0),
    };
}
