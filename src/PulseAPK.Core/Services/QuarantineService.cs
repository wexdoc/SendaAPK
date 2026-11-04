using System.Text.Json;

namespace PulseAPK.Core.Services;

public sealed class QuarantineEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OriginalPath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public string State { get; set; } = "Preparando";
    public override string ToString() => $"{Path.GetFileName(OriginalPath)} · {State} · {Created:g}";
}

public sealed class QuarantineService
{
    private readonly string root;
    private readonly SemaphoreSlim gate = new(1, 1);
    public QuarantineService() : this(Path.Combine(LocalAnalysisService.DataRoot, "quarantine")) { }
    public QuarantineService(string root) { this.root = Path.GetFullPath(root); }
    private string Data(QuarantineEntry entry) => Path.Combine(root, entry.Id + ".quarantine");
    private string Metadata(QuarantineEntry entry) => Path.Combine(root, entry.Id + ".json");
    private void Validate(QuarantineEntry entry)
    {
        if (!Guid.TryParseExact(entry.Id, "N", out _) || entry.Sha256 is null || entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit))
            throw new IOException("Metadatos de cuarentena no válidos.");
        if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("La carpeta de cuarentena no puede ser un enlace.");
        if (File.Exists(Data(entry)) && (File.GetAttributes(Data(entry)) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("El archivo en cuarentena no puede ser un enlace.");
    }
    private async Task Save(QuarantineEntry entry)
    {
        var temporary = Metadata(entry) + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, Metadata(entry), overwrite: true);
    }
    public IReadOnlyList<QuarantineEntry> List()
    {
        if (!Directory.Exists(root)) return Array.Empty<QuarantineEntry>();
        var entries = new List<QuarantineEntry>();
        foreach (var path in Directory.GetFiles(root, "*.json"))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<QuarantineEntry>(File.ReadAllText(path));
                if (entry is null) continue;
                Validate(entry);
                if (File.Exists(Data(entry))) entries.Add(entry);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return entries.OrderByDescending(e => e.Created).ToList();
    }
    public async Task<QuarantineEntry> QuarantineAsync(string path, string expectedHash)
    {
        await gate.WaitAsync();
        try
        {
            var entry = new QuarantineEntry { OriginalPath = Path.GetFullPath(path), Sha256 = expectedHash };
            Directory.CreateDirectory(root);
            Validate(entry);
            if (!Path.GetExtension(path).Equals(".apk", StringComparison.OrdinalIgnoreCase)) throw new IOException("Solo se aíslan archivos APK.");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("El APK no puede ser un enlace.");
            await Save(entry);
            // Copy first on every volume; never remove an original before verifying a durable copy.
            await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await using var destination = new FileStream(Data(entry), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(destination);
                destination.Flush(flushToDisk: true);
            }
            var hash = await ApkInputService.HashAsync(Data(entry));
            if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                entry.State = "Copia sin verificar; original conservado";
                await Save(entry);
                throw new IOException("La huella cambió. El original se conservó.");
            }
            entry.State = "Copia verificada; original pendiente de retirar";
            await Save(entry);
            // Verify source again in case it changed since the previous analysis.
            if (!(await ApkInputService.HashAsync(path)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("El original cambió; no se retirará.");
            File.Delete(path);
            entry.State = "En cuarentena";
            await Save(entry);
            return entry;
        }
        finally { gate.Release(); }
    }
    public async Task RestoreAsync(QuarantineEntry entry)
    {
        await gate.WaitAsync();
        try
        {
            Validate(entry);
            if (!(await ApkInputService.HashAsync(Data(entry))).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("La huella de la cuarentena no coincide. No se restauró.");
            if (!Path.GetExtension(entry.OriginalPath).Equals(".apk", StringComparison.OrdinalIgnoreCase)) throw new IOException("La ruta original no es un APK.");
            if (File.Exists(entry.OriginalPath)) throw new IOException("Ya existe un archivo en la ruta original. No se sobrescribirá.");
            var parent = Path.GetDirectoryName(entry.OriginalPath)!;
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("La carpeta original no existe. Vuelve a crearla antes de restaurar.");
            var copied = false;
            try
            {
                await using (var output = new FileStream(entry.OriginalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    copied = true;
                    await using var input = File.OpenRead(Data(entry));
                    await input.CopyToAsync(output);
                    output.Flush(flushToDisk: true);
                }
                if (!(await ApkInputService.HashAsync(entry.OriginalPath)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("No se pudo verificar la restauración.");
            }
            catch
            {
                if (copied) File.Delete(entry.OriginalPath);
                throw;
            }
            entry.State = "Restaurado";
            await Save(entry);
            File.Delete(Data(entry));
        }
        finally { gate.Release(); }
    }
}
