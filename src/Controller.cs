using System;
using System.Linq;
using System.Threading;

namespace ZnKeyboard;

/// <summary>One shortcut press: read the selection, ask the AI, show the result, replace on Enter.</summary>
public sealed class Controller
{
    private readonly ReviewWindow panel = new();
    private CancellationTokenSource? running;

    public Action OpenSettings { get; set; } = () => { };

    public bool IsPanelVisible => panel.IsVisible;

    public Controller()
    {
        panel.OnOpenSettings = () => OpenSettings();
    }

    public async void Run(RewriteMode mode)
    {
        running?.Cancel();
        var cancel = new CancellationTokenSource();
        running = cancel;

        // The window whose text is selected; the paste must land there.
        var source = Native.GetForegroundWindow();
        if (Native.IsOwnWindow(source)) source = IntPtr.Zero;

        var client = Settings.Current.CreateClient();
        if (client.ApiKey.Length == 0 || !client.HasValidEndpoint)
        {
            panel.ShowError(client.ApiKey.Length == 0 ? L.NeedKey : L.BadEndpoint, offerSettings: true);
            return;
        }

        // Read the selection before showing anything: once the panel takes focus, Ctrl+C would copy from it.
        var selection = await TextBridge.SelectedTextAsync();
        if (cancel.IsCancellationRequested) return;
        if (string.IsNullOrWhiteSpace(selection))
        {
            panel.ShowError(L.NoSelection, offerSettings: false);
            return;
        }

        panel.OnReplace = text => _ = TextBridge.ReplaceSelectionAsync(source, selection, text);
        // Picking another language in the card translates the same selection again and remembers it.
        panel.OnLanguageChange = language =>
        {
            Settings.Current.TranslateTarget = language.Code;
            Settings.Current.Save();
            Process(selection, RewriteMode.Translate(language), client);
        };
        Process(selection, mode, client);
    }

    private async void Process(string selection, RewriteMode mode, AIClient client)
    {
        running?.Cancel();
        var cancel = new CancellationTokenSource();
        running = cancel;
        panel.ShowLoading(L.Polish, mode.Target);
        var (leading, core, trailing) = SplitWhitespace(selection);
        try
        {
            var result = await client.RewriteAsync(core, mode, cancel.Token);
            if (cancel.IsCancellationRequested || !panel.IsVisible) return;
            // Keep the selection's surrounding spaces and line breaks so the paste fits back in.
            panel.ShowResult(leading + result + trailing);
        }
        catch (RewriteException e)
        {
            if (!cancel.IsCancellationRequested && panel.IsVisible) panel.ShowError(e.Describe(), e.FixableInSettings);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (!cancel.IsCancellationRequested && panel.IsVisible) panel.ShowError(L.NetworkFailed, offerSettings: false);
        }
    }

    /// <summary>Splits off leading and trailing whitespace, which the model would drop.</summary>
    private static (string leading, string core, string trailing) SplitWhitespace(string text)
    {
        var leading = new string(text.TakeWhile(char.IsWhiteSpace).ToArray());
        var trailing = new string(text.Reverse().TakeWhile(char.IsWhiteSpace).Reverse().ToArray());
        return (leading, text[leading.Length..^trailing.Length], trailing);
    }
}
