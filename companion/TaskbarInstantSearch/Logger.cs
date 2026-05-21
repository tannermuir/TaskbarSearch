namespace TaskbarInstantSearch;

internal static class Logger
{
    private static readonly object LockObject = new();
    private static string? _logPath;

    public static string AppDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TaskbarInstantSearch");

    public static void Initialize()
    {
        Directory.CreateDirectory(AppDirectory);
        _logPath = Path.Combine(AppDirectory, "app.log");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? exception = null)
    {
        Write("ERROR", exception == null ? message : $"{message}: {exception}");
    }

    private static void Write(string level, string message)
    {
        if (_logPath == null)
        {
            Initialize();
        }

        string line = $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}";
        lock (LockObject)
        {
            File.AppendAllText(_logPath!, line);
        }
    }
}
