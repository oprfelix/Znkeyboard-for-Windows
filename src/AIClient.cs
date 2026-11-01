using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ZnKeyboard;

/// <summary>
/// Polish only, polish and translate into <see cref="Target"/>, or (<see cref="IsRead"/>) a faithful
/// translation to read, for screenshot translation.
/// </summary>
public sealed record RewriteMode(TranslateLanguage? Target, bool IsRead = false)
{
    public static readonly RewriteMode Polish = new((TranslateLanguage?)null);
    public static RewriteMode Translate(TranslateLanguage target) => new(target);
    public static RewriteMode Read(TranslateLanguage target) => new(target, true);
}

public enum RewriteFailure { MissingKey, BadEndpoint, TooLong, NoChange, Http, BadResponse, Network }

public sealed class RewriteException(RewriteFailure failure, int status = 0, string body = "") : Exception(body)
{
    public RewriteFailure Failure { get; } = failure;
    public int Status { get; } = status;
    public string Body { get; } = body;

    /// <summary>The message to show the user.</summary>
    public string Describe() => Failure switch
    {
        RewriteFailure.MissingKey => L.NeedKey,
        RewriteFailure.BadEndpoint => L.BadEndpoint,
        RewriteFailure.TooLong => L.TooLong,
        RewriteFailure.NoChange => L.NoChange,
        RewriteFailure.Http when Status is 401 or 403 => L.KeyRejected,
        RewriteFailure.Http when ProviderMessage(Body) is { } reason && reason.Contains("location is not supported", StringComparison.OrdinalIgnoreCase)
            => L.RegionUnsupported,
        RewriteFailure.Http => L.RequestRejected(Status, ProviderMessage(Body)),
        RewriteFailure.Network => L.NetworkFailed,
        _ => L.BadResponse,
    };

    /// <summary>
    /// The provider's own reason from an error body: OpenAI-style {"error":{"message"}}, or Gemini's
    /// [{"error":{"message"}}]. Null when there is none.
    /// </summary>
    public static string? ProviderMessage(string body)
    {
        try
        {
            var root = JsonNode.Parse(body);
            var error = root is JsonArray list ? list.FirstOrDefault()?["error"] : root?["error"];
            var message = error is JsonValue ? (string?)error : (string?)error?["message"];
            return string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        }
        catch (Exception)
        {
            var text = body.Trim();
            return text.Length == 0 ? null : text;
        }
    }

    /// <summary>Key, model or endpoint problems are fixed in Settings.</summary>
    public bool FixableInSettings => Failure is RewriteFailure.MissingKey or RewriteFailure.BadEndpoint
        || (Failure == RewriteFailure.Http && Status is 400 or 401 or 403 or 404);
}

/// <summary>A /models answer: the models, the HTTP status (0: unreachable), and the provider's reason when it failed.</summary>
public sealed record ModelList(string[] Models, int Status, string? Reason);

/// <summary>
/// Talks to Gemini's OpenAI-compatible endpoint or any other OpenAI-compatible server, with the
/// same prompts and JSON contract as ZnKeyboard for Android and macOS.
/// </summary>
public sealed class AIClient
{
    public const int MaxSourceCharacters = 5_000;
    private const string GeminiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai";
    // A whole response, not a stream: translating a few thousand characters can take over a minute.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public Provider Provider { get; }
    public string ApiKey { get; }
    public string Model { get; }
    private readonly string? baseUrl;

    public AIClient(Provider provider, string openAIBaseUrl, string apiKey, string model)
    {
        Provider = provider;
        ApiKey = apiKey.Trim();
        Model = model;
        if (provider == Provider.Gemini)
        {
            baseUrl = GeminiBaseUrl;
        }
        else
        {
            var trimmed = (openAIBaseUrl ?? "").Trim().TrimEnd('/');
            baseUrl = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? trimmed : null;
        }
    }

    public bool HasValidEndpoint => baseUrl != null;

    public async Task<string> RewriteAsync(string text, RewriteMode mode, CancellationToken cancel)
    {
        if (ApiKey.Length == 0) throw new RewriteException(RewriteFailure.MissingKey);
        if (baseUrl == null) throw new RewriteException(RewriteFailure.BadEndpoint);
        if (text.Length > MaxSourceCharacters) throw new RewriteException(RewriteFailure.TooLong);
        try
        {
            return await RequestAsync(text, mode, strictSchema: true, cancel);
        }
        catch (RewriteException e) when (e.Failure == RewriteFailure.Http && e.Status is 400 or 404 or 422 && MentionsSchema(e.Body))
        {
            // Some servers (DeepSeek, older OpenAI-compatible ones) only take plain JSON mode.
            return await RequestAsync(text, mode, strictSchema: false, cancel);
        }
    }

    private async Task<string> RequestAsync(string text, RewriteMode mode, bool strictSchema, CancellationToken cancel)
    {
        JsonNode format = strictSchema
            ? new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "znkeyboard_rewrite_response",
                    ["strict"] = true,
                    ["schema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new JsonObject
                        {
                            ["text"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "The final rewritten text only, without labels, explanations, or prefaces.",
                            },
                        },
                        ["required"] = new JsonArray("text"),
                    },
                },
            }
            : new JsonObject { ["type"] = "json_object" };

        var body = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = Prompts.System(mode) },
                new JsonObject { ["role"] = "user", ["content"] = text }),
            ["temperature"] = 0.4,
            ["max_tokens"] = MaxTokens(text),
            ["response_format"] = format,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        string responseText;
        int status;
        try
        {
            using var response = await Http.SendAsync(request, cancel);
            status = (int)response.StatusCode;
            responseText = await response.Content.ReadAsStringAsync(cancel);
        }
        catch (HttpRequestException) { throw new RewriteException(RewriteFailure.Network); }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested) { throw new RewriteException(RewriteFailure.Network); }

        if (status is < 200 or >= 300)
        {
            // The provider's reason, never the user's text, so colleagues can send the log.
            var reason = RewriteException.ProviderMessage(responseText) ?? "";
            DebugLog.Note($"{Provider} {Model}: HTTP {status} ({(strictSchema ? "json_schema" : "json_object")}) {(reason.Length > 200 ? reason[..200] : reason)}");
            throw new RewriteException(RewriteFailure.Http, status, responseText.Length > 2000 ? responseText[..2000] : responseText);
        }

        try
        {
            var choice = JsonNode.Parse(responseText)?["choices"]?[0] ?? throw new RewriteException(RewriteFailure.BadResponse);
            // A cut-off answer is half a JSON object; say why instead of reporting a parse error.
            if ((string?)choice["finish_reason"] == "length") throw new RewriteException(RewriteFailure.TooLong);
            var content = (string?)choice["message"]?["content"] ?? throw new RewriteException(RewriteFailure.BadResponse);
            var result = ((string?)JsonNode.Parse(StripFences(content))?["text"])?.Trim();
            if (string.IsNullOrEmpty(result)) throw new RewriteException(RewriteFailure.BadResponse);
            // Text already in the reading language comes back as is, which is fine for reading.
            if (!mode.IsRead && result == text.Trim()) throw new RewriteException(RewriteFailure.NoChange);
            return result;
        }
        catch (JsonException) { throw new RewriteException(RewriteFailure.BadResponse); }
    }

    /// <summary>Models this key can use: Flash-Lite only for Gemini, everything the server lists otherwise.</summary>
    /// <remarks>Loading the list is also the settings window's key check: <see cref="ModelList.Status"/> is 0 when the server could not be reached.</remarks>
    public async Task<ModelList> ListModelsAsync(CancellationToken cancel)
    {
        if (ApiKey.Length == 0 || baseUrl == null) return new([], 0, null);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        try
        {
            using var response = await Http.SendAsync(request, cancel);
            var text = await response.Content.ReadAsStringAsync(cancel);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode) return new([], status, RewriteException.ProviderMessage(text));
            var ids = JsonNode.Parse(text)?["data"]?.AsArray().Select(m => (string?)m?["id"]).OfType<string>().ToArray() ?? [];
            return new(Provider == Provider.Gemini ? GeminiModels.LiteModels(ids) : ids.Distinct().OrderBy(id => id).ToArray(), status, null);
        }
        catch (HttpRequestException) { return new([], 0, null); }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested) { return new([], 0, null); }
        catch (JsonException) { return new([], 200, null); }
    }

    private static bool MentionsSchema(string body)
    {
        var lower = body.ToLowerInvariant();
        return lower.Contains("response_format") || lower.Contains("json_schema") || lower.Contains("schema");
    }

    /// <summary>
    /// A CJK character can cost a whole token while Latin text packs about three characters per
    /// token; translation can lengthen text, hence the margin. Same budget as Android and macOS.
    /// </summary>
    public static int MaxTokens(string text)
    {
        var cjk = text.Count(c => c >= 0x2E80);
        var estimate = cjk + (text.Length - cjk) / 3;
        return Math.Clamp(estimate * 3 / 2 + 200, 2_000, 8_000);
    }

    /// <summary>Some models wrap JSON in ``` fences despite the schema.</summary>
    private static string StripFences(string content)
    {
        var text = content.Trim();
        if (!text.StartsWith("```")) return text;
        var newline = text.IndexOf('\n');
        text = newline >= 0 ? text[(newline + 1)..] : "";
        return text.EndsWith("```") ? text[..^3] : text;
    }
}

/// <summary>System prompts embedded in the exe, copied from ZnKeyboard for Android.</summary>
public static class Prompts
{
    public static string System(RewriteMode mode)
    {
        if (mode.IsRead && mode.Target is { } reading)
            return (Load("read") ?? ReadFallback).Replace("{language}", reading.Name);
        var parts = new System.Collections.Generic.List<string> { Load("rewrite_base") ?? Fallback };
        if (mode.Target is { } target && Load("translate") is { } translate)
        {
            // Last, so it overrides the language-matching rules above.
            parts.Add(translate.Replace("{language}", target.Name));
        }
        return string.Join("\n\n---\n\n", parts);
    }

    private static string? Load(string name)
    {
        using var stream = typeof(Prompts).Assembly.GetManifestResourceStream($"prompts.{name}.md");
        if (stream == null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Trim();
    }

    private const string ReadFallback = "Translate the user's text faithfully into {language}, keeping its meaning, facts and line breaks. " +
        "Return a valid JSON object with exactly one field, `text`, containing only the translation.";

    private const string Fallback = "Polish the user's text while preserving its meaning, tone, and language. " +
        "Return a valid JSON object with exactly one field, `text`, containing only the rewritten text.";
}
