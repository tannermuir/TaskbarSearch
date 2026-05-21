namespace TaskbarInstantSearch;

internal enum LauncherDisplayMode
{
    PlainText,
    PlainTextWithSuggestion,
    AiThinking,
    RenderedLatex
}

internal sealed class LauncherTextState
{
    public string SourceText { get; set; } = "";
    public string ClipboardText { get; set; } = "";
    public LauncherDisplayMode DisplayMode { get; set; } = LauncherDisplayMode.PlainText;
    public string? RenderSource { get; set; }
    public bool IsEditable { get; set; } = true;

    public void Reset()
    {
        SourceText = "";
        ClipboardText = "";
        DisplayMode = LauncherDisplayMode.PlainText;
        RenderSource = null;
        IsEditable = true;
    }

    public void SetPlainText(string text)
    {
        SourceText = text;
        ClipboardText = text;
        DisplayMode = LauncherDisplayMode.PlainText;
        RenderSource = null;
        IsEditable = true;
    }
}
