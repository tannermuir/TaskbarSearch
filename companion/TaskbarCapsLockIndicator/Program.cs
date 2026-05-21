using System.Threading;
using System.Windows.Forms;

namespace TaskbarCapsLockIndicator;

internal static class Program
{
    private const string MutexName = "TaskbarCapsLockIndicator.Singleton";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            return;
        }

        Logger.Initialize();
        Application.ThreadException += (_, e) =>
            Logger.Error("Unhandled UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                Logger.Error("Unhandled application exception", exception);
            }
            else
            {
                Logger.Error($"Unhandled application exception: {e.ExceptionObject}");
            }
        };

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new IndicatorApplicationContext());
    }
}
