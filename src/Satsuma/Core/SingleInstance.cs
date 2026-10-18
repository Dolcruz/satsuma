using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Satsuma.Core;

/// <summary>Keeps one Satsuma per user and forwards command lines from later launches to it.</summary>
public sealed class SingleInstance : IDisposable
{
    static readonly string Id = "Satsuma-" + Environment.UserName;
    readonly Mutex _mutex;
    readonly CancellationTokenSource _cts = new();

    public bool IsFirst { get; }

    public event Action<string[]>? ArgsReceived;

    public SingleInstance()
    {
        // Fixed (session-local) name so the installer can detect a running copy via AppMutex.
        _mutex = new Mutex(true, "SatsumaAppMutex", out bool created);
        IsFirst = created;
    }

    public void StartListening()
    {
        Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync();
                    ArgsReceived?.Invoke(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(100); }
            }
        });
    }

    public static bool Send(string[] args)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out);
                client.Connect(400);
                using var writer = new StreamWriter(client, Encoding.UTF8);
                writer.Write(string.Join('\n', args));
                return true;
            }
            catch { Thread.Sleep(80); }
        }
        return false;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}
