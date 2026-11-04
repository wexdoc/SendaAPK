using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PulseAPK.Core.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StageState { Pending, Running, Completed, Failed, Cancelled, Unavailable }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AntivirusState { NotRun, NoDetections, Detected, ReviewRequired, Error, Unavailable, Cancelled }
public sealed class AnalysisStage
{
    public string Name { get; set; } = "";
    public StageState State { get; set; }
    public string Detail { get; set; } = "";
}
public sealed record CodeEvidence(string Category, string Explanation, string File, int Line, string Evidence)
{
    public string Location => $"{File}:{Line}";
}
public sealed record DefenderResult(AntivirusState State, string Detail, string Version = "", string SignatureVersion = "", int? ExitCode = null);
public sealed class LocalAnalysisReport
{
    public static string StateLabel(StageState state) => state switch
    {
        StageState.Pending => "Pendiente", StageState.Running => "En curso", StageState.Completed => "Completada",
        StageState.Failed => "Fallida o incompleta", StageState.Cancelled => "Cancelada", _ => "No disponible"
    };
    public static string AntivirusLabel(AntivirusState state) => state switch
    {
        AntivirusState.NoDetections => "Sin detecciones en este análisis", AntivirusState.Detected => "Detección informada por el motor",
        AntivirusState.ReviewRequired => "Resultado ambiguo; requiere revisión", AntivirusState.Error => "Error",
        AntivirusState.Unavailable => "No disponible", AntivirusState.Cancelled => "Cancelado", _ => "No ejecutado"
    };
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ApkPath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? Finished { get; set; }
    public string Workspace { get; set; } = "";
    public string AppVersion { get; set; } = typeof(LocalAnalysisReport).Assembly.GetName().Version?.ToString() ?? "";
    public string ApktoolVersion { get; set; } = "No disponible";
    public string JavaVersion { get; set; } = "No disponible";
    public int NativeLibraries { get; set; }
    public int PackagedDexFiles { get; set; }
    public int SmaliFilesFound { get; set; }
    public int AnalyzedFiles { get; set; }
    public int SkippedLibraryFiles { get; set; }
    public List<string> AdditionalCodeFiles { get; set; } = new();
    public bool IncludesLibraries { get; set; } = true;
    public ManifestAnalysisResult Manifest { get; set; } = new();
    public string Overview => Defender.State == AntivirusState.Detected
        ? "Defender informó una detección. Evita instalar el APK y revisa la evidencia; puedes aislar el archivo."
        : "No hay un diagnóstico concluyente de malware. Revisa las evidencias y las etapas incompletas antes de decidir si instalarlo.";
    public List<AnalysisStage> Stages { get; set; } = new();
    public DefenderResult Defender { get; set; } = new(AntivirusState.NotRun, "Pendiente");
    public List<CodeEvidence> Findings { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string Limitations { get; set; } = "El análisis estático y Defender pueden no detectar malware Android, bibliotecas nativas maliciosas, código ofuscado o descargado. El informe indica la cobertura real y las omisiones según el modo elegido. Permisos declarados no equivalen a permisos concedidos. Configuración, root, SQL y URLs no demuestran una infección. No se comprueba aquí la firma criptográfica del APK. Sin detecciones no significa que el APK sea seguro. No se instala ni ejecuta el APK y no se modifica su contenido.";

    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine("PulseAPK · Informe de análisis local");
        text.AppendLine(Overview);
        text.AppendLine($"Archivo: {ApkPath}\nSHA-256: {Sha256}\nInicio: {Started:O}\nFin: {Finished:O}");
        text.AppendLine($"PulseAPK: {AppVersion}\nApktool: {ApktoolVersion}\nJava: {JavaVersion}");
        text.AppendLine($"Contenido: {PackagedDexFiles} archivos DEX; {NativeLibraries} bibliotecas nativas (sin análisis de comportamiento).\nCobertura Smali: {SmaliFilesFound} encontrados; {AnalyzedFiles} analizados; {SkippedLibraryFiles} omitidos por reglas de bibliotecas.");
        text.AppendLine($"Modo: {(IncludesLibraries ? "amplio, incluye bibliotecas" : "rápido, omite bibliotecas conocidas")}\nPaquete: {Manifest.PackageName}\nAndroid objetivo: {Manifest.TargetSdk}");
        if (AdditionalCodeFiles.Count > 0)
            text.AppendLine($"\nCOBERTURA LIMITADA: {AdditionalCodeFiles.Count} scripts o archivos de código adicional inventariados, sin análisis de comportamiento. La cobertura Smali no incluye estos contenidos.\n" + string.Join("\n", AdditionalCodeFiles));
        foreach (var stage in Stages) text.AppendLine($"{stage.Name}: {StateLabel(stage.State)} — {stage.Detail}");
        text.AppendLine($"\nDEFENDER: {AntivirusLabel(Defender.State)}\n{Defender.Detail}\nVersión: {Defender.Version}; firmas: {Defender.SignatureVersion}");
        text.AppendLine($"\nINDICIOS DEL CÓDIGO: {Findings.Count} (no son un diagnóstico de malware)");
        foreach (var finding in Findings)
            text.AppendLine($"\n[{finding.Category}] {finding.Explanation}\n{finding.File}:{finding.Line}\n{finding.Evidence}");
        text.AppendLine($"\nPERMISOS DECLARADOS ({Manifest.Permissions.Count})\n" + string.Join("\n", Manifest.Permissions));
        text.AppendLine($"\nMANIFIESTO: {Manifest.Findings.Count} observaciones de revisión, no diagnósticos de malware");
        foreach (var finding in Manifest.Findings)
            text.AppendLine($"\n[{finding.Category}] {finding.Explanation}\n{finding.Location}\n{finding.Evidence}");
        foreach (var warning in Warnings) text.AppendLine($"\nAVISO: {warning}");
        text.AppendLine($"\nLÍMITES\n{Limitations}");
        return text.ToString();
    }

    public async Task SaveAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, Id + ".json"), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(Path.Combine(directory, Id + ".txt"), ToText());
    }
}
