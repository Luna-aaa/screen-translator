using System.IO;
using System.IO.Pipes;
using System.Text;

namespace ScreenTranslator.Infrastructure;

public static class IpcCommands
{
    public const string ShowSettings = "show-settings";
    public const string Capture = "capture";
}

/// <summary>
/// Guarantees exactly one running instance. A named mutex decides who is primary;
/// a named pipe lets the loser hand its intent to the winner before exiting, so
/// double-clicking the .exe again surfaces the existing instance instead of doing nothing.
/// Both names are Local\ (per-session), which is what we want: one instance per
/// logged-in user, not one per machine.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\ScreenTranslator.SingleInstance.v1";
    private const string PipeName = "ScreenTranslator.Ipc.v1";

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;

    /// <summary>Raised on a background thread. Marshal to the UI thread yourself.</summary>
    public event Action<string>? CommandReceived;

    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
        }
        return createdNew;
    }

    public void StartServer()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ServerLoopAsync(_cts.Token));
    }

    private async Task ServerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var command = line.Trim();
                    Log.Info($"收到来自第二个实例的命令：{command}");
                    CommandReceived?.Invoke(command);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("单实例通信服务出错", ex);
                try { await Task.Delay(500, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Called by the losing instance to hand its command to the primary.</summary>
    public static bool SendToPrimary(string command, int timeoutMs = 3000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(command);
            return true;
        }
        catch (Exception ex)
        {
            // The primary may be mid-shutdown; nothing useful to do but log it.
            Log.Warn($"无法把命令 {command} 转交给已有实例：{ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignored */ }
        _cts?.Dispose();
        _cts = null;
        _mutex?.Dispose();
        _mutex = null;
    }
}
