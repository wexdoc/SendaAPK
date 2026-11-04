using System.Diagnostics;

namespace PulseAPK.Core.Services;

public sealed record LocalProcessResult(int ExitCode, string Output);
public interface ILocalProcessRunner
{
    Task<LocalProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token);
}
public sealed class LocalProcessRunner : ILocalProcessRunner
{
    public async Task<LocalProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("No se pudo iniciar la herramienta.");
        var output = ReadBoundedAsync(process.StandardOutput);
        var error = ReadBoundedAsync(process.StandardError);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            throw;
        }
        return new(process.ExitCode, (await output + "\n" + await error).Trim());
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        const int limit = 1024 * 1024;
        var result = new System.Text.StringBuilder();
        var buffer = new char[8192];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory())) != 0)
        {
            var keep = Math.Min(read, limit - result.Length);
            if (keep > 0) result.Append(buffer, 0, keep);
            if (keep != read) truncated = true;
        }
        if (truncated) result.Append("\n[Salida truncada para limitar el uso de memoria.]");
        return result.ToString();
    }
}
