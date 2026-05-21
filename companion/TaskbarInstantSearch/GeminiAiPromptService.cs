using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TaskbarInstantSearch;

internal sealed class CerebrasAiPromptService
{
    private readonly AiConfig _config;
    private readonly HttpClient _httpClient = new();

    public CerebrasAiPromptService(AiConfig config)
    {
        _config = config;
    }

    public async Task<AiPromptResult> PromptAsync(string input, CancellationToken cancellationToken)
    {
        if (!_config.Enabled)
        {
            return AiPromptResult.Failed(AiPromptFailureKind.Disabled);
        }

        string apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return AiPromptResult.Failed(AiPromptFailureKind.MissingKey);
        }

        int maxOutputTokens = Math.Max(_config.MaxOutputTokens, 512);
        AiPromptResult result = await PromptModelAsync(
            _config.Model,
            apiKey,
            LoadSystemPrompt(),
            input,
            maxOutputTokens,
            cancellationToken);

        if (result.FailureKind != AiPromptFailureKind.EmptyResponse)
        {
            return result;
        }

        int retryMaxOutputTokens = Math.Max(maxOutputTokens * 2, 1024);
        Logger.Info($"AI response from Cerebras model '{_config.Model}' had no final content; retrying with {retryMaxOutputTokens} max tokens");
        return await PromptModelAsync(
            _config.Model,
            apiKey,
            LoadSystemPrompt(),
            input,
            retryMaxOutputTokens,
            cancellationToken);
    }

    private async Task<AiPromptResult> PromptModelAsync(
        string model,
        string apiKey,
        string systemPrompt,
        string input,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_config.TimeoutMs);

            string endpoint = $"{_config.BaseUrl}/chat/completions";
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            request.Content = JsonContent.Create(new
            {
                model,
                messages = new[]
                {
                    new
                    {
                        role = "system",
                        content = systemPrompt
                    },
                    new
                    {
                        role = "user",
                        content = input
                    }
                },
                temperature = _config.Temperature,
                max_tokens = maxOutputTokens,
                stream = false,
                response_format = new { type = "text" },
                reasoning_effort = "low"
            });

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Logger.Error($"AI request was rate limited by Cerebras model '{model}'");
                return AiPromptResult.Failed(AiPromptFailureKind.RateLimited);
            }

            if ((int)response.StatusCode >= 500)
            {
                Logger.Error($"AI request failed with server status {(int)response.StatusCode} for Cerebras model '{model}': {await GetResponsePreview(response, timeoutCts.Token)}");
                return AiPromptResult.Failed(AiPromptFailureKind.Error);
            }

            if (!response.IsSuccessStatusCode)
            {
                Logger.Error($"AI request failed with status {(int)response.StatusCode} for Cerebras model '{model}': {await GetResponsePreview(response, timeoutCts.Token)}");
                return AiPromptResult.Failed(AiPromptFailureKind.Error);
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);
            string text = ExtractText(document).Trim();
            text = TrimMarkdownFence(text);
            return string.IsNullOrWhiteSpace(text)
                ? AiPromptResult.Failed(AiPromptFailureKind.EmptyResponse)
                : AiPromptResult.Succeeded(text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AiPromptResult.Failed(AiPromptFailureKind.Canceled);
        }
        catch (OperationCanceledException exception)
        {
            Logger.Error($"AI request timed out for Cerebras model '{model}'", exception);
            return AiPromptResult.Failed(AiPromptFailureKind.Error);
        }
        catch (Exception exception)
        {
            Logger.Error($"AI request failed for Cerebras model '{model}'", exception);
            return AiPromptResult.Failed(AiPromptFailureKind.Error);
        }
    }

    private static async Task<string> GetResponsePreview(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            string text = await response.Content.ReadAsStringAsync(cancellationToken);
            text = text.ReplaceLineEndings(" ").Trim();
            return text.Length <= 500 ? text : text[..500];
        }
        catch
        {
            return "";
        }
    }

    private string GetApiKey()
    {
        string apiKey = Environment.GetEnvironmentVariable(_config.ApiKeyEnvironmentVariable) ?? "";
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return apiKey;
        }

        apiKey = Environment.GetEnvironmentVariable(_config.ApiKeyEnvironmentVariable, EnvironmentVariableTarget.User) ?? "";
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return apiKey;
        }

        return Environment.GetEnvironmentVariable(_config.ApiKeyEnvironmentVariable, EnvironmentVariableTarget.Machine) ?? "";
    }

    private string LoadSystemPrompt()
    {
        try
        {
            return File.ReadAllText(_config.PromptPath);
        }
        catch (Exception exception)
        {
            Logger.Error("Failed to load AI prompt file; using built-in prompt", exception);
            return """
You are the inline answer engine for TaskbarInstantSearch.
Return only the final answer. Be concise.
For arithmetic, output only the result.
Use LaTeX only when it makes the answer clearer.
""";
        }
    }

    private static string ExtractText(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return "";
        }

        JsonElement choice = choices[0];
        if (choice.TryGetProperty("text", out JsonElement choiceText) &&
            choiceText.ValueKind == JsonValueKind.String)
        {
            return choiceText.GetString() ?? "";
        }

        if (!choice.TryGetProperty("message", out JsonElement message) ||
            !message.TryGetProperty("content", out JsonElement content))
        {
            return "";
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? "";
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            return string.Join("", content.EnumerateArray().Select(ExtractContentPartText));
        }

        return content.ToString();
    }

    private static string ExtractContentPartText(JsonElement part)
    {
        if (part.ValueKind == JsonValueKind.String)
        {
            return part.GetString() ?? "";
        }

        if (part.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        if (part.TryGetProperty("text", out JsonElement text) &&
            text.ValueKind == JsonValueKind.String)
        {
            return text.GetString() ?? "";
        }

        return "";
    }

    private static string TrimMarkdownFence(string text)
    {
        string trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        string[] lines = trimmed.Split('\n');
        if (lines.Length >= 2 && lines[^1].TrimEnd().Equals("```", StringComparison.Ordinal))
        {
            return string.Join('\n', lines.Skip(1).SkipLast(1)).Trim();
        }

        return trimmed;
    }
}

internal sealed class AiPromptResult
{
    private AiPromptResult(bool success, string text, AiPromptFailureKind failureKind)
    {
        Success = success;
        Text = text;
        FailureKind = failureKind;
    }

    public bool Success { get; }
    public string Text { get; }
    public AiPromptFailureKind FailureKind { get; }
    public bool UsedFallback { get; set; }

    public static AiPromptResult Succeeded(string text) =>
        new(true, text, AiPromptFailureKind.None);

    public static AiPromptResult Failed(AiPromptFailureKind failureKind) =>
        new(false, "", failureKind);
}

internal enum AiPromptFailureKind
{
    None,
    Disabled,
    MissingKey,
    RateLimited,
    EmptyResponse,
    Error,
    Canceled
}
