using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace TaskbarInstantSearch;

internal sealed class AppConfig
{
    public string Hotkey { get; set; } = "Alt+Space";
    public OverlayConfig Overlay { get; set; } = new();
    public AiConfig Ai { get; set; } = new();
    public MathConfig Math { get; set; } = new();
    public List<ActionConfig> Actions { get; set; } = new();

    [JsonIgnore]
    public IReadOnlyDictionary<string, ActionConfig> ValidActions { get; private set; } =
        new Dictionary<string, ActionConfig>(StringComparer.Ordinal);

    [JsonIgnore]
    public IReadOnlyList<string> AutocompleteTriggers { get; private set; } =
        new List<string>();

    [JsonIgnore]
    public IReadOnlySet<string> ConfirmationInputs { get; private set; } =
        new HashSet<string>(StringComparer.Ordinal);

    [JsonIgnore]
    public IReadOnlyDictionary<string, string> ConfirmationInputTriggers { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public static string ConfigPath =>
        Path.Combine(Logger.AppDirectory, "config.json");

    public static AppConfig LoadOrCreate()
    {
        Directory.CreateDirectory(Logger.AppDirectory);

        if (!File.Exists(ConfigPath))
        {
            var defaultConfig = new AppConfig
            {
                Actions =
                {
                    new ActionConfig
                    {
                        Trigger = "note",
                        Type = "command",
                        Command = "notepad.exe"
                    }
                }
            };

            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(defaultConfig, JsonOptions()));
        }

        try
        {
            var config = JsonSerializer.Deserialize<AppConfig>(
                File.ReadAllText(ConfigPath), JsonOptions()) ?? new AppConfig();
            config.Validate();
            return config;
        }
        catch (Exception exception)
        {
            Logger.Error("Failed to load config; using defaults", exception);
            var fallback = new AppConfig();
            fallback.Validate();
            return fallback;
        }
    }

    private void Validate()
    {
        Hotkey ??= "Alt+Space";
        Overlay ??= new OverlayConfig();
        Ai ??= new AiConfig();
        Math ??= new MathConfig();
        Ai.Normalize();
        Math.Normalize();
        Actions ??= new List<ActionConfig>();
        AddGeneratedPdfActions();

        var validActions = new Dictionary<string, ActionConfig>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (ActionConfig action in Actions)
        {
            action.Type ??= "command";
            action.Command ??= "";
            action.Text ??= "";
            action.Trigger ??= "";
            action.Alias ??= "";
            action.Arguments ??= new List<string>();
            action.ReuseProcessNames ??= new List<string>();
            action.ReuseWindowClasses ??= new List<string>();
            action.ExactMatchBehavior = NormalizeActionBehavior(action.ExactMatchBehavior, "keepOpen");
            action.TabCompletionBehavior = NormalizeActionBehavior(action.TabCompletionBehavior, "close");

            if (string.IsNullOrWhiteSpace(action.Trigger))
            {
                Logger.Error("Skipping action with empty trigger");
                continue;
            }

            if (!seen.Add(action.Trigger))
            {
                Logger.Error($"Skipping duplicate trigger '{action.Trigger}'");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(action.Alias) && !seen.Add(action.Alias))
            {
                Logger.Error($"Skipping trigger '{action.Trigger}' because alias '{action.Alias}' is duplicated");
                continue;
            }

            if (!IsSupportedActionType(action.Type))
            {
                Logger.Error($"Skipping trigger '{action.Trigger}' with unsupported type '{action.Type}'");
                continue;
            }

            if (RequiresCommand(action.Type) &&
                string.IsNullOrWhiteSpace(action.Command))
            {
                Logger.Error($"Skipping trigger '{action.Trigger}' with empty command");
                continue;
            }

            foreach (string existing in validActions.Keys)
            {
                if (action.Trigger.StartsWith(existing, StringComparison.Ordinal) ||
                    existing.StartsWith(action.Trigger, StringComparison.Ordinal))
                {
                    Logger.Info(
                        $"Prefix conflict: '{action.Trigger}' and '{existing}'. Immediate exact matching may make the longer trigger unreachable.");
                }
            }

            validActions[action.Trigger] = action;
            if (!string.IsNullOrWhiteSpace(action.Alias))
            {
                validActions[action.Alias] = action;
            }
        }

        ValidActions = validActions;
        AutocompleteTriggers = Actions
            .Where(action => !string.IsNullOrWhiteSpace(action.Trigger) &&
                             validActions.TryGetValue(action.Trigger, out ActionConfig? validAction) &&
                             ReferenceEquals(validAction, action))
            .Select(action => action.Trigger!)
            .ToList();
        var confirmationInputs = new HashSet<string>(StringComparer.Ordinal);
        var confirmationInputTriggers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ActionConfig action in Actions)
        {
            if (!action.RequiresConfirmation ||
                string.IsNullOrWhiteSpace(action.Trigger) ||
                !validActions.TryGetValue(action.Trigger, out ActionConfig? validAction) ||
                !ReferenceEquals(validAction, action))
            {
                continue;
            }

            confirmationInputs.Add(action.Trigger);
            confirmationInputTriggers[action.Trigger] = action.Trigger;
            if (!string.IsNullOrWhiteSpace(action.Alias))
            {
                confirmationInputs.Add(action.Alias);
                confirmationInputTriggers[action.Alias] = action.Trigger;
            }
        }

        ConfirmationInputs = confirmationInputs;
        ConfirmationInputTriggers = confirmationInputTriggers;

        if (!HotkeyManager.TryParseHotkey(Hotkey, out _, out _))
        {
            Logger.Error($"Invalid hotkey '{Hotkey}'. Hotkey registration will be skipped.");
        }
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static bool IsSupportedActionType(string? type)
    {
        return string.Equals(type, "command", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "generatedPdf", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "showDesktop", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "selfRestart", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "sleepComputer", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "closeActiveWindow", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "minimizeActiveWindow", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "maximizeActiveWindow", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "copyText", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "prefixMenu", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresCommand(string? type)
    {
        return string.Equals(type, "command", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "generatedPdf", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeActionBehavior(string? behavior, string fallback)
    {
        if (string.Equals(behavior, "keepOpen", StringComparison.OrdinalIgnoreCase))
        {
            return "keepOpen";
        }

        if (string.Equals(behavior, "close", StringComparison.OrdinalIgnoreCase))
        {
            return "close";
        }

        return fallback;
    }

    private void AddGeneratedPdfActions()
    {
        Actions.RemoveAll(action =>
            string.Equals(action.Type, "generatedPdf", StringComparison.OrdinalIgnoreCase));

        string pdfDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "pdf");
        if (!Directory.Exists(pdfDirectory))
        {
            Logger.Info($"PDF action folder does not exist: {pdfDirectory}");
            return;
        }

        string sioyekPath = FindSioyekExecutable();
        string? sioyekDirectory = Path.GetDirectoryName(sioyekPath);
        foreach (string pdfPath in Directory.EnumerateFiles(pdfDirectory, "*.pdf", SearchOption.TopDirectoryOnly))
        {
            string trigger = "pdf:" + Path.GetFileNameWithoutExtension(pdfPath);
            Actions.Add(new ActionConfig
            {
                Trigger = trigger,
                Type = "generatedPdf",
                Command = sioyekPath,
                WorkingDirectory = sioyekDirectory,
                Arguments = new List<string> { pdfPath }
            });
        }
    }

    private static string FindSioyekExecutable()
    {
        string downloadsSioyek = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "sioyek-release-windows",
            "sioyek-release-windows",
            "sioyek.exe");
        if (File.Exists(downloadsSioyek))
        {
            return downloadsSioyek;
        }

        return "sioyek.exe";
    }
}

internal sealed class OverlayConfig
{
    public bool CloseOnFocusLost { get; set; } = true;
    public string Theme { get; set; } = "system";
    public int LeftPadding { get; set; } = 0;
    public int Width { get; set; } = 360;
    public int Height { get; set; } = 36;
}

internal sealed class AiConfig
{
    public bool Enabled { get; set; } = true;
    public string Provider { get; set; } = "cerebras";
    public string ApiKeyEnvironmentVariable { get; set; } = "CEREBRAS_API_KEY";
    public string BaseUrl { get; set; } = "https://api.cerebras.ai/v1";
    public string Model { get; set; } = "gpt-oss-120b";
    public string PromptPath { get; set; } =
        Path.Combine(Logger.AppDirectory, "ai-prompt.md");
    public double Temperature { get; set; } = 0.1;
    public int MaxOutputTokens { get; set; } = 512;
    public int TimeoutMs { get; set; } = 10000;
    public string ThinkingSuffix { get; set; } = " ...";
    public string RateLimitSuffix { get; set; } = " [rate-limit]";
    public string MissingKeySuffix { get; set; } = " [ai-key]";
    public string ErrorSuffix { get; set; } = " [ai-error]";

    public void Normalize()
    {
        Provider = string.IsNullOrWhiteSpace(Provider)
            ? "cerebras"
            : Provider.Trim();
        ApiKeyEnvironmentVariable = string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariable)
            ? "CEREBRAS_API_KEY"
            : ApiKeyEnvironmentVariable.Trim();
        BaseUrl = string.IsNullOrWhiteSpace(BaseUrl)
            ? "https://api.cerebras.ai/v1"
            : BaseUrl.Trim().TrimEnd('/');
        Model = string.IsNullOrWhiteSpace(Model)
            ? "gpt-oss-120b"
            : Model.Trim();
        PromptPath = string.IsNullOrWhiteSpace(PromptPath)
            ? Path.Combine(Logger.AppDirectory, "ai-prompt.md")
            : Environment.ExpandEnvironmentVariables(PromptPath);
        MaxOutputTokens = Math.Clamp(MaxOutputTokens, 16, 2048);
        TimeoutMs = Math.Clamp(TimeoutMs, 3000, 60000);
        ThinkingSuffix = string.IsNullOrEmpty(ThinkingSuffix) ? " ..." : ThinkingSuffix;
        RateLimitSuffix = string.IsNullOrEmpty(RateLimitSuffix) ? " [rate-limit]" : RateLimitSuffix;
        MissingKeySuffix = string.IsNullOrEmpty(MissingKeySuffix) ? " [ai-key]" : MissingKeySuffix;
        ErrorSuffix = string.IsNullOrEmpty(ErrorSuffix) ? " [ai-error]" : ErrorSuffix;

        try
        {
            string? directory = Path.GetDirectoryName(PromptPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(PromptPath))
            {
                File.WriteAllText(PromptPath, DefaultPrompt);
            }
        }
        catch (Exception exception)
        {
            Logger.Error("Failed to create default AI prompt file", exception);
        }
    }

    private const string DefaultPrompt = """
You are the inline answer engine for TaskbarInstantSearch.

Return only the final answer. Be concise.
Prefer one word or one short phrase when that is enough.
For arithmetic, output only the result.
For unit conversions, output only the converted value and unit.
For dates, output the concise date and weekday when useful.

For mathematical expressions:
- Prefer plain text for simple answers.
- Use LaTeX only when it makes the answer clearer.
- If returning LaTeX, return only the LaTeX expression, without markdown fences.
- Do not wrap inline math in prose.
- Do not include derivations unless explicitly requested.
""";
}

internal sealed class MathConfig
{
    public bool Enabled { get; set; } = true;
    public bool RenderAiLatexResponses { get; set; } = true;
    public string Notation { get; set; } = "latex";
    public bool FallbackToPlainText { get; set; } = true;
    public int MaxRenderWidth { get; set; } = 720;
    public int MaxRenderHeight { get; set; } = 80;

    public void Normalize()
    {
        Notation = string.IsNullOrWhiteSpace(Notation) ? "latex" : Notation.Trim();
        MaxRenderWidth = Math.Clamp(MaxRenderWidth, 220, 1600);
        MaxRenderHeight = Math.Clamp(MaxRenderHeight, 32, 300);
    }
}

internal sealed class ActionConfig
{
    public string? Trigger { get; set; } = "";
    public string? Alias { get; set; } = "";
    public string? Type { get; set; } = "command";
    public string? Command { get; set; } = "";
    public string? Text { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public List<string>? Arguments { get; set; } = new();
    public List<string>? ReuseProcessNames { get; set; } = new();
    public List<string>? ReuseWindowClasses { get; set; } = new();
    public bool ReuseExistingWindow { get; set; } = true;
    public bool ActivateAfterLaunch { get; set; }
    public bool RequiresConfirmation { get; set; }
    public string? ExactMatchBehavior { get; set; } = "keepOpen";
    public string? TabCompletionBehavior { get; set; } = "close";
}
