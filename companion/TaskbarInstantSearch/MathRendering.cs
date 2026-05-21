using System.Drawing;

namespace TaskbarInstantSearch;

internal sealed class MathRenderOptions
{
    public int MaxWidth { get; set; } = 720;
    public int MaxHeight { get; set; } = 80;
}

internal interface IMathRenderer
{
    bool CanRender(string source);
    Task<Bitmap?> RenderLatexAsync(
        string source,
        MathRenderOptions options,
        CancellationToken cancellationToken);
}

internal sealed class NullMathRenderer : IMathRenderer
{
    public bool CanRender(string source) => false;

    public Task<Bitmap?> RenderLatexAsync(
        string source,
        MathRenderOptions options,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<Bitmap?>(null);
    }
}

internal static class LatexDetector
{
    private static readonly string[] LatexCommands =
    {
        "\\frac",
        "\\sqrt",
        "\\int",
        "\\sum",
        "\\prod",
        "\\lim",
        "\\begin",
        "\\alpha",
        "\\beta",
        "\\gamma",
        "\\delta",
        "\\theta",
        "\\lambda",
        "\\mu",
        "\\pi",
        "\\sigma",
        "\\omega"
    };

    public static bool TryNormalizeLatex(string text, out string normalized)
    {
        normalized = "";
        string trimmed = text.Trim();
        if (trimmed.Length == 0 ||
            IsSimpleNumber(trimmed) ||
            !HasBalancedBraces(trimmed))
        {
            return false;
        }

        normalized = StripMathDelimiters(trimmed);
        if (normalized.Length == 0 || !HasBalancedBraces(normalized))
        {
            normalized = "";
            return false;
        }

        if (IsDelimitedMath(trimmed))
        {
            return true;
        }

        foreach (string command in LatexCommands)
        {
            if (normalized.Contains(command, StringComparison.Ordinal))
            {
                return true;
            }
        }

        normalized = "";
        return false;
    }

    private static bool IsSimpleNumber(string text)
    {
        return double.TryParse(
            text,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out _);
    }

    private static bool IsDelimitedMath(string text)
    {
        return (text.StartsWith("$", StringComparison.Ordinal) && text.EndsWith("$", StringComparison.Ordinal) && text.Length > 1) ||
               (text.StartsWith("\\(", StringComparison.Ordinal) && text.EndsWith("\\)", StringComparison.Ordinal)) ||
               (text.StartsWith("\\[", StringComparison.Ordinal) && text.EndsWith("\\]", StringComparison.Ordinal));
    }

    private static string StripMathDelimiters(string text)
    {
        if (text.StartsWith("$", StringComparison.Ordinal) &&
            text.EndsWith("$", StringComparison.Ordinal) &&
            text.Length > 1)
        {
            return text[1..^1].Trim();
        }

        if ((text.StartsWith("\\(", StringComparison.Ordinal) &&
             text.EndsWith("\\)", StringComparison.Ordinal)) ||
            (text.StartsWith("\\[", StringComparison.Ordinal) &&
             text.EndsWith("\\]", StringComparison.Ordinal)))
        {
            return text[2..^2].Trim();
        }

        return text;
    }

    private static bool HasBalancedBraces(string text)
    {
        int depth = 0;
        foreach (char character in text)
        {
            if (character == '{')
            {
                depth++;
            }
            else if (character == '}')
            {
                depth--;
                if (depth < 0)
                {
                    return false;
                }
            }
        }

        return depth == 0;
    }
}
