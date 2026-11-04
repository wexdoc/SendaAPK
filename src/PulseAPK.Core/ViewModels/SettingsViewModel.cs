// Modified for SendaAPK (2026); original work remains credited in NOTICE.
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PulseAPK.Core.Abstractions;
using PulseAPK.Core.Services;
using Properties = PulseAPK.Core.Properties;

namespace PulseAPK.Core.ViewModels;

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly IFilePickerService _filePickerService;
    private readonly IDialogService _dialogService;
    private readonly IToolRepository _toolRepository;
    private readonly IToolDownloadService _toolDownloadService;
    private readonly AdbService _adbService;
    private readonly LocalizationService _localizationService;
    private readonly IThemeService _themeService;
    private readonly OperationCoordinator _operations;
    private readonly IDispatcherService? _dispatcher;
    private OperationTicket? _activeDownload;
    private bool _disposed;

    [ObservableProperty]
    private string _apktoolPath;

    [ObservableProperty]
    private string _ubersignPath;

    [ObservableProperty]
    private string _adbPath;

    [ObservableProperty]
    private string _adbPathWatermark = "Auto-detect if empty";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadApktoolCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadUbersignerCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelDownloadCommand))]
    [NotifyPropertyChangedFor(nameof(CanModifyToolPaths))]
    private bool _isDownloadingTools;

    [ObservableProperty]
    private string _downloadStatus = string.Empty;

    public bool CanModifyToolPaths => !_operations.HasWork && !IsDownloadingTools;

    [ObservableProperty]
    private bool _isDeviceToolsEnabled;
    
    [ObservableProperty]
    private LanguageItem _selectedLanguage;

    [ObservableProperty]
    private ThemeModeItem _selectedThemeMode = null!;
    
    public List<LanguageItem> AvailableLanguages => _localizationService.AvailableLanguages;
    private List<ThemeModeItem> _availableThemeModes = [];

    public List<ThemeModeItem> AvailableThemeModes
    {
        get => _availableThemeModes;
        private set => SetProperty(ref _availableThemeModes, value);
    }

    public SettingsViewModel(
        ISettingsService settingsService,
        IFilePickerService filePickerService,
        IDialogService dialogService,
        IToolRepository toolRepository,
        IToolDownloadService toolDownloadService,
        AdbService adbService,
        LocalizationService localizationService,
        IThemeService themeService,
        OperationCoordinator? operations = null,
        IDispatcherService? dispatcher = null)
    {
        _settingsService = settingsService;
        _filePickerService = filePickerService;
        _dialogService = dialogService;
        _toolRepository = toolRepository;
        _toolDownloadService = toolDownloadService;
        _adbService = adbService;
        _localizationService = localizationService;
        _themeService = themeService;
        _operations = operations ?? new OperationCoordinator(null);
        _dispatcher = dispatcher;
        _operations.Changed += OnOperationsChanged;

        _apktoolPath = _settingsService.Settings.ApktoolPath;
        _ubersignPath = _settingsService.Settings.UbersignPath;
        _adbPath = _settingsService.Settings.AdbPath;
        _selectedLanguage = _localizationService.CurrentLanguage;
        _isDeviceToolsEnabled = _settingsService.Settings.IsDeviceToolsEnabled;

        RefreshThemeModes(_settingsService.Settings.ThemeMode);
        _localizationService.PropertyChanged += OnLocalizationChanged;

        NormalizeManagedToolPathsIfMissing();
        _ = RefreshAdbPathWatermarkAsync();
    }

    partial void OnApktoolPathChanged(string value)
    {
        _settingsService.Settings.ApktoolPath = value;
        _settingsService.Save();
    }

    partial void OnUbersignPathChanged(string value)
    {
        _settingsService.Settings.UbersignPath = value;
        _settingsService.Save();
    }

    partial void OnAdbPathChanged(string value)
    {
        _settingsService.Settings.AdbPath = value;
        _settingsService.Save();
        _ = RefreshAdbPathWatermarkAsync();
    }
    
    partial void OnIsDeviceToolsEnabledChanged(bool value)
    {
        _settingsService.Settings.IsDeviceToolsEnabled = value;
        _settingsService.Save();
    }

    partial void OnSelectedLanguageChanged(LanguageItem value)
    {
        if (value != null && value.Code != _localizationService.CurrentLanguage.Code)
        {
            _localizationService.SetLanguage(value.Code);
            _settingsService.Settings.SelectedLanguage = value.Code;
            _settingsService.Save();
        }
    }

    partial void OnSelectedThemeModeChanged(ThemeModeItem value)
    {
        if (value is null)
        {
            return;
        }

        _settingsService.Settings.ThemeMode = value.Key;
        _settingsService.Save();
        _themeService.ApplyTheme(value.Key);
    }

    [RelayCommand]
    private async Task BrowseApktool()
    {
        if (!CanModifyToolPaths) return;
        var file = await _filePickerService.OpenFileAsync("Apktool files (*.jar;*.bat;*.cmd;*.exe)|*.jar;*.bat;*.cmd;*.exe|All Files (*.*)|*.*");
        if (file != null && CanModifyToolPaths)
        {
            ApktoolPath = file;
        }
    }

    [RelayCommand]
    private async Task BrowseUbersign()
    {
        if (!CanModifyToolPaths) return;
        var file = await _filePickerService.OpenFileAsync("Jar/Exe Files (*.jar;*.exe)|*.jar;*.exe|All Files (*.*)|*.*");
        if (file != null && CanModifyToolPaths)
        {
            UbersignPath = file;
        }
    }

    [RelayCommand]
    private async Task BrowseAdb()
    {
        if (!CanModifyToolPaths) return;
        var file = await _filePickerService.OpenFileAsync("ADB|adb;adb.exe|All Files (*.*)|*.*");
        if (file != null && CanModifyToolPaths)
        {
            AdbPath = file;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartDownload))]
    private async Task DownloadApktool()
    {
        await DownloadToolAsync(
            token => _toolDownloadService.DownloadApktoolAsync(token),
            path => ApktoolPath = path,
            Properties.Resources.ResourceManager.GetString("ToolNameApktool") ?? "Apktool",
            warnUnverified: true);
    }

    [RelayCommand(CanExecute = nameof(CanStartDownload))]
    private async Task DownloadUbersigner()
    {
        await DownloadToolAsync(
            token => _toolDownloadService.DownloadUbersignerAsync(token),
            path => UbersignPath = path,
            Properties.Resources.ResourceManager.GetString("ToolNameUbersigner") ?? "Ubersigner");
    }

    private bool CanStartDownload() => !IsDownloadingTools;
    private bool CanCancelDownload() => IsDownloadingTools && _activeDownload?.CanCancel == true;

    [RelayCommand(CanExecute = nameof(CanCancelDownload))]
    private void CancelDownload()
    {
        _activeDownload?.Cancel();
        DownloadStatus = "Cancelando la descarga; espera a que termine de detenerse.";
        CancelDownloadCommand.NotifyCanExecuteChanged();
    }

    private async Task DownloadToolAsync(
        Func<CancellationToken, Task<ToolDownloadResult>> action,
        Action<string> applyPath,
        string toolDisplayName,
        bool warnUnverified = false)
    {
        if (IsDownloadingTools)
        {
            return;
        }

        OperationTicket? ticket = null;
        try
        {
            IsDownloadingTools = true;
            ticket = _operations.Enqueue("Descarga · " + toolDisplayName);
            _activeDownload = ticket;
            CancelDownloadCommand.NotifyCanExecuteChanged();
            DownloadStatus = "Esperando turno para descargar la herramienta...";
            await ticket.WaitAsync();
            ticket.Token.ThrowIfCancellationRequested();
            if (warnUnverified)
            {
                ticket.Stage("Aviso de integridad de Apktool");
                await _dialogService.ShowWarningAsync("La descarga de Apktool no incluye una suma de verificación independiente en este flujo. Comprueba su origen e integridad antes de utilizarla.", "Integridad de la herramienta");
                ticket.Token.ThrowIfCancellationRequested();
            }
            ticket.Stage("Descargando y comprobando " + toolDisplayName);
            DownloadStatus = "Descargando y comprobando " + toolDisplayName + "...";
            var result = await action(ticket.Token);
            ticket.Token.ThrowIfCancellationRequested();
            applyPath(result.Path);
            ticket.Complete();
            DownloadStatus = result.Downloaded
                ? warnUnverified ? "Herramienta descargada; su integridad independiente no se verificó." : "Herramienta descargada y verificada."
                : "Se conservó la herramienta existente; no se verificó su integridad en esta operación.";

            if (result.Downloaded)
            {
                var successTemplate = Properties.Resources.ResourceManager.GetString("ToolDownloadedSuccessfullyMessage")
                    ?? "{0} downloaded successfully.";
                await _dialogService.ShowInfoAsync(string.Format(successTemplate, toolDisplayName), Properties.Resources.SettingsHeader);
            }
        }
        catch (OperationCanceledException) { DownloadStatus = "Descarga cancelada. Se conservó la herramienta anterior."; }
        catch (Exception ex)
        {
            DownloadStatus = "No se completó la descarga. Se conservó la herramienta anterior.";
            await _dialogService.ShowErrorAsync($"No se pudo descargar {toolDisplayName}: {ex.Message}", Properties.Resources.SettingsHeader);
        }
        finally
        {
            _activeDownload = null;
            ticket?.Dispose();
            IsDownloadingTools = false;
        }
    }

    private void OnOperationsChanged(object? sender, EventArgs e)
    {
        void Refresh()
        {
            if (_disposed) return;
            OnPropertyChanged(nameof(CanModifyToolPaths));
            CancelDownloadCommand.NotifyCanExecuteChanged();
        }
        if (_dispatcher is not null && !_dispatcher.CheckAccess()) _ = _dispatcher.InvokeAsync(Refresh);
        else Refresh();
    }

    private void NormalizeManagedToolPathsIfMissing()
    {
        var changed = false;

        if (IsManagedToolMissing(ApktoolPath))
        {
            ApktoolPath = string.Empty;
            changed = true;
        }

        if (IsManagedToolMissing(UbersignPath))
        {
            UbersignPath = string.Empty;
            changed = true;
        }

        if (changed)
        {
            _settingsService.Save();
        }
    }

    private bool IsManagedToolMissing(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return false;
        }

        if (File.Exists(configuredPath))
        {
            return false;
        }

        var normalizedToolFolder = Path.GetFullPath(_toolRepository.ToolsDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedConfiguredPath = Path.GetFullPath(configuredPath);

        return normalizedConfiguredPath.StartsWith(normalizedToolFolder, StringComparison.OrdinalIgnoreCase);
    }


    private async Task RefreshAdbPathWatermarkAsync()
    {
        if (!string.IsNullOrWhiteSpace(AdbPath))
        {
            AdbPathWatermark = "Auto-detect if empty";
            return;
        }

        var detectedPath = await _adbService.ResolveAdbPathAsync();
        if (!string.IsNullOrWhiteSpace(AdbPath))
        {
            return;
        }

        AdbPathWatermark = string.IsNullOrWhiteSpace(detectedPath)
            ? "Auto-detect if empty"
            : detectedPath;
    }

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]" || e.PropertyName == string.Empty)
        {
            RefreshThemeModes(SelectedThemeMode?.Key);
        }
    }

    private void RefreshThemeModes(string? selectedThemeKey)
    {
        AvailableThemeModes =
        [
            new ThemeModeItem("dark_mode", _localizationService["ThemeModeDark"]),
            new ThemeModeItem("light_mode", _localizationService["ThemeModeLight"])
        ];

        SelectedThemeMode = ResolveThemeMode(selectedThemeKey);
    }

    private ThemeModeItem ResolveThemeMode(string? themeMode)
    {
        return AvailableThemeModes.FirstOrDefault(mode =>
                   string.Equals(mode.Key, themeMode, StringComparison.OrdinalIgnoreCase))
               ?? AvailableThemeModes[0];
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _localizationService.PropertyChanged -= OnLocalizationChanged;
        _operations.Changed -= OnOperationsChanged;
        _activeDownload?.Cancel();
        _disposed = true;
    }
}

public sealed record ThemeModeItem(string Key, string Name);

internal static class CommandLogFormatter
{
    public static string FormatCommandResult(PulseAPK.Core.Models.AdbCommandResult result)
    {
        var output = string.Join(
            Environment.NewLine,
            new[] { result.StandardOutput, result.StandardError }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.TrimEnd()));

        return string.Join(
            Environment.NewLine,
            $"Command: {result.CommandText}",
            "Output:",
            string.IsNullOrWhiteSpace(output) ? "(empty)" : output);
    }
}
