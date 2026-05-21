using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace TaskbarInstantSearch;

internal sealed class SearchOverlayForm : Form
{
    private static readonly Color WezTermForeground = ColorTranslator.FromHtml("#bea3c7");
    private static readonly Color WezTermSuggestion = ColorTranslator.FromHtml("#6f6474");
    private static readonly Color WezTermCursor = ColorTranslator.FromHtml("#bea3c7");
    private const string PdfMenuTrigger = "pdf";
    private const string PdfMenuPrefix = "pdf:";

    private readonly OverlayConfig _config;
    private readonly System.Windows.Forms.Timer _cursorTimer;
    private readonly List<string> _actionTriggers = new();
    private readonly HashSet<string> _confirmationInputs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _confirmationTriggers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _confirmationInputTriggers = new(StringComparer.Ordinal);
    private readonly LauncherTextState _textState = new();
    private Bitmap? _renderedLatexBitmap;
    private string _aiThinkingSuffix = " ...";
    private string _statusSuffix = "";
    private int _caretIndex;
    private int _selectionAnchor = -1;
    private string? _armedConfirmationInput;
    private string? _armedConfirmationTrigger;
    private bool _cursorVisible = true;
    private DateTime _ignoreDeactivateUntilUtc = DateTime.MinValue;
    private DateTime _suppressDeactivateUntilUtc = DateTime.MinValue;
    private int _focusRestoreGeneration;

    public event EventHandler<ActionRequestedEventArgs>? ActionRequested;
    public event EventHandler<AiPromptRequestedEventArgs>? AiPromptRequested;
    public event EventHandler? AiPromptCanceled;
    public event EventHandler? OverlayClosed;

    public SearchOverlayForm(OverlayConfig config)
    {
        _config = config;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        Width = Math.Max(220, _config.Width);
        Height = Math.Max(28, _config.Height);

        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint, true);

        _cursorTimer = new System.Windows.Forms.Timer { Interval = 530 };
        _cursorTimer.Tick += (_, _) =>
        {
            _cursorVisible = !_cursorVisible;
            RenderLayeredOverlay();
        };

    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_LAYERED;
            return cp;
        }
    }

    private string Buffer
    {
        get => _textState.SourceText;
        set
        {
            _textState.SourceText = value;
            _textState.ClipboardText = value;
            _textState.DisplayMode = LauncherDisplayMode.PlainText;
            _textState.RenderSource = null;
            _textState.IsEditable = true;
            _statusSuffix = "";
            ClearRenderedLatexBitmap();
            SetCaret(value.Length, clearSelection: true);
            NormalizeEditorState();
        }
    }

    public void SetActionTriggers(IEnumerable<string> triggers)
    {
        _actionTriggers.Clear();
        _actionTriggers.AddRange(triggers.Where(t => !string.IsNullOrEmpty(t)));
        RebuildConfirmationTriggers();
    }

    public void SetConfirmationInputs(IReadOnlyDictionary<string, string> inputTriggers)
    {
        _confirmationInputs.Clear();
        _confirmationInputTriggers.Clear();
        foreach (KeyValuePair<string, string> inputTrigger in inputTriggers)
        {
            if (string.IsNullOrEmpty(inputTrigger.Key) || string.IsNullOrEmpty(inputTrigger.Value))
            {
                continue;
            }

            _confirmationInputs.Add(inputTrigger.Key);
            _confirmationInputTriggers[inputTrigger.Key] = inputTrigger.Value;
        }

        RebuildConfirmationTriggers();
    }

    public void SetAiThinkingSuffix(string suffix)
    {
        _aiThinkingSuffix = string.IsNullOrEmpty(suffix) ? " ..." : suffix;
    }

    public void ShowForInput()
    {
        try
        {
            Logger.Info("ShowForInput begin");
            ResetTextState();
            _armedConfirmationInput = null;
            _armedConfirmationTrigger = null;
            _cursorVisible = true;
            Width = Math.Max(220, _config.Width);
            Bounds = CalculateOverlayBounds();
            RenderLayeredOverlay();
            _ignoreDeactivateUntilUtc = DateTime.UtcNow.AddMilliseconds(900);
            _focusRestoreGeneration++;
            Show();
            TopMost = false;
            TopMost = true;
            RestoreOverlayFocus();
            _cursorTimer.Start();
            RenderLayeredOverlay();
            Logger.Info($"ShowForInput end visible={Visible} focused={Focused} containsFocus={ContainsFocus}");
        }
        catch (Exception exception)
        {
            Logger.Error("ShowForInput failed", exception);
            try
            {
                HideAndClear();
            }
            catch
            {
            }
        }
    }

    public void HideAndClear()
    {
        CloseAndReset();
    }

    public void CloseAndReset()
    {
        Logger.Info("HideAndClear");
        ResetTextState();
        _armedConfirmationInput = null;
        _armedConfirmationTrigger = null;
        Width = Math.Max(220, _config.Width);
        _cursorTimer.Stop();
        _focusRestoreGeneration++;
        _suppressDeactivateUntilUtc = DateTime.MinValue;
        RenderLayeredOverlay();
        Hide();
        OverlayClosed?.Invoke(this, EventArgs.Empty);
    }

    public void ClearForNextInput()
    {
        ClearAndStayOpen();
    }

    public void ClearAndStayOpen()
    {
        Logger.Info("ClearForNextInput");
        ResetTextState();
        _armedConfirmationInput = null;
        _armedConfirmationTrigger = null;
        Width = Math.Max(220, _config.Width);
        _cursorVisible = true;
        int generation = ++_focusRestoreGeneration;
        SuppressDeactivateClose(TimeSpan.FromMilliseconds(3000));
        if (!Visible)
        {
            Bounds = CalculateOverlayBounds();
            Show();
        }

        TopMost = false;
        TopMost = true;
        RenderLayeredOverlay();
        RestoreOverlayFocus();
        ScheduleFocusRestoreBurst(generation);
    }

    public void SuppressDeactivateClose(TimeSpan duration)
    {
        DateTime until = DateTime.UtcNow.Add(duration);
        if (until > _suppressDeactivateUntilUtc)
        {
            _suppressDeactivateUntilUtc = until;
        }
    }

    public void RestoreInputForKeepOpenAction(TimeSpan duration)
    {
        int generation = ++_focusRestoreGeneration;
        SuppressDeactivateClose(duration);
        RestoreOverlayFocus();
        ScheduleFocusRestoreBurst(generation);
    }

    public void SetAiThinking(string prompt)
    {
        ClearRenderedLatexBitmap();
        _textState.SourceText = prompt;
        _textState.ClipboardText = "";
        _textState.DisplayMode = LauncherDisplayMode.AiThinking;
        _textState.RenderSource = null;
        _textState.IsEditable = false;
        _statusSuffix = "";
        SetCaret(prompt.Length, clearSelection: true);
        _cursorVisible = true;
        RenderLayeredOverlay();
    }

    public void SetAiResult(string text, string? renderSource, Bitmap? renderedBitmap)
    {
        ClearRenderedLatexBitmap();
        _textState.SourceText = text;
        _textState.ClipboardText = text;
        _textState.RenderSource = renderSource;
        _textState.IsEditable = renderedBitmap == null;
        _statusSuffix = "";
        SetCaret(text.Length, clearSelection: true);
        if (renderedBitmap != null)
        {
            _renderedLatexBitmap = renderedBitmap;
            _textState.DisplayMode = LauncherDisplayMode.RenderedLatex;
            _cursorVisible = false;
        }
        else
        {
            _textState.DisplayMode = LauncherDisplayMode.PlainText;
            _cursorVisible = true;
        }

        RenderLayeredOverlay();
    }

    public void SetAiFailure(string suffix)
    {
        _textState.DisplayMode = LauncherDisplayMode.PlainTextWithSuggestion;
        _textState.IsEditable = true;
        _textState.RenderSource = null;
        _statusSuffix = suffix;
        SetCaret(Buffer.Length, clearSelection: true);
        ClearRenderedLatexBitmap();
        RenderLayeredOverlay();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (_config.CloseOnFocusLost && Visible)
        {
            DateTime now = DateTime.UtcNow;
            if (now < _ignoreDeactivateUntilUtc || now < _suppressDeactivateUntilUtc)
            {
                Logger.Info("OnDeactivate ignored during activation grace");
                BeginInvoke(new MethodInvoker(() =>
                {
                    if (Visible)
                    {
                        RestoreOverlayFocus();
                        Logger.Info($"OnDeactivate reactivated visible={Visible} focused={Focused} containsFocus={ContainsFocus}");
                    }
                }));
                return;
            }

            Logger.Info("OnDeactivate hiding overlay");
            HideAndClear();
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            HideAndClear();
            return true;
        }

        if (keyData == Keys.Enter)
        {
            return true;
        }

        if (keyData == (Keys.Control | Keys.A))
        {
            if (_textState.DisplayMode != LauncherDisplayMode.AiThinking)
            {
                SelectAllText();
                RenderLayeredOverlay();
            }

            return true;
        }

        if (keyData == (Keys.Control | Keys.C))
        {
            CopySelectionToClipboard();
            return true;
        }

        if (keyData == (Keys.Control | Keys.X))
        {
            if (_textState.DisplayMode != LauncherDisplayMode.AiThinking)
            {
                CutSelectionToClipboard();
            }

            return true;
        }

        if (keyData == (Keys.Control | Keys.V))
        {
            if (_textState.DisplayMode != LauncherDisplayMode.AiThinking)
            {
                PasteClipboardText();
            }

            return true;
        }

        if (HandleNavigationKey(keyData))
        {
            return true;
        }

        if (keyData == Keys.Tab)
        {
            if (_textState.DisplayMode == LauncherDisplayMode.AiThinking)
            {
                return true;
            }

            if (IsWaitingForConfirmation())
            {
                string input = _armedConfirmationTrigger ?? _armedConfirmationInput!;
                ActionRequested?.Invoke(this, new ActionRequestedEventArgs(
                    input,
                    ActionInvocationKind.Confirmed));
                return true;
            }

            CompletionSuggestion? suggestion = GetUniqueSuggestion();
            if (suggestion != null)
            {
                _cursorVisible = true;
                if (_confirmationTriggers.Contains(suggestion.Trigger))
                {
                    ArmConfirmation(Buffer, suggestion.Trigger);
                    RenderLayeredOverlay();
                }
                else
                {
                    Buffer += suggestion.RemainingText;
                    if (TryEnterPrefixMenu())
                    {
                        RenderLayeredOverlay();
                        return true;
                    }

                    RenderLayeredOverlay();
                    ActionRequested?.Invoke(this, new ActionRequestedEventArgs(
                        Buffer,
                        ActionInvocationKind.TabCompleted));
                }
            }
            else if (ShouldPromptAi(Buffer))
            {
                string prompt = Buffer;
                SetAiThinking(prompt);
                AiPromptRequested?.Invoke(this, new AiPromptRequestedEventArgs(prompt));
            }

            return true;
        }

        if (keyData == (Keys.Control | Keys.Back))
        {
            if (_textState.DisplayMode == LauncherDisplayMode.AiThinking)
            {
                return true;
            }

            EnsureEditablePlainText();
            ClearConfirmation();
            DeleteWordBeforeCaret();
            _armedConfirmationInput = null;
            _armedConfirmationTrigger = null;
            RenderLayeredOverlay();
            return true;
        }

        if (keyData == Keys.Back)
        {
            if (_textState.DisplayMode == LauncherDisplayMode.AiThinking)
            {
                return true;
            }

            EnsureEditablePlainText();
            NormalizeEditorState();
            if (HasSelection)
            {
                DeleteSelection();
                _armedConfirmationInput = null;
                _armedConfirmationTrigger = null;
                RenderLayeredOverlay();
            }
            else if (_caretIndex > 0)
            {
                int newCaret = _caretIndex - 1;
                SetBufferPreservingMode(Buffer.Remove(_caretIndex - 1, 1));
                SetCaret(newCaret, clearSelection: true);
                _armedConfirmationInput = null;
                _armedConfirmationTrigger = null;
                RenderLayeredOverlay();
            }

            return true;
        }

        if (keyData == Keys.Delete)
        {
            if (_textState.DisplayMode == LauncherDisplayMode.AiThinking)
            {
                return true;
            }

            EnsureEditablePlainText();
            NormalizeEditorState();
            if (HasSelection)
            {
                DeleteSelection();
            }
            else if (_caretIndex < Buffer.Length)
            {
                SetBufferPreservingMode(Buffer.Remove(_caretIndex, 1));
            }

            _armedConfirmationInput = null;
            _armedConfirmationTrigger = null;
            RenderLayeredOverlay();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);

        if (!Visible || char.IsControl(e.KeyChar))
        {
            return;
        }

        if (_textState.DisplayMode == LauncherDisplayMode.AiThinking)
        {
            e.Handled = true;
            return;
        }

        EnsureEditablePlainText();
        ClearConfirmation();
        InsertText(e.KeyChar.ToString());
        ProcessEditedText(invokeAction: true);
        e.Handled = true;
    }

    private void RenderLayeredOverlay()
    {
        try
        {
            NormalizeEditorState();
            if (!IsHandleCreated)
            {
                return;
            }

            EnsureOverlayWidth();

            using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                using var textFont = new Font("Iosevka", 18f, FontStyle.Regular, GraphicsUnit.Point);
                using var textBrush = new SolidBrush(WezTermForeground);
                using var suggestionBrush = new SolidBrush(WezTermSuggestion);
                using var selectionBrush = new SolidBrush(Color.FromArgb(105, 75, 64, 82));

                float textX = 38;
                float textY = (Height - textFont.Height) / 2f;
                using StringFormat format = CreateTextFormat();
                if (_textState.DisplayMode == LauncherDisplayMode.RenderedLatex &&
                    _renderedLatexBitmap != null)
                {
                    graphics.DrawImage(_renderedLatexBitmap, textX, Math.Max(0, (Height - _renderedLatexBitmap.Height) / 2f));
                    UpdateLayeredWindow(bitmap);
                    return;
                }

                DrawTextWithSelection(graphics, textFont, textBrush, selectionBrush, textX, textY, format);

                SizeF textSize = graphics.MeasureString(Buffer, textFont, PointF.Empty, format);
                string? confirmationDisplayText = GetConfirmationDisplayText();
                string suffix = GetDisplaySuffix(confirmationDisplayText);
                if (suffix.Length > 0)
                {
                    graphics.DrawString(suffix, textFont, suggestionBrush,
                        textX + textSize.Width, textY, format);
                }

                if (_cursorVisible)
                {
                    float caretWidth = MeasureTextWidth(graphics, textFont, format, Buffer[.._caretIndex]);
                    float cursorX = textX + caretWidth + 1;
                    using var cursorPen = new Pen(WezTermCursor, 1.5f);
                    graphics.DrawLine(cursorPen, cursorX, 8, cursorX, Height - 8);
                }
            }

            UpdateLayeredWindow(bitmap);
        }
        catch (Exception exception)
        {
            Logger.Error("RenderLayeredOverlay failed", exception);
        }
    }

    private void DrawTextWithSelection(
        Graphics graphics,
        Font textFont,
        Brush textBrush,
        Brush selectionBrush,
        float textX,
        float textY,
        StringFormat format)
    {
        NormalizeEditorState();
        if (!HasSelection)
        {
            graphics.DrawString(Buffer, textFont, textBrush, textX, textY, format);
            return;
        }

        int selectionStart = SelectionStart;
        int selectionEnd = SelectionEnd;
        string beforeSelection = Buffer[..selectionStart];
        string selectedText = Buffer[selectionStart..selectionEnd];
        string afterSelection = Buffer[selectionEnd..];

        float beforeWidth = MeasureTextWidth(graphics, textFont, format, beforeSelection);
        float selectionWidth = MeasureTextWidth(graphics, textFont, format, selectedText);
        graphics.FillRectangle(selectionBrush, textX + beforeWidth, 6, selectionWidth + 2, Height - 12);
        graphics.DrawString(beforeSelection, textFont, textBrush, textX, textY, format);
        graphics.DrawString(selectedText, textFont, textBrush, textX + beforeWidth, textY, format);
        graphics.DrawString(afterSelection, textFont, textBrush, textX + beforeWidth + selectionWidth, textY, format);
    }

    private static float MeasureTextWidth(
        Graphics graphics,
        Font textFont,
        StringFormat format,
        string text)
    {
        return text.Length == 0
            ? 0
            : graphics.MeasureString(text, textFont, PointF.Empty, format).Width;
    }

    private static StringFormat CreateTextFormat()
    {
        var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        return format;
    }

    private Rectangle CalculateOverlayBounds()
    {
        IntPtr taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero &&
            NativeMethods.GetWindowRect(taskbar, out NativeMethods.RECT taskbarRect))
        {
            int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
            int x = taskbarRect.Left + 8 + Math.Max(0, _config.LeftPadding);
            int y = taskbarRect.Top + Math.Max(0, (taskbarHeight - Height) / 2);
            return new Rectangle(x, y, Width, Height);
        }

        Rectangle workingArea = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetWorkingArea(Point.Empty);
        return new Rectangle(workingArea.Left + 8 + Math.Max(0, _config.LeftPadding),
            workingArea.Bottom - Height - 6, Width, Height);
    }

    private int CalculateMaxOverlayWidth()
    {
        IntPtr taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero &&
            NativeMethods.GetWindowRect(taskbar, out NativeMethods.RECT taskbarRect))
        {
            return Math.Max(220, taskbarRect.Right - Left - 8);
        }

        Rectangle workingArea = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetWorkingArea(Point.Empty);
        return Math.Max(220, workingArea.Right - Left - 8);
    }

    private void EnsureOverlayWidth()
    {
        using var measureBitmap = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using Graphics graphics = Graphics.FromImage(measureBitmap);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using var textFont = new Font("Iosevka", 18f, FontStyle.Regular, GraphicsUnit.Point);
        using StringFormat format = CreateTextFormat();

        float textX = 38;
        if (_textState.DisplayMode == LauncherDisplayMode.RenderedLatex &&
            _renderedLatexBitmap != null)
        {
            int renderedWidth = (int)Math.Ceiling(textX + _renderedLatexBitmap.Width + 28);
            int renderTargetWidth = Math.Max(Math.Max(220, _config.Width), renderedWidth);
            int renderMaxWidth = CalculateMaxOverlayWidth();
            if (renderMaxWidth > 0)
            {
                renderTargetWidth = Math.Min(renderTargetWidth, renderMaxWidth);
            }

            if (renderTargetWidth != Width)
            {
                Width = renderTargetWidth;
            }

            return;
        }

        SizeF textSize = graphics.MeasureString(Buffer, textFont, PointF.Empty, format);
        string confirmationDisplayText = GetConfirmationDisplayText();
        float requiredWidth = CalculateRequiredWidth(graphics, textFont, format, textX, textSize.Width, confirmationDisplayText);
        int width = Math.Max(Math.Max(220, _config.Width), (int)Math.Ceiling(requiredWidth));
        int maxWidth = CalculateMaxOverlayWidth();
        if (maxWidth > 0)
        {
            width = Math.Min(width, maxWidth);
        }

        if (width != Width)
        {
            Width = width;
        }
    }

    private void UpdateLayeredWindow(Bitmap bitmap)
    {
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
        IntPtr bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
        IntPtr oldBitmap = NativeMethods.SelectObject(memoryDc, bitmapHandle);

        try
        {
            var destination = new NativeMethods.POINT(Left, Top);
            var size = new NativeMethods.SIZE(Width, Height);
            var source = new NativeMethods.POINT(0, 0);
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA
            };

            NativeMethods.UpdateLayeredWindow(
                Handle, screenDc, ref destination, ref size, memoryDc,
                ref source, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            NativeMethods.SelectObject(memoryDc, oldBitmap);
            NativeMethods.DeleteObject(bitmapHandle);
            NativeMethods.DeleteDC(memoryDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private CompletionSuggestion? GetUniqueSuggestion()
    {
        NormalizeEditorState();
        if (Buffer.Length == 0 || HasSelection || _caretIndex != Buffer.Length)
        {
            return null;
        }

        string? match = null;
        foreach (string trigger in _actionTriggers)
        {
            if (trigger.Length <= Buffer.Length ||
                !trigger.StartsWith(Buffer, StringComparison.Ordinal))
            {
                continue;
            }

            if (match != null)
            {
                return null;
            }

            match = trigger;
        }

        if (match == null)
        {
            return null;
        }

        string remaining = match[Buffer.Length..];
        return new CompletionSuggestion(match, remaining, remaining);
    }

    private bool IsWaitingForConfirmation()
    {
        return _armedConfirmationInput != null;
    }

    private bool ArmExactConfirmationIfNeeded(string input)
    {
        if (_confirmationInputTriggers.TryGetValue(input, out string? canonicalTrigger))
        {
            ArmConfirmation(input, canonicalTrigger);
            return true;
        }

        return false;
    }

    private bool TryEnterPrefixMenu()
    {
        if (!string.Equals(Buffer, PdfMenuTrigger, StringComparison.Ordinal))
        {
            return false;
        }

        Buffer = PdfMenuPrefix;
        return true;
    }

    private void ArmConfirmation(string input, string canonicalTrigger)
    {
        _armedConfirmationInput = input;
        _armedConfirmationTrigger = canonicalTrigger;
    }

    private void ClearConfirmation()
    {
        _armedConfirmationInput = null;
        _armedConfirmationTrigger = null;
    }

    private string GetConfirmationDisplayText()
    {
        if (_armedConfirmationInput == null || _armedConfirmationTrigger == null)
        {
            return "";
        }

        if (_armedConfirmationTrigger.StartsWith(Buffer, StringComparison.Ordinal) &&
            _armedConfirmationTrigger.Length > Buffer.Length)
        {
            return _armedConfirmationTrigger[Buffer.Length..] + " [confirm]";
        }

        return " [confirm]";
    }

    private float CalculateRequiredWidth(
        Graphics graphics,
        Font textFont,
        StringFormat format,
        float textX,
        float typedWidth,
        string confirmationDisplayText)
    {
        string displayText = GetDisplaySuffix(confirmationDisplayText);

        float suggestionWidth = displayText.Length == 0
            ? 0
            : graphics.MeasureString(displayText, textFont, PointF.Empty, format).Width;
        return textX + typedWidth + suggestionWidth + 28;
    }

    private string GetDisplaySuffix(string confirmationDisplayText)
    {
        if (_textState.DisplayMode == LauncherDisplayMode.AiThinking)
        {
            return _aiThinkingSuffix;
        }

        if (_statusSuffix.Length > 0)
        {
            return _statusSuffix;
        }

        if (confirmationDisplayText.Length > 0)
        {
            return confirmationDisplayText;
        }

        CompletionSuggestion? suggestion = GetUniqueSuggestion();
        return suggestion?.DisplayText ?? "";
    }

    private static bool ShouldPromptAi(string text)
    {
        return text.Length > 0 &&
               !text.StartsWith(PdfMenuPrefix, StringComparison.Ordinal);
    }

    private bool HasSelection => _selectionAnchor >= 0 && _selectionAnchor != _caretIndex;

    private int SelectionStart => HasSelection ? Math.Min(_selectionAnchor, _caretIndex) : _caretIndex;

    private int SelectionEnd => HasSelection ? Math.Max(_selectionAnchor, _caretIndex) : _caretIndex;

    private void NormalizeEditorState()
    {
        int length = Buffer.Length;
        _caretIndex = Math.Clamp(_caretIndex, 0, length);
        if (_selectionAnchor > length)
        {
            _selectionAnchor = length;
        }

        if (_selectionAnchor == _caretIndex)
        {
            _selectionAnchor = -1;
        }
    }

    private void SetBufferPreservingMode(string text)
    {
        _textState.SourceText = text;
        _textState.ClipboardText = text;
        _textState.DisplayMode = LauncherDisplayMode.PlainText;
        _textState.RenderSource = null;
        _textState.IsEditable = true;
        _statusSuffix = "";
        ClearRenderedLatexBitmap();
        NormalizeEditorState();
    }

    private void SetCaret(int index, bool clearSelection)
    {
        if (!clearSelection && _selectionAnchor < 0)
        {
            _selectionAnchor = _caretIndex;
        }

        _caretIndex = Math.Clamp(index, 0, Buffer.Length);
        if (clearSelection)
        {
            _selectionAnchor = -1;
        }
    }

    private void SelectAllText()
    {
        _selectionAnchor = 0;
        _caretIndex = Buffer.Length;
        _cursorVisible = true;
    }

    private void InsertText(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (HasSelection)
        {
            DeleteSelection();
        }

        SetBufferPreservingMode(Buffer.Insert(_caretIndex, text));
        SetCaret(_caretIndex + text.Length, clearSelection: true);
        _cursorVisible = true;
    }

    private void DeleteSelection()
    {
        NormalizeEditorState();
        if (!HasSelection)
        {
            return;
        }

        int start = SelectionStart;
        int length = SelectionEnd - SelectionStart;
        SetBufferPreservingMode(Buffer.Remove(start, length));
        SetCaret(start, clearSelection: true);
        _cursorVisible = true;
    }

    private void DeleteWordBeforeCaret()
    {
        NormalizeEditorState();
        if (HasSelection)
        {
            DeleteSelection();
            return;
        }

        if (_caretIndex == 0)
        {
            return;
        }

        int start = PreviousWordBoundary(_caretIndex);
        SetBufferPreservingMode(Buffer.Remove(start, _caretIndex - start));
        SetCaret(start, clearSelection: true);
        _cursorVisible = true;
    }

    private bool HandleNavigationKey(Keys keyData)
    {
        if (_textState.DisplayMode == LauncherDisplayMode.AiThinking)
        {
            return false;
        }

        Keys keyCode = keyData & Keys.KeyCode;
        bool shift = (keyData & Keys.Shift) == Keys.Shift;
        bool control = (keyData & Keys.Control) == Keys.Control;
        int target;

        switch (keyCode)
        {
            case Keys.Left:
                if (!shift && HasSelection)
                {
                    target = SelectionStart;
                }
                else
                {
                    target = control ? PreviousWordBoundary(_caretIndex) : _caretIndex - 1;
                }
                break;
            case Keys.Right:
                if (!shift && HasSelection)
                {
                    target = SelectionEnd;
                }
                else
                {
                    target = control ? NextWordBoundary(_caretIndex) : _caretIndex + 1;
                }
                break;
            case Keys.Home:
                target = 0;
                break;
            case Keys.End:
                target = Buffer.Length;
                break;
            default:
                return false;
        }

        EnsureEditablePlainText();
        NormalizeEditorState();
        SetCaret(target, clearSelection: !shift);
        _cursorVisible = true;
        RenderLayeredOverlay();
        return true;
    }

    private int PreviousWordBoundary(int index)
    {
        int position = Math.Clamp(index, 0, Buffer.Length);
        while (position > 0 && char.IsWhiteSpace(Buffer[position - 1]))
        {
            position--;
        }

        while (position > 0 && !char.IsWhiteSpace(Buffer[position - 1]))
        {
            position--;
        }

        return position;
    }

    private int NextWordBoundary(int index)
    {
        int position = Math.Clamp(index, 0, Buffer.Length);
        while (position < Buffer.Length && !char.IsWhiteSpace(Buffer[position]))
        {
            position++;
        }

        while (position < Buffer.Length && char.IsWhiteSpace(Buffer[position]))
        {
            position++;
        }

        return position;
    }

    private void CopySelectionToClipboard()
    {
        if (!HasSelection)
        {
            return;
        }

        ClipboardHelper.SetText(Buffer[SelectionStart..SelectionEnd]);
    }

    private void CutSelectionToClipboard()
    {
        if (!HasSelection)
        {
            return;
        }

        CopySelectionToClipboard();
        DeleteSelection();
        ClearConfirmation();
        RenderLayeredOverlay();
    }

    private void PasteClipboardText()
    {
        try
        {
            string text = Clipboard.GetText();
            if (text.Length == 0)
            {
                return;
            }

            EnsureEditablePlainText();
            ClearConfirmation();
            InsertText(text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' '));
            ProcessEditedText(invokeAction: true);
        }
        catch (Exception exception)
        {
            Logger.Error("Failed to paste clipboard text", exception);
        }
    }

    private void ProcessEditedText(bool invokeAction)
    {
        if (TryEnterPrefixMenu())
        {
            _cursorVisible = true;
            RenderLayeredOverlay();
            return;
        }

        bool armedConfirmation = ArmExactConfirmationIfNeeded(Buffer);
        _cursorVisible = true;
        RenderLayeredOverlay();
        if (invokeAction && !armedConfirmation)
        {
            ActionRequested?.Invoke(this, new ActionRequestedEventArgs(
                Buffer,
                ActionInvocationKind.TypedExact));
        }
    }

    private void EnsureEditablePlainText()
    {
        if (_textState.DisplayMode != LauncherDisplayMode.RenderedLatex)
        {
            return;
        }

        _textState.DisplayMode = LauncherDisplayMode.PlainText;
        _textState.RenderSource = null;
        _textState.IsEditable = true;
        _cursorVisible = true;
        ClearRenderedLatexBitmap();
    }

    private void ResetTextState()
    {
        bool wasThinking = _textState.DisplayMode == LauncherDisplayMode.AiThinking;
        _textState.Reset();
        _statusSuffix = "";
        _caretIndex = 0;
        _selectionAnchor = -1;
        ClearRenderedLatexBitmap();
        if (wasThinking)
        {
            AiPromptCanceled?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ClearRenderedLatexBitmap()
    {
        _renderedLatexBitmap?.Dispose();
        _renderedLatexBitmap = null;
    }

    private void ScheduleFocusRestoreBurst(int generation)
    {
        int[] delays = { 75, 200, 450, 900, 1500, 2400 };
        foreach (int delay in delays)
        {
            var timer = new System.Windows.Forms.Timer { Interval = delay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                if (generation != _focusRestoreGeneration || !Visible)
                {
                    return;
                }

                SuppressDeactivateClose(TimeSpan.FromMilliseconds(600));
                RestoreOverlayFocus();
                RenderLayeredOverlay();
            };
            timer.Start();
        }
    }

    private void RestoreOverlayFocus()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        if (!Visible)
        {
            Show();
        }

        TopMost = false;
        TopMost = true;
        NativeMethods.ShowWindow(Handle, NativeMethods.SW_RESTORE);

        IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();
        uint foregroundThread = foregroundWindow == IntPtr.Zero
            ? 0
            : NativeMethods.GetWindowThreadProcessId(foregroundWindow, out _);
        uint currentThread = NativeMethods.GetCurrentThreadId();
        bool attached = false;

        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attached = NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
            }

            NativeMethods.BringWindowToTop(Handle);
            NativeMethods.SetForegroundWindow(Handle);
            NativeMethods.SetFocus(Handle);
            Activate();
            Focus();
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    private void RebuildConfirmationTriggers()
    {
        _confirmationTriggers.Clear();
        foreach (string trigger in _actionTriggers)
        {
            if (_confirmationInputs.Contains(trigger))
            {
                _confirmationTriggers.Add(trigger);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cursorTimer.Dispose();
            ClearRenderedLatexBitmap();
        }

        base.Dispose(disposing);
    }
}

internal sealed class CompletionSuggestion
{
    public CompletionSuggestion(string trigger, string remainingText, string displayText)
    {
        Trigger = trigger;
        RemainingText = remainingText;
        DisplayText = displayText;
    }

    public string Trigger { get; }
    public string RemainingText { get; }
    public string DisplayText { get; }
}

internal sealed class ActionRequestedEventArgs : EventArgs
{
    public ActionRequestedEventArgs(
        string input,
        ActionInvocationKind invocationKind)
    {
        Input = input;
        InvocationKind = invocationKind;
    }

    public string Input { get; }
    public ActionInvocationKind InvocationKind { get; }
}

internal sealed class AiPromptRequestedEventArgs : EventArgs
{
    public AiPromptRequestedEventArgs(string prompt)
    {
        Prompt = prompt;
    }

    public string Prompt { get; }
}
