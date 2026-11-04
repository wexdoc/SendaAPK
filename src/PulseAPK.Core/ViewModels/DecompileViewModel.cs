// Modified for SendaAPK (2026); original work remains credited in NOTICE.
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PulseAPK.Core.Abstractions;
using PulseAPK.Core.Services;
using PulseAPK.Core.Utils;
using Properties = PulseAPK.Core.Properties;

namespace PulseAPK.Core.ViewModels;

public partial class DecompileViewModel : ObservableObject, IDisposable
{
    private const int MaxLogCharacters = 900_000;
    private const int LogTrimTargetCharacters = 850_000;
    private const int LogFlushDelayMilliseconds = 150;

    private readonly Queue<string> _logLines = new();
    private readonly object _logLock = new();
    private int _logCharCount;
    private bool _logFlushScheduled;
    private long _logVersion;
    private CancellationTokenSource? _logFlushCancellationTokenSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHintVisible))]
    [NotifyCanExecuteChangedFor(nameof(RunDecompileCommand))]
    private string _apkPath = string.Empty;

    [ObservableProperty]
    private bool _decodeResources = true;

    [ObservableProperty]
    private bool _decodeSources = true;

    [ObservableProperty]
    private bool _keepOriginalManifest;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunDecompileCommand))]
    private bool _extractToApkFolder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunDecompileCommand))]
    private string? _outputFolder;

    [ObservableProperty]
    private string _consoleLog;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunDecompileCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelDecompileCommand))]
    private bool _isRunning;

    private bool _isConsolePreviewActive = true;
    private bool _disposed;
    private OperationTicket? _activeDecompile;

    private readonly IFilePickerService _filePickerService;
    private readonly ISettingsService _settingsService;
    private readonly ApktoolRunner _apktoolRunner;
    private readonly IDialogService _dialogService;
    private readonly IDispatcherService _dispatcherService;
    private readonly ISystemService _systemService;
    private readonly IAppLogService? _appLogService;
    private readonly OperationCoordinator _operations;

    public bool IsHintVisible => string.IsNullOrEmpty(ApkPath);

    public DecompileViewModel(
        IFilePickerService filePickerService,
        ISettingsService settingsService,
        ApktoolRunner apktoolRunner,
        IDialogService dialogService,
        IDispatcherService dispatcherService,
        ISystemService systemService,
        IAppLogService? appLogService = null,
        OperationCoordinator? operations = null)
    {
        _filePickerService = filePickerService;
        _settingsService = settingsService;
        _apktoolRunner = apktoolRunner;
        _dialogService = dialogService;
        _dispatcherService = dispatcherService;
        _systemService = systemService;
        _appLogService = appLogService;
        _operations = operations ?? new OperationCoordinator(null);
        
        _consoleLog = Properties.Resources.WaitingForCommand;

        _apktoolRunner.OutputDataReceived += OnOutputDataReceived;

        OutputFolder = PathUtils.GetDefaultDecompilePath();

        UpdateCommandPreview();
        RunDecompileCommand.NotifyCanExecuteChanged();
    }

    partial void OnApkPathChanged(string value)
    {
        if (ExtractToApkFolder)
        {
            var apkDerivedOutputFolder = GetApkDerivedOutputFolder(value);
            if (!string.IsNullOrWhiteSpace(apkDerivedOutputFolder))
            {
                OutputFolder = apkDerivedOutputFolder;
            }
        }
        else if (string.Equals(OutputFolder, PathUtils.GetDefaultDecompilePath(), StringComparison.OrdinalIgnoreCase))
        {
            OutputFolder = GetWorkspaceOutputFolder(value);
        }

        UpdateCommandPreview();
        RunDecompileCommand.NotifyCanExecuteChanged();
    }

    partial void OnDecodeResourcesChanged(bool value) => UpdateCommandPreview();
    partial void OnDecodeSourcesChanged(bool value) => UpdateCommandPreview();
    partial void OnKeepOriginalManifestChanged(bool value) => UpdateCommandPreview();

    partial void OnExtractToApkFolderChanged(bool value)
    {
        if (value)
        {
            var apkDerivedOutputFolder = GetApkDerivedOutputFolder(ApkPath);
            if (!string.IsNullOrWhiteSpace(apkDerivedOutputFolder))
            {
                OutputFolder = apkDerivedOutputFolder;
            }
        }
        else
        {
            OutputFolder = GetWorkspaceOutputFolder(ApkPath);
        }

        UpdateCommandPreview();
    }

    partial void OnOutputFolderChanged(string? value) => UpdateCommandPreview();

    private static string GetWorkspaceOutputFolder(string apkPath)
    {
        var root = PathUtils.GetDefaultDecompilePath();
        var name = Path.GetFileNameWithoutExtension(apkPath);
        return string.IsNullOrWhiteSpace(name) ? root : Path.Combine(root, name);
    }

    [RelayCommand]
    private async Task BrowseApk()
    {
        var file = await _filePickerService.OpenFileAsync("APK Files (*.apk)|*.apk|All Files (*.*)|*.*");
        if (file != null)
        {
            var (isValid, message) = FileSanitizer.ValidateApk(file);
            if (!isValid)
            {
                await _dialogService.ShowErrorAsync(message, "Invalid APK File");
                return;
            }
            ApkPath = file;
        }
    }

    [RelayCommand]
    private async Task BrowseOutputFolder()
    {
        var initialDir = OutputFolder;
        
        if (!string.IsNullOrWhiteSpace(initialDir) && !Directory.Exists(initialDir))
        {
             initialDir = null;
        }

        var folder = await _filePickerService.OpenFolderAsync(initialDir);

        if (folder != null)
        {
            var projectName = Path.GetFileNameWithoutExtension(ApkPath);
            OutputFolder = Path.Combine(folder, string.IsNullOrWhiteSpace(projectName) ? "proyecto-apk" : projectName);
        }
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        var path = OutputFolder;
        if (string.IsNullOrWhiteSpace(path) )
        {
             _dialogService.ShowWarningAsync(Properties.Resources.Error_OutputFolderNotSet);
             return;
        }
        
        if (!Directory.Exists(path))
        {
            _dialogService.ShowWarningAsync(string.Format(Properties.Resources.Error_FolderNotFound, path));
            return;
        }

        _systemService.OpenFolder(path);
    }

    [RelayCommand(CanExecute = nameof(CanRunDecompile))]
    private async Task RunDecompile()
    {
        if (string.IsNullOrWhiteSpace(ApkPath))
        {
            await _dialogService.ShowWarningAsync("Please select an APK file to decompile.", "Missing APK");
            return;
        }

        var apktoolPath = _settingsService.Settings.ApktoolPath?.Trim();
        if (string.IsNullOrWhiteSpace(apktoolPath))
        {
            await _dialogService.ShowWarningAsync(Properties.Resources.Error_MissingApktool, Properties.Resources.SettingsHeader);
            return;
        }

        try
        {
            ApktoolRunner.ResolveConfiguredApktoolPath(apktoolPath, _settingsService.SettingsDirectory);
        }
        catch (FileNotFoundException)
        {
            await _dialogService.ShowErrorAsync(string.Format(Properties.Resources.Error_InvalidApktoolPath, apktoolPath), Properties.Resources.Error_InvalidApkFile);
            RunDecompileCommand.NotifyCanExecuteChanged();
            return;
        }

        SetConsoleLog(Properties.Resources.StartingApktool);

        string normalizedOutputDir;

        try
        {
            var outputDir = ResolveOutputDirectory();
            if (string.IsNullOrWhiteSpace(outputDir))
            {
                await _dialogService.ShowErrorAsync("Unable to derive an output folder from the selected APK. Choose an output folder and try again.", "Invalid output folder");
                return;
            }

            normalizedOutputDir = Path.GetFullPath(outputDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            AppendLog($"{Properties.Resources.DecompileFailed}: {ex.Message}");
            await _dialogService.ShowErrorAsync($"The selected output folder is invalid: {ex.Message}", "Invalid output folder");
            return;
        }

        if (IsHighRiskOutputDirectory(normalizedOutputDir))
        {
            await _dialogService.ShowErrorAsync($"The selected output folder '{normalizedOutputDir}' is unsafe. Choose a different location.", "Invalid output folder");
            return;
        }

        _appLogService?.LogInfo(
            "Decompile",
            $"Decompile requested. timestamp={DateTimeOffset.UtcNow:O}; apkFile={Path.GetFileName(ApkPath)}; outputDir={normalizedOutputDir}; decodeResources={DecodeResources}; decodeSources={DecodeSources}; keepOriginalManifest={KeepOriginalManifest}; extractToApkFolder={ExtractToApkFolder}");

        if (Directory.Exists(normalizedOutputDir))
        {
            await _dialogService.ShowErrorAsync("La carpeta de salida ya existe. Elige una ruta nueva para conservar su contenido.", "Carpeta de salida ocupada");
            return;
        }

        var selectedApk = ApkPath;
        var decodeResources = DecodeResources;
        var decodeSources = DecodeSources;
        var keepOriginalManifest = KeepOriginalManifest;
        var extractToApkFolder = ExtractToApkFolder;
        IsRunning = true;
        using var ticket = _operations.Enqueue("Descompilación · " + Path.GetFileName(selectedApk), request: new OperationRequestSnapshot
        {
            Kind = "decompile", InputPath = selectedApk, OutputPath = normalizedOutputDir,
            DecodeResources = decodeResources, DecodeSources = decodeSources,
            KeepOriginalManifest = keepOriginalManifest, ExtractToApkFolder = extractToApkFolder
        });
        _activeDecompile = ticket;
        CancelDecompileCommand.NotifyCanExecuteChanged();
        var temporaryDir = Path.Combine(Path.GetDirectoryName(normalizedOutputDir)!, ".sendaapk-" + Guid.NewGuid().ToString("N"));
        var preserveTemporary = false;

        try
        {
            await ticket.WaitAsync();
            ticket.Token.ThrowIfCancellationRequested();
            if (Directory.Exists(normalizedOutputDir) || File.Exists(normalizedOutputDir))
                throw new IOException("La ruta de salida ya existe. Elige otra para conservar su contenido.");
            ticket.Stage("Descompilando APK");
            Directory.CreateDirectory(Path.GetDirectoryName(normalizedOutputDir)!);
            var decompileResult = await _apktoolRunner.RunDecompileAsync(selectedApk, temporaryDir, decodeResources, decodeSources, keepOriginalManifest, forceOverwrite: false, ticket.Token);
            var exitCode = decompileResult.ExitCode;

            if (exitCode == 0)
            {
                ticket.Token.ThrowIfCancellationRequested();
                if (!Directory.Exists(temporaryDir)) throw new IOException("La herramienta no produjo la carpeta esperada.");
                try { Directory.Move(temporaryDir, normalizedOutputDir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    preserveTemporary = true;
                    throw new IOException($"No se pudo publicar la salida; el proyecto generado se conservó en {temporaryDir}: {ex.Message}", ex);
                }
                AppendLog(Properties.Resources.DecompileSuccessful);
                ticket.Complete();
                await _dialogService.ShowInfoAsync(Properties.Resources.DecompileSuccessful);
            }
            else
            {
                AppendLog($"{Properties.Resources.DecompileFailed} (Exit Code: {exitCode})");
                await _dialogService.ShowErrorAsync(Properties.Resources.DecompileFailed);
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("Descompilación cancelada.");
        }
        catch (ProcessTerminationUnconfirmedException ex)
        {
            ticket.TerminationUnconfirmed();
            preserveTemporary = true;
            AppendLog($"{ex.Message} Proyecto temporal: {temporaryDir}");
            await _dialogService.ShowErrorAsync(ex.Message);
        }
        catch (Exception ex)
        {
            AppendLog($"{Properties.Resources.DecompileFailed}: {ex.Message}");
            await _dialogService.ShowErrorAsync(ex.Message);
        }
        finally
        {
            if (!preserveTemporary && Directory.Exists(temporaryDir))
            {
                try { DeleteTemporaryProject(temporaryDir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { AppendLog($"No se pudo limpiar el proyecto temporal {temporaryDir}: {ex.Message}"); }
            }
            if (ReferenceEquals(_activeDecompile, ticket))
            {
                _activeDecompile = null;
            }
            IsRunning = false;
            RunDecompileCommand.NotifyCanExecuteChanged();
        }
    }

    private static void DeleteTemporaryProject(string directory)
    {
        if (!Path.GetFileName(directory).StartsWith(".sendaapk-", StringComparison.Ordinal))
            throw new IOException("La carpeta no es un proyecto temporal de SendaAPK.");
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("El proyecto contiene un enlace y debe revisarse manualmente.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("El proyecto contiene un enlace y debe revisarse manualmente.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
        Directory.Delete(directory, recursive: true);
    }

    private bool CanCancelDecompile() => IsRunning && _activeDecompile is { Token.IsCancellationRequested: false };

    [RelayCommand(CanExecute = nameof(CanCancelDecompile))]
    private void CancelDecompile()
    {
        _activeDecompile?.Cancel();
        CancelDecompileCommand.NotifyCanExecuteChanged();
        AppendLog("Cancelando descompilación; espera a que se detenga la herramienta.");
    }

    private void OnOutputDataReceived(string message)
    {
        QueueLogAppend(message);
    }

    private void QueueLogAppend(string message)
    {
        if (_disposed)
        {
            return;
        }

        AppendLog(message);
    }

    private void AppendLog(string message)
    {
        _isConsolePreviewActive = false;

        lock (_logLock)
        {
            var sanitized = message ?? string.Empty;
            _logLines.Enqueue(sanitized);
            _logCharCount += sanitized.Length;

            TrimLogIfNeeded();
            _logVersion++;
            ScheduleLogFlushLocked();
        }
    }

    private void SetConsoleLog(string message)
    {
        _isConsolePreviewActive = false;

        lock (_logLock)
        {
            _logLines.Clear();
            _logCharCount = 0;

            var sanitized = message ?? string.Empty;
            _logLines.Enqueue(sanitized);
            _logCharCount = sanitized.Length;
            _logVersion++;

            ScheduleLogFlushLocked();
        }
    }

    private void ScheduleLogFlushLocked()
    {
        if (_disposed || _logFlushScheduled)
        {
            return;
        }

        _logFlushCancellationTokenSource ??= new CancellationTokenSource();
        var cancellationToken = _logFlushCancellationTokenSource.Token;
        _logFlushScheduled = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(LogFlushDelayMilliseconds, cancellationToken);
                FlushLog(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }, cancellationToken);
    }

    private void FlushLog(CancellationToken cancellationToken)
    {
        string logText;
        long logVersion;

        lock (_logLock)
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                _logFlushScheduled = false;
                return;
            }

            logText = string.Join(Environment.NewLine, _logLines);
            logVersion = _logVersion;
            _logFlushScheduled = false;
        }

        if (!_dispatcherService.CheckAccess())
        {
            _ = InvokeDispatcherSafelyAsync(() =>
            {
                if (_disposed)
                {
                    return;
                }

                if (ShouldApplyLogFlush(logVersion, cancellationToken))
                {
                    ConsoleLog = logText;
                }
            });
        }
        else if (ShouldApplyLogFlush(logVersion, cancellationToken))
        {
            ConsoleLog = logText;
        }
    }

    private async Task InvokeDispatcherSafelyAsync(Action action)
    {
        try
        {
            await _dispatcherService.InvokeAsync(action);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to dispatch decompile log update: {ex}");
        }
    }

    private bool ShouldApplyLogFlush(long logVersion, CancellationToken cancellationToken)
    {
        lock (_logLock)
        {
            return !_disposed
                && !cancellationToken.IsCancellationRequested
                && logVersion == _logVersion;
        }
    }

    private void TrimLogIfNeeded()
    {
        var newlineLength = Environment.NewLine.Length;
        var totalCharacters = _logCharCount + ((_logLines.Count - 1) * newlineLength);
        if (totalCharacters <= MaxLogCharacters)
        {
            return;
        }

        while (_logLines.Count > 0 && totalCharacters > LogTrimTargetCharacters)
        {
            var removed = _logLines.Dequeue();
            _logCharCount -= removed.Length;
            totalCharacters = _logCharCount + ((_logLines.Count - 1) * newlineLength);
        }
    }

    private void UpdateCommandPreview()
    {
        if (!_isConsolePreviewActive)
        {
            return;
        }

        ConsoleLog = BuildCommandPreview();
    }

    private bool CanRunDecompile()
    {
        if (IsRunning || string.IsNullOrWhiteSpace(ApkPath) || !File.Exists(ApkPath))
        {
            return false;
        }

        try
        {
            return !string.IsNullOrWhiteSpace(ResolveOutputDirectory());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string BuildCommandPreview()
    {
        var apktoolPath = _settingsService.Settings.ApktoolPath?.Trim();
        var apktool = string.IsNullOrWhiteSpace(apktoolPath)
            ? "<set apktool path>"
            : $"\"{apktoolPath}\"";

        var apkInput = string.IsNullOrWhiteSpace(ApkPath)
            ? "<select apk>"
            : $"\"{ApkPath}\"";

        var outputDir = ResolveOutputDirectoryPreview();

        var builder = new StringBuilder();
        builder.Append($"java -jar {apktool} d {apkInput} -o \"{outputDir}\"");

        if (!DecodeResources) builder.Append(" -r");
        if (!DecodeSources) builder.Append(" -s");
        if (KeepOriginalManifest) builder.Append(" -m");

        return $"Command preview: {builder}";
    }

    private string ResolveOutputDirectory()
    {
        if (ExtractToApkFolder)
        {
            return GetApkDerivedOutputFolder(ApkPath);
        }

        return !string.IsNullOrWhiteSpace(OutputFolder)
            ? OutputFolder
            : GetApkDerivedOutputFolder(ApkPath);
    }

    private string ResolveOutputDirectoryPreview()
    {
        if (ExtractToApkFolder)
        {
            return !string.IsNullOrWhiteSpace(ApkPath)
                ? GetApkDerivedOutputFolder(ApkPath)
                : "<apk folder>";
        }

        return !string.IsNullOrWhiteSpace(OutputFolder)
            ? OutputFolder
            : !string.IsNullOrWhiteSpace(ApkPath)
                ? GetApkDerivedOutputFolder(ApkPath)
                : "<output folder>";
    }

    private static string GetApkDerivedOutputFolder(string? apkPath)
    {
        if (string.IsNullOrWhiteSpace(apkPath))
        {
            return string.Empty;
        }

        var apkDirectory = Path.GetDirectoryName(apkPath);
        if (string.IsNullOrWhiteSpace(apkDirectory))
        {
            apkDirectory = Directory.GetCurrentDirectory();
        }

        return Path.Combine(apkDirectory, Path.GetFileNameWithoutExtension(apkPath));
    }

    private static bool IsHighRiskOutputDirectory(string outputDir)
    {
        var normalizedOutput = NormalizePath(outputDir);
        var outputRoot = Path.GetPathRoot(normalizedOutput);

        var riskyPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };

        if (!string.IsNullOrWhiteSpace(outputRoot) && string.Equals(normalizedOutput, NormalizePath(outputRoot), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return riskyPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Any(riskyPath => IsSameOrSubPath(riskyPath, normalizedOutput));
    }

    private static bool IsSameOrSubPath(string basePath, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (string.Equals(basePath, candidatePath, comparison))
        {
            return true;
        }

        if (basePath.Length == 1 && (basePath[0] == Path.DirectorySeparatorChar || basePath[0] == Path.AltDirectorySeparatorChar))
        {
            return false;
        }

        var basePathWithSeparator = basePath.EndsWith(Path.DirectorySeparatorChar)
            ? basePath
            : basePath + Path.DirectorySeparatorChar;

        return candidatePath.StartsWith(basePathWithSeparator, comparison);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _activeDecompile?.Cancel();
        _activeDecompile = null;

        lock (_logLock)
        {
            _logFlushCancellationTokenSource?.Cancel();
            _logFlushCancellationTokenSource?.Dispose();
            _logFlushCancellationTokenSource = null;
            _logFlushScheduled = false;
        }

        _apktoolRunner.OutputDataReceived -= OnOutputDataReceived;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.GetPathRoot(fullPath);

        if (!string.IsNullOrWhiteSpace(root) && string.Equals(fullPath, root, comparison))
        {
            return root.Length > 1
                ? root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : root;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
