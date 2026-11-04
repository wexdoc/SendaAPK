using System.Text.Json;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;

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
            await Save(entry);
            // Keep a delete-capable handle to this exact file open until the verified copy is durable.
            await using (var source = OpenLocked(path, createNew: false, writable: false))
            {
                await using (var destination = new FileStream(Data(entry), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
                {
                    await source.CopyToAsync(destination);
                    destination.Flush(flushToDisk: true);
                    destination.Position = 0;
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(destination));
                    source.Position = 0;
                    var sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(source));
                    if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase) || !sourceHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        entry.State = "Copia sin verificar; original conservado";
                        await Save(entry);
                        throw new IOException("La huella cambió. El original se conservó.");
                    }
                    entry.State = "Copia verificada; original pendiente de retirar";
                    await Save(entry);
                    MarkForDeletion(source.SafeFileHandle);
                    await source.DisposeAsync();
                }
            }
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
            if (!Path.GetExtension(entry.OriginalPath).Equals(".apk", StringComparison.OrdinalIgnoreCase)) throw new IOException("La ruta original no es un APK.");
            if (File.Exists(entry.OriginalPath)) throw new IOException("Ya existe un archivo en la ruta original. No se sobrescribirá.");
            var parent = Path.GetDirectoryName(entry.OriginalPath)!;
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("La carpeta original no existe. Vuelve a crearla antes de restaurar.");
            await using var input = OpenLocked(Data(entry), createNew: false, writable: false);
            await using var output = OpenLocked(entry.OriginalPath, createNew: true, writable: true);
            var verified = false;
            try
            {
                await input.CopyToAsync(output);
                output.Flush(flushToDisk: true);
                input.Position = 0;
                output.Position = 0;
                var inputHash = Convert.ToHexString(await SHA256.HashDataAsync(input));
                var outputHash = Convert.ToHexString(await SHA256.HashDataAsync(output));
                if (!inputHash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase) || !outputHash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("No se pudo verificar la restauración.");
                verified = true;
                entry.State = "Restaurado; retirada de copia de cuarentena pendiente";
                await Save(entry);
                MarkForDeletion(input.SafeFileHandle);
                await input.DisposeAsync();
            }
            catch
            {
                if (!verified) MarkForDeletion(output.SafeFileHandle);
                throw;
            }
            entry.State = "Restaurado";
            await Save(entry);
        }
        finally { gate.Release(); }
    }

    private static FileStream OpenLocked(string path, bool createNew, bool writable)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("La cuarentena segura está disponible por ahora solo en Windows.");
        const uint genericRead = 0x80000000, genericWrite = 0x40000000, delete = 0x00010000;
        const uint shareRead = 1, openExisting = 3, createNewMode = 1, openReparsePoint = 0x00200000;
        var handle = CreateFileW(path, genericRead | delete | (writable ? genericWrite : 0), shareRead, IntPtr.Zero,
            createNew ? createNewMode : openExisting, openReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("No se pudo bloquear el archivo de forma segura: " + new Win32Exception(error).Message);
        }
        if (!GetFileInformationByHandleEx(handle, 9, out var attributes, (uint)Marshal.SizeOf<FileAttributeTagInfo>()) ||
            (attributes.FileAttributes & (0x400u | 0x10u)) != 0)
        {
            handle.Dispose();
            throw new IOException("El archivo no se puede verificar o es un enlace.");
        }
        return new FileStream(handle, writable ? FileAccess.ReadWrite : FileAccess.Read);
    }

    private static void MarkForDeletion(SafeFileHandle handle)
    {
        byte deleteFile = 1;
        if (!SetFileInformationByHandle(handle, 4, ref deleteFile, 1))
            throw new IOException("No se pudo retirar el archivo bloqueado: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo { public uint FileAttributes; public uint ReparseTag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out FileAttributeTagInfo information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref byte information, uint size);
}
