using System.Threading;
using System.Windows.Forms;

namespace TaskbarInstantSearch;

internal static class Program
{
    private const string MutexName = "TaskbarInstantSearch.Singleton";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out bool firstInstance);
        bool toggleOnStart = args.Any(a => string.Equals(a, "--toggle", StringComparison.OrdinalIgnoreCase));
        bool restartChild = args.Any(a => string.Equals(a, "--restart-child", StringComparison.OrdinalIgnoreCase));

        if (!firstInstance)
        {
            if (restartChild)
            {
                try
                {
                    if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
                    {
                        return;
                    }
                }
                catch (AbandonedMutexException)
                {
                }
            }
            else
            {
                if (toggleOnStart)
                {
                    PipeServer.SendClientMessage("{\"type\":\"toggle\"}");
                }

                return;
            }
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

        AppConfig config = AppConfig.LoadOrCreate();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var context = new SearchApplicationContext(config);
        if (toggleOnStart)
        {
            context.ToggleOverlay();
        }

        Application.Run(context);
    }
}
