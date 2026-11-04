using System.ComponentModel;
using System.Diagnostics;

namespace PulseAPK.Core.Services;

public sealed class ProcessTerminationUnconfirmedException(string message, Exception? inner = null) : IOException(message, inner);

internal static class ProcessTermination
{
    public static async Task StopAfterCancellationAsync(Process process)
    {
        Exception? failure = null;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            try { if (!process.HasExited) failure = ex; }
            catch (InvalidOperationException) { failure = ex; }
        }
        await process.WaitForExitAsync();
        if (failure is not null)
            throw new ProcessTerminationUnconfirmedException("No se pudo confirmar que todos los procesos secundarios se detuvieran. Conserva los archivos temporales y revísalos antes de volver a intentar.", failure);
    }
}
