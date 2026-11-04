namespace PulseAPK.Core.Services;

public sealed class LocalAnalysisService(ApktoolRunner apktool, SmaliAnalyserService analyser, IDefenderScanService defender, ILocalProcessRunner processes, ISettingsService settings)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseAPK");
    public static string AnalysesRoot => Path.Combine(DataRoot, "analyses");
    public static string ReportsRoot => Path.Combine(DataRoot, "reports-local");

    public async Task<LocalAnalysisReport> AnalyseAsync(string path, Action<string> progress, CancellationToken token = default, bool includeLibraries = true)
    {
        if (!await gate.WaitAsync(0, token)) throw new InvalidOperationException("Ya hay un análisis en curso.");
        var report = new LocalAnalysisReport { ApkPath = path, IncludesLibraries = includeLibraries };
        report.Stages = new[] { "Validación y huella", "Microsoft Defender", "Descompilación", "Indicios del código", "Permisos y componentes" }.Select(n => new AnalysisStage { Name = n }).ToList();
        try
        {
            report.ApkPath = Path.GetFullPath(path);
            using var original = new FileStream(report.ApkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await Stage(0, async () =>
            {
                token.ThrowIfCancellationRequested();
                await Task.Run(() => ApkInputService.Validate(report.ApkPath), token);
                using (var archive = System.IO.Compression.ZipFile.OpenRead(report.ApkPath))
                {
                    report.NativeLibraries = archive.Entries.Count(e => e.FullName.EndsWith(".so", StringComparison.OrdinalIgnoreCase));
                    report.PackagedDexFiles = archive.Entries.Count(e => e.FullName.EndsWith(".dex", StringComparison.OrdinalIgnoreCase));
                }
                report.Sha256 = await ApkInputService.HashAsync(report.ApkPath, token);
                report.AdditionalCodeFiles = ApkInputService.AdditionalCodeInventory(report.ApkPath);
                report.Stages[0].Detail = report.Sha256;
            });
            if (report.Stages[0].State != StageState.Completed) return report;
            await Stage(1, async () =>
            {
                report.Defender = await defender.ScanAsync(report.ApkPath, token);
                report.Stages[1].Detail = report.Defender.Detail;
                report.Stages[1].State = report.Defender.State switch
                {
                    AntivirusState.Unavailable => StageState.Unavailable,
                    AntivirusState.Error or AntivirusState.ReviewRequired => StageState.Failed,
                    AntivirusState.Cancelled => StageState.Cancelled,
                    AntivirusState.NotRun => StageState.Unavailable,
                    _ => StageState.Completed
                };
            });
            report.Workspace = Path.Combine(AnalysesRoot, report.Id);
            await Stage(2, async () =>
            {
                var tool = ApktoolRunner.ResolveConfiguredApktoolPath(settings.Settings.ApktoolPath, settings.SettingsDirectory);
                var java = await processes.RunAsync("java", new[] { "-version" }, token);
                report.JavaVersion = java.Output;
                if (java.ExitCode != 0) throw new IOException("Java no está disponible: " + java.Output);
                if (Path.GetExtension(tool).Equals(".jar", StringComparison.OrdinalIgnoreCase))
                {
                    var version = await processes.RunAsync("java", new[] { "-jar", tool, "--version" }, token);
                    report.ApktoolVersion = version.Output;
                    if (version.ExitCode != 0) throw new IOException("No se pudo iniciar Apktool: " + version.Output);
                }
                Directory.CreateDirectory(report.Workspace);
                apktool.OutputDataReceived += progress;
                try
                {
                    var result = await apktool.RunDecompileAsync(report.ApkPath, Path.Combine(report.Workspace, "code"), false, true, false, cancellationToken: token);
                    if (result.ExitCode != 0) throw new IOException($"Apktool: código {result.ExitCode}. " + string.Join("\n", result.Stderr.Concat(result.Stdout).TakeLast(12)));
                }
                finally { apktool.OutputDataReceived -= progress; }
            });
            if (report.Stages[2].State != StageState.Completed) return report;
            await Stage(3, async () =>
            {
                var result = await analyser.AnalyseProjectAsync(Path.Combine(report.Workspace, "code"), progress, token, includeLibraries);
                report.SmaliFilesFound = result.SmaliFilesFound;
                report.AnalyzedFiles = result.AnalyzedFiles;
                report.SkippedLibraryFiles = result.SkippedLibraryFiles;
                Add(result.RootChecks, "Root", "Comprobaciones de root; también aparecen en aplicaciones legítimas.");
                Add(result.EmulatorChecks, "Emulador", "Detección de emuladores; por sí sola no prueba evasión maliciosa.");
                Add(result.HardcodedCredentials, "Credenciales", "Posible dato sensible incluido en el código; requiere revisar el contexto.");
                Add(result.SqlQueries, "SQL", "Uso de consultas SQL; no implica inyección ni malware por sí mismo.");
                Add(result.HttpUrls, "Red", "Dirección de red encontrada en código; no demuestra una conexión ni exfiltración.");
                report.Stages[3].Detail = $"{report.Findings.Count} indicios; {result.AnalyzedFiles} archivos analizados; {result.SkippedLibraryFiles} omitidos; {result.ReadErrors} errores de lectura.";
                if (result.ReadErrors > 0) throw new IOException("Análisis parcial: hubo archivos que no pudieron analizarse.");
                void Add(List<Finding> items, string category, string explanation)
                {
                    report.Findings.AddRange(items.Select(f => new CodeEvidence(category, explanation, Path.GetRelativePath(report.Workspace, f.FilePath), f.LineNumber, f.Context)));
                }
            });
            await Stage(4, async () =>
            {
                var manifestDirectory = Path.Combine(report.Workspace, "manifest");
                var decoded = await apktool.RunManifestDecodeAsync(report.ApkPath, manifestDirectory, token);
                if (decoded.ExitCode != 0) throw new IOException("No se pudo decodificar el manifiesto: " + string.Join("\n", decoded.Stderr.Concat(decoded.Stdout).TakeLast(10)));
                report.Manifest = await Task.Run(() => ManifestAnalysisService.Analyse(Path.Combine(manifestDirectory, "AndroidManifest.xml"), token), token);
                report.Stages[4].Detail = $"{report.Manifest.Permissions.Count} permisos declarados; {report.Manifest.Findings.Count} observaciones para revisar.";
            });
            return report;
        }
        catch (OperationCanceledException)
        {
            var defenderWasRunning = report.Stages[1].State == StageState.Running;
            foreach (var stage in report.Stages.Where(s => s.State is StageState.Running or StageState.Pending)) stage.State = StageState.Cancelled;
            if (report.Defender.State == AntivirusState.NotRun)
                report.Defender = new(AntivirusState.Cancelled, defenderWasRunning
                    ? "Solicitud cancelada; no se puede confirmar que Defender haya terminado en segundo plano."
                    : "Defender no llegó a iniciarse.");
            return report;
        }
        catch (Exception ex)
        {
            report.Warnings.Add(ex.Message);
            foreach (var stage in report.Stages.Where(s => s.State == StageState.Running)) { stage.State = StageState.Failed; stage.Detail = ex.Message; }
            if (report.Stages[0].State == StageState.Pending) { report.Stages[0].State = StageState.Failed; report.Stages[0].Detail = ex.Message; }
            return report;
        }
        finally
        {
            report.Finished = DateTimeOffset.Now;
            foreach (var stage in report.Stages.Where(s => s.State == StageState.Pending)) { stage.State = StageState.Unavailable; stage.Detail = "No ejecutada por un fallo anterior."; }
            gate.Release();
        }

        async Task Stage(int index, Func<Task> action)
        {
            token.ThrowIfCancellationRequested();
            var stage = report.Stages[index];
            stage.State = StageState.Running;
            progress($"Etapa {index + 1}/{report.Stages.Count} · {stage.Name}");
            try { await action(); if (stage.State == StageState.Running) stage.State = StageState.Completed; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { stage.State = StageState.Failed; stage.Detail = ex.Message; progress(ex.Message); }
        }
    }

    public static void DeleteGeneratedData(LocalAnalysisReport report)
    {
        var expected = Path.GetFullPath(Path.Combine(AnalysesRoot, report.Id));
        if (!Guid.TryParseExact(report.Id, "N", out _) || !string.Equals(expected, Path.GetFullPath(report.Workspace), StringComparison.OrdinalIgnoreCase))
            throw new IOException("La carpeta no corresponde a un análisis generado.");
        if (!Directory.Exists(expected)) return;
        if ((File.GetAttributes(expected) & FileAttributes.ReparsePoint) != 0) throw new IOException("La carpeta es un enlace.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(expected, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("La carpeta contiene enlaces; no se borrará automáticamente.");
        Directory.Delete(expected, recursive: true);
    }
}
