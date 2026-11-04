using System.IO.Compression;
using System.Security.Cryptography;

namespace PulseAPK.Core.Services;

public static class ApkInputService
{
    public static List<string> AdditionalCodeInventory(string apkPath)
    {
        using var archive = ZipFile.OpenRead(apkPath);
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".py", ".pyc", ".pyo", ".rpy", ".rpyc", ".rpym", ".rpymc", ".rpa", ".rpi", ".lua", ".luac", ".js", ".wasm" };
        return archive.Entries.Where(e => extensions.Contains(Path.GetExtension(e.FullName))).Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }
    public static void Validate(string apkPath)
    {
        if (!string.Equals(Path.GetExtension(apkPath), ".apk", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Selecciona un archivo .apk.");
        using var archive = ZipFile.OpenRead(apkPath);
        if (archive.GetEntry("AndroidManifest.xml") is null)
            throw new InvalidDataException("El archivo no contiene AndroidManifest.xml; no es un APK válido.");
        long size = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Contains(".."))
                throw new InvalidDataException("El APK contiene rutas de extracción no válidas.");
            size = checked(size + entry.Length);
            if (size > 10L * 1024 * 1024 * 1024 || archive.Entries.Count > 250_000)
                throw new InvalidDataException("El APK supera el límite de análisis (10 GiB descomprimidos o 250 000 archivos).");
        }
    }

    public static async Task<string> HashAsync(string path, CancellationToken token = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
}
