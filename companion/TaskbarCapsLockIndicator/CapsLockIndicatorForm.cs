using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace TaskbarCapsLockIndicator;

internal sealed class CapsLockIndicatorForm : Form
{
    private static readonly Color IndicatorColor = ColorTranslator.FromHtml("#bea3c7");
    private const string IndicatorText = "\u21e9";
    private const int TextOriginX = 38;
    private const int OverlayScreenPadding = 8;
    private readonly Rectangle _fixedBounds;
    private bool _indicatorEnabled;

    public CapsLockIndicatorForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Width = 34;
        Height = 28;
        _fixedBounds = CalculateIndicatorBounds();
        Bounds = _fixedBounds;

        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint, true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_LAYERED |
                          NativeMethods.WS_EX_TRANSPARENT |
                          NativeMethods.WS_EX_TOOLWINDOW |
                          NativeMethods.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void ToggleIndicator()
    {
        _indicatorEnabled = !_indicatorEnabled;
        if (!_indicatorEnabled)
        {
            Hide();
            return;
        }

        Bounds = _fixedBounds;
        RenderIndicator();
        if (!Visible)
        {
            Show();
        }
    }

    private Rectangle CalculateIndicatorBounds()
    {
        Rectangle screenBounds = Screen.PrimaryScreen?.Bounds ?? Screen.GetBounds(Point.Empty);
        int caretX = screenBounds.Left + OverlayScreenPadding + GetConfiguredLeftPadding() + TextOriginX + 1;
        int centerX = (screenBounds.Left + caretX) / 2;
        int centerY = CalculateTaskbarCenterY(screenBounds);
        return new Rectangle(centerX - Width / 2, centerY - Height / 2, Width, Height);
    }

    private static int GetConfiguredLeftPadding()
    {
        string configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TaskbarInstantSearch",
            "config.json");
        try
        {
            if (!File.Exists(configPath))
            {
                return 0;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!document.RootElement.TryGetProperty("Overlay", out JsonElement overlay) ||
                !overlay.TryGetProperty("LeftPadding", out JsonElement leftPadding) ||
                leftPadding.ValueKind != JsonValueKind.Number)
            {
                return 0;
            }

            return Math.Max(0, leftPadding.GetInt32());
        }
        catch
        {
            return 0;
        }
    }

    private static int CalculateTaskbarCenterY(Rectangle fallbackBounds)
    {
        IntPtr taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero &&
            NativeMethods.GetWindowRect(taskbar, out NativeMethods.RECT taskbarRect))
        {
            return taskbarRect.Top + (taskbarRect.Bottom - taskbarRect.Top) / 2;
        }

        return fallbackBounds.Bottom - 24;
    }

    private void RenderIndicator()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            using var font = new Font("Iosevka", 18f, FontStyle.Bold, GraphicsUnit.Point);
            using var brush = new SolidBrush(IndicatorColor);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            graphics.DrawString(IndicatorText, font, brush, ClientRectangle, format);
        }

        UpdateLayeredWindow(bitmap);
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
                Handle,
                screenDc,
                ref destination,
                ref size,
                memoryDc,
                ref source,
                0,
                ref blend,
                NativeMethods.ULW_ALPHA);
        }
        finally
        {
            NativeMethods.SelectObject(memoryDc, oldBitmap);
            NativeMethods.DeleteObject(bitmapHandle);
            NativeMethods.DeleteDC(memoryDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
