using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseAPK.Core.Abstractions;
using PulseAPK.Core.Services;
using System.Collections.ObjectModel;

namespace PulseAPK.Core.ViewModels;

public partial class LocalApkViewModel(LocalAnalysisService analysis, QuarantineService quarantine, IFilePickerService picker, IDialogService dialogs, IDispatcherService dispatcher, OperationCoordinator? coordinator = null) : ObservableObject
{
    private readonly OperationCoordinator operations = coordinator ?? new OperationCoordinator(null);
    private OperationTicket? currentOperation;
    private LocalAnalysisReport? report;
    private readonly object progressLock = new();
    private DateTime lastProgress;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AnalyseCommand))] [NotifyCanExecuteChangedFor(nameof(IsolateCommand))] private string apkPath = "";
    [ObservableProperty] private string status = "Selecciona un APK para comenzar. El archivo no se instala ni se ejecuta.";
    [ObservableProperty] private string summary = "Sin análisis. Defender y los indicios del código se mostrarán por separado.";
    [ObservableProperty] private string resultText = "";
    [ObservableProperty] private string defenderSummary = "Defender: pendiente";
    [ObservableProperty] private bool includeLibraries = true;
    [ObservableProperty] private string findingQuery = "";
    [ObservableProperty] private string findingCount = "Sin hallazgos cargados.";
    public ObservableCollection<CodeEvidence> VisibleFindings { get; } = new();
    partial void OnFindingQueryChanged(string value) => FilterFindings();
    private void FilterFindings()
    {
        VisibleFindings.Clear();
        if (report is null) { FindingCount = "Sin hallazgos cargados."; return; }
        var all = report.Manifest.Findings.Concat(report.Findings).ToList();
        var matches = all.Where(f => string.IsNullOrWhiteSpace(FindingQuery) || $"{f.Category} {f.Explanation} {f.File} {f.Evidence}".Contains(FindingQuery, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var finding in matches) VisibleFindings.Add(finding);
        FindingCount = $"{matches.Count} coincidencias de {all.Count} observaciones.";
    }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(BrowseCommand))] [NotifyCanExecuteChangedFor(nameof(AnalyseCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))] [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(IsolateCommand))] [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))] private bool busy;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RestoreCommand))] private QuarantineEntry? selectedEntry;
    public ObservableCollection<QuarantineEntry> QuarantineEntries { get; } = new();
    private bool Idle() => !Busy;
    private bool CanAnalyse() => !Busy && File.Exists(ApkPath);
    private bool CanCancel() => Busy && currentOperation?.CanCancel == true;
    private bool CanSave() => !Busy && report is not null;
    private bool CanIsolate() => !Busy && report?.Sha256.Length == 64 && File.Exists(report.ApkPath)
        && string.Equals(ApkPath, report.ApkPath, StringComparison.OrdinalIgnoreCase);
    private bool CanRestore() => !Busy && SelectedEntry is not null;
    private bool CanClean() => !Busy && report is not null && Directory.Exists(report.Workspace);

    [RelayCommand(CanExecute = nameof(Idle))]
    private async Task Browse()
    {
        var path = await picker.OpenFileAsync("Archivo APK (*.apk)|*.apk");
        if (path is not null) ApkPath = path;
    }
    [RelayCommand(CanExecute = nameof(CanAnalyse))]
    private async Task Analyse()
    {
        var selectedPath = ApkPath;
        var selectedLibraries = IncludeLibraries;
        Busy = true;
        using var ticket = operations.Enqueue("Análisis · " + Path.GetFileName(selectedPath), request: new OperationRequestSnapshot
        {
            Kind = "analysis", InputPath = selectedPath, IncludeLibraries = selectedLibraries
        });
        currentOperation = ticket;
        CancelCommand.NotifyCanExecuteChanged();
        try
        {
            Status = "En espera de las demás tareas...";
            await ticket.WaitAsync();
            ticket.Token.ThrowIfCancellationRequested();
            report = null;
            ResultText = "";
            VisibleFindings.Clear(); FindingCount = "Análisis en curso...";
            Summary = "Análisis en curso; todavía no hay una conclusión.";
            DefenderSummary = "Defender: pendiente";
            ticket.Stage("Preparando análisis");
            report = await analysis.AnalyseAsync(selectedPath, UpdateProgress, ticket.Token, selectedLibraries);
            ResultText = report.ToText();
            DefenderSummary = "Defender: " + (report.Defender.State switch
            {
                AntivirusState.NoDetections => "sin detecciones en este análisis",
                AntivirusState.Detected => "detección informada por el motor",
                AntivirusState.Unavailable => "no disponible en modo local",
                AntivirusState.Error => "error de análisis",
                AntivirusState.ReviewRequired => "resultado ambiguo; requiere revisión",
                AntivirusState.Cancelled => "cancelado",
                _ => "no ejecutado"
            });
            Summary = $"{report.Findings.Count} indicios del código · {report.Manifest.Findings.Count} observaciones del manifiesto · {report.AnalyzedFiles}/{report.SmaliFilesFound} archivos Smali analizados. " + (report.Stages.All(s => s.State == StageState.Completed) ? "Etapas completadas." : "Análisis parcial: revisa el estado de cada etapa.");
            if (report.AdditionalCodeFiles.Count > 0) Summary += $" Hay {report.AdditionalCodeFiles.Count} scripts/archivos de código adicional sin análisis de comportamiento; consulta la cobertura limitada del informe.";
            FilterFindings();
            Status = ticket.Token.IsCancellationRequested ? "Análisis cancelado o incompleto. Revisa las etapas." : "Análisis terminado. Sin detecciones no significa que el APK sea seguro.";
            await report.SaveAsync(LocalAnalysisService.ReportsRoot);
            Status += " Informes TXT y JSON guardados en: " + LocalAnalysisService.ReportsRoot;
            if (!ticket.Token.IsCancellationRequested && report.Stages.All(s => s.State == StageState.Completed)) ticket.Complete();
        }
        catch (OperationCanceledException) { Status = "Análisis cancelado antes de comenzar o incompleto."; }
        catch (Exception ex) { Status = "No se completó la operación: " + ex.Message; }
        finally
        {
            currentOperation = null; Busy = false; RefreshCommands();
        }
    }
    private void UpdateProgress(string message)
    {
        if (message.StartsWith("Etapa ")) currentOperation?.Stage(message);
        lock (progressLock)
        {
            if (!message.StartsWith("Etapa ") && DateTime.UtcNow - lastProgress < TimeSpan.FromMilliseconds(300)) return;
            lastProgress = DateTime.UtcNow;
        }
        _ = dispatcher.InvokeAsync(() => { if (Busy) Status = message; });
    }
    [RelayCommand(CanExecute = nameof(CanCancel))] private void Cancel() { currentOperation?.Cancel(); Status = "Cancelando; espera a que las herramientas se detengan..."; CancelCommand.NotifyCanExecuteChanged(); }
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        var folder = await picker.OpenFolderAsync(LocalAnalysisService.ReportsRoot);
        if (folder is null || report is null) return;
        Busy = true;
        try { await report.SaveAsync(folder); Status = "Informes TXT y JSON guardados en " + folder; }
        catch (Exception ex) { await dialogs.ShowErrorAsync(ex.Message); }
        finally { Busy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanIsolate))]
    private async Task Isolate()
    {
        if (report is null) return;
        Busy = true;
        try
        {
            if (!await dialogs.ShowQuestionAsync($"¿Mover este APK a cuarentena?\n{report.ApkPath}\nSe retirará de su ubicación actual tras verificar la copia. Puedes restaurarlo después. No se desinfecta el contenido.", "Cuarentena")) return;
            using var ticket = operations.Enqueue("Cuarentena", request: new OperationRequestSnapshot { Kind = "quarantine", InputPath = report.ApkPath });
            currentOperation = ticket;
            CancelCommand.NotifyCanExecuteChanged();
            await ticket.WaitAsync();
            if (!ticket.EnterCriticalSection()) throw new OperationCanceledException();
            CancelCommand.NotifyCanExecuteChanged();
            ticket.Stage("Copiando y verificando; no se interrumpirá la transferencia");
            await quarantine.QuarantineAsync(report.ApkPath, report.Sha256);
            ticket.Complete();
            Status = "APK en cuarentena. No se modificó su contenido.";
        }
        catch (OperationCanceledException) { Status = "Cuarentena cancelada antes de comenzar."; }
        catch (Exception ex) { await dialogs.ShowErrorAsync(ex.Message, "No se pudo completar la cuarentena"); }
        finally { currentOperation = null; Busy = false; RefreshQuarantine(); RefreshCommands(); }
    }
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task Restore()
    {
        var entry = SelectedEntry;
        if (entry is null) return;
        Busy = true;
        try
        {
            if (!await dialogs.ShowQuestionAsync($"¿Restaurar el APK sin modificar a esta ruta?\n{entry.OriginalPath}\nSi era malicioso, seguirá siéndolo. No se ejecutará.", "Restaurar APK")) return;
            using var ticket = operations.Enqueue("Restauración", request: new OperationRequestSnapshot { Kind = "quarantine", InputPath = entry.OriginalPath });
            currentOperation = ticket;
            CancelCommand.NotifyCanExecuteChanged();
            await ticket.WaitAsync();
            if (!ticket.EnterCriticalSection()) throw new OperationCanceledException();
            CancelCommand.NotifyCanExecuteChanged();
            ticket.Stage("Restaurando y verificando; no se interrumpirá la transferencia");
            await quarantine.RestoreAsync(entry);
            ticket.Complete();
            Status = "APK restaurado tras comprobar su huella.";
        }
        catch (OperationCanceledException) { Status = "Restauración cancelada antes de comenzar."; }
        catch (Exception ex) { await dialogs.ShowErrorAsync(ex.Message); }
        finally { currentOperation = null; Busy = false; RefreshQuarantine(); RefreshCommands(); }
    }
    [RelayCommand] private void RefreshQuarantine()
    {
        try { QuarantineEntries.Clear(); foreach (var entry in quarantine.List()) QuarantineEntries.Add(entry); }
        catch (Exception ex) { Status = "No se pudo leer la cuarentena: " + ex.Message; }
    }
    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task Clean()
    {
        if (report is null) return;
        Busy = true;
        try
        {
            if (!await dialogs.ShowQuestionAsync("¿Borrar los datos de descompilación de este análisis? Se conservarán el APK y los informes.", "Borrar datos generados")) return;
            using var ticket = operations.Enqueue("Limpieza de datos generados");
            currentOperation = ticket;
            CancelCommand.NotifyCanExecuteChanged();
            await ticket.WaitAsync();
            if (!ticket.EnterCriticalSection()) throw new OperationCanceledException();
            CancelCommand.NotifyCanExecuteChanged();
            ticket.Stage("Borrando datos generados");
            await Task.Run(() => LocalAnalysisService.DeleteGeneratedData(report));
            ticket.Complete();
            Status = "Datos generados eliminados. Los informes se conservaron.";
        }
        catch (OperationCanceledException) { Status = "Limpieza cancelada antes de comenzar."; }
        catch (Exception ex) { await dialogs.ShowErrorAsync(ex.Message); }
        finally { currentOperation = null; Busy = false; RefreshCommands(); }
    }
    private void RefreshCommands()
    {
        AnalyseCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged(); IsolateCommand.NotifyCanExecuteChanged();
        CleanCommand.NotifyCanExecuteChanged(); RestoreCommand.NotifyCanExecuteChanged();
    }
}
