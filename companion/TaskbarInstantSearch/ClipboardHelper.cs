using System.Windows.Forms;

namespace TaskbarInstantSearch;

internal static class ClipboardHelper
{
    public static void SetText(string text)
    {
        Exception? lastException = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (Exception exception)
            {
                lastException = exception;
                Thread.Sleep(25);
            }
        }

        throw new InvalidOperationException("Failed to set clipboard text", lastException);
    }
}
