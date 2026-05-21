using System.IO.Pipes;
using System.Text.Json;

namespace TaskbarInstantSearch;

internal sealed class PipeServer : IDisposable
{
    private const string PipeName = "TaskbarInstantSearch";
    private readonly Action<string> _onMessage;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _serverTask;
    private readonly object _disposeLock = new();
    private bool _disposed;

    public PipeServer(Action<string> onMessage)
    {
        _onMessage = onMessage;
        _serverTask = Task.Run(ServerLoopAsync);
    }

    public static bool SendClientMessage(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            client.Connect(500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            using var reader = new StreamReader(client);
            writer.WriteLine(message);
            reader.ReadLine();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ServerLoopAsync()
    {
        CancellationToken cancellationToken = _cancellation.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(pipe);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(line))
                {
                    string type = ParseMessageType(line);
                    if (!string.IsNullOrEmpty(type))
                    {
                        _onMessage(type);
                    }
                }

                await writer.WriteLineAsync("{\"ok\":true}").ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                Logger.Error("Pipe server error", exception);
            }
        }
    }

    private static string ParseMessageType(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("type", out JsonElement typeElement)
                ? typeElement.GetString() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _serverTask.Wait(500);
        }
        catch
        {
        }

        _cancellation.Dispose();
    }
}
