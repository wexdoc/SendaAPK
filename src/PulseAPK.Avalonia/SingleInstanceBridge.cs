using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PulseAPK.Avalonia;

/// <summary>Forwards launch requests only between processes of the same Windows user.</summary>
internal sealed class SingleInstanceBridge : IDisposable
{
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource stop = new();
    private bool ownsMutex;
    private Task? listener;
    public event Action<string?>? RequestReceived;

    public SingleInstanceBridge()
    {
        var identity = Environment.UserDomainName + "\\" + Environment.UserName;
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
        pipeName = "SendaAPK-" + suffix;
        mutex = new Mutex(false, "Global\\" + pipeName);
    }

    public bool TryAcquire()
    {
        try { ownsMutex = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { ownsMutex = true; }
        return ownsMutex;
    }

    public void StartListening() => listener = Task.Run(() => ListenAsync(stop.Token));

    public async Task<bool> SendAsync(string? path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(timeout.Token);
            var message = path is null ? "" : ValidatePath(path);
            if (message is null) return false;
            var bytes = Encoding.UTF8.GetBytes(message + "\n");
            await client.WriteAsync(bytes, timeout.Token);
            await client.FlushAsync(timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var buffer = new char[1024];
                var input = new StringBuilder();
                while (input.Length <= 32768)
                {
                    var read = await reader.ReadAsync(buffer.AsMemory(), timeout.Token);
                    if (read == 0) break;
                    input.Append(buffer, 0, read);
                    if (input.ToString().Contains('\n')) break;
                }
                if (input.Length > 32768) continue;
                var message = input.ToString().TrimEnd('\r', '\n');
                var path = message.Length == 0 ? null : ValidatePath(message);
                if (message.Length == 0 || path is not null) RequestReceived?.Invoke(path);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { break; }
        }
    }

    private static string? ValidatePath(string path)
    {
        try
        {
            if (path.Length > 32760 || !Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".apk", StringComparison.OrdinalIgnoreCase)) return null;
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public void Dispose()
    {
        stop.Cancel();
        try { listener?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        stop.Dispose();
        if (ownsMutex) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
