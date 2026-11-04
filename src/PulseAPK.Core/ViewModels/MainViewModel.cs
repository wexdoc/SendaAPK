// Modified for SendaAPK (2026); original work remains credited in NOTICE.
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using PulseAPK.Core.Abstractions;
using PulseAPK.Core;
using PulseAPK.Core.Services;
using Properties = PulseAPK.Core.Properties;

namespace PulseAPK.Core.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private const string SupportWorkUrl = "https://yarygintech.com/support-work/";

    private readonly LocalizationService _localizationService;
    private readonly ISystemService _systemService;
    private readonly ISettingsService _settingsService;
    private readonly OperationCoordinator _operations;
    private readonly IDispatcherService? _dispatcher;
    private readonly Dictionary<Type, object> _persistentViews = new();
    private readonly Queue<string> _pendingApks = new();
    private bool _disposed;

    [ObservableProperty]
    private object _currentView = null!;

    [ObservableProperty]
    private string _windowTitle = Properties.Resources.AppTitle;

    [ObservableProperty]
    private string _selectedMenu = "Decompile";

    public string MenuDecompileLabel => _localizationService["MenuDecompile"];
    public string MenuBuildLabel => _localizationService["MenuBuild"];
    public string MenuPatchLabel => _localizationService["MenuPatch"];
    public string MenuAnalyserLabel => "Analizar APK";
    public string MenuDeviceToolsLabel => _localizationService["MenuDeviceTools"];
    public string MenuSettingsLabel => _localizationService["MenuSettings"];
    public string MenuAboutLabel => _localizationService["MenuAbout"];

    public bool IsSupportWorkVisible => true;

    public bool IsDecompileSelected => SelectedMenu == "Decompile";
    public bool IsBuildSelected => SelectedMenu == "Build";
    public bool IsPatchSelected => SelectedMenu == "Patch";
    public bool IsAnalyserSelected => SelectedMenu == "Analyser";
    public bool IsDeviceToolsSelected => SelectedMenu == "DeviceTools";
    public bool IsSettingsSelected => SelectedMenu == "Settings";
    public bool IsAboutSelected => SelectedMenu == "About";

    public bool IsDecompileEnabled => FeatureFlags.Decompile;
    public bool IsBuildEnabled => FeatureFlags.BuildApk;
    public bool IsPatchEnabled => FeatureFlags.PatchApk;
    public bool IsAnalyserEnabled => FeatureFlags.ApkAnalyser;
    public bool IsDeviceToolsEnabled => FeatureFlags.DeviceTools && _settingsService.Settings.IsDeviceToolsEnabled;
    public bool IsSettingsEnabled => FeatureFlags.Settings;
    public bool IsAboutEnabled => FeatureFlags.About;
    public bool HasPendingApks => _pendingApks.Count > 0;
    public string PendingApksText => $"Abrir siguiente APK ({_pendingApks.Count})";
    public bool HasInterruptedOperations => _operations.Snapshot().Any(x => x.State is "Interrumpida" or "Terminación no confirmada");
    public bool IsQueuePaused => _operations.IsPaused;
    [RelayCommand]
    private void ResumeQueueAfterReview()
    {
        RecoveryNotice = "Reanudaste la cola tras revisar los procesos pendientes.";
        _operations.ResumeAfterReview();
    }
    [ObservableProperty] private string _recoveryNotice = "";

    private bool CanReviewInterrupted() => HasInterruptedOperations && !_operations.HasWork && !_persistentViews.Values.Any(IsTaskActive);

    [RelayCommand(CanExecute = nameof(CanReviewInterrupted))]
    private void ReviewInterrupted()
    {
        if (!CanReviewInterrupted()) return;
        var record = _operations.Snapshot().First(x => x.State is "Interrumpida" or "Terminación no confirmada");
        var request = record.Request;
        try
        {
            switch (request?.Kind)
            {
                case "analysis":
                    NavigateToAnalyser();
                    var analysis = ResolvePersistent<LocalApkViewModel>();
                    analysis.ApkPath = request.InputPath;
                    analysis.IncludeLibraries = request.IncludeLibraries;
                    analysis.Status = "Tarea interrumpida recuperada. Comprueba el archivo y pulsa Iniciar análisis si deseas repetirla.";
                    break;
                case "decompile":
                    NavigateToDecompile();
                    var decompile = ResolvePersistent<DecompileViewModel>();
                    decompile.ApkPath = request.InputPath;
                    decompile.ExtractToApkFolder = request.ExtractToApkFolder;
                    decompile.OutputFolder = request.OutputPath;
                    decompile.DecodeResources = request.DecodeResources;
                    decompile.DecodeSources = request.DecodeSources;
                    decompile.KeepOriginalManifest = request.KeepOriginalManifest;
                    decompile.ConsoleLog = "Tarea interrumpida recuperada. Comprueba las opciones y pulsa Ejecutar para repetirla.";
                    break;
                case "build":
                    NavigateToBuild();
                    var build = ResolvePersistent<BuildViewModel>();
                    build.ProjectPath = request.InputPath;
                    build.OutputFolderPath = Path.GetDirectoryName(request.OutputPath) ?? "";
                    build.OutputApkName = Path.GetFileName(request.OutputPath);
                    build.SignApk = request.SignApk;
                    build.UseAapt2 = request.UseAapt2;
                    build.ConsoleLog = "Tarea interrumpida recuperada. Comprueba las opciones y vuelve a compilar si lo deseas.";
                    break;
                case "patch":
                    NavigateToPatch();
                    var patch = ResolvePersistent<PatchViewModel>();
                    patch.ApkPath = request.InputPath;
                    patch.OutputFolderPath = Path.GetDirectoryName(request.OutputPath) ?? "";
                    patch.OutputApkName = Path.GetFileName(request.OutputPath);
                    patch.SignApk = request.SignApk;
                    patch.DecodeResources = request.DecodeResources;
                    patch.DecodeSources = request.DecodeSources;
                    patch.UseAapt2ForBuild = request.UseAapt2;
                    patch.InjectLibForAllArchitectures = request.InjectForAllArchitectures;
                    patch.SkipDexValidation = request.SkipDexValidation;
                    patch.SelectedScriptInjectionOption = patch.ScriptInjectionOptions.First(x => (int)x.Profile == request.ScriptProfile);
                    patch.SelectedDexPreservationOption = patch.DexPreservationOptions.First(x => (int)x.Mode == request.DexMode);
                    patch.ConsoleLog = "Tarea interrumpida recuperada. Revisa las opciones; las confirmaciones se solicitarán de nuevo.";
                    break;
                default:
                    NavigateToAnalyser();
                    ResolvePersistent<LocalApkViewModel>().Status = "Revisa Cuarentena y el archivo original antes de repetir la operación interrumpida.";
                    break;
            }
            RecoveryNotice = record.State == "Terminación no confirmada"
                ? "No se confirmó que terminaran todos los procesos. Revísalos antes de repetir la tarea."
                : "Revisa la tarea recuperada antes de iniciarla.";
            _operations.MarkReviewed(record.Id);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            RecoveryNotice = "No se pudieron recuperar las opciones: " + ex.Message;
        }
        RefreshOperationStatus();
    }

    public void QueueExternalApk(string path)
    {
        _pendingApks.Enqueue(path);
        OnPropertyChanged(nameof(HasPendingApks));
        OnPropertyChanged(nameof(PendingApksText));
        OpenPendingApkCommand.NotifyCanExecuteChanged();
    }

    private bool CanOpenPendingApk() => HasPendingApks && !_operations.HasWork && !_persistentViews.Values.Any(IsTaskActive);
    [RelayCommand(CanExecute = nameof(CanOpenPendingApk))]
    private void OpenPendingApk()
    {
        if (!CanOpenPendingApk()) return;
        var path = _pendingApks.Dequeue();
        NavigateToAnalyser();
        ResolvePersistent<LocalApkViewModel>().ApkPath = path;
        OnPropertyChanged(nameof(HasPendingApks));
        OnPropertyChanged(nameof(PendingApksText));
        OpenPendingApkCommand.NotifyCanExecuteChanged();
    }
    public bool HasOperationHistory => _operations.PersistenceWarning is not null || _operations.Snapshot().Any(x => x.State is "En curso" or "En espera" or "Cancelando" or "Interrumpida" or "Terminación no confirmada");
    public string OperationQueueText => string.Join("\n\n", _operations.Snapshot()
        .Where(x => x.State is "En curso" or "En espera" or "Cancelando" or "Interrumpida" or "Terminación no confirmada")
        .Select(x => $"{x.Name} · {x.State}" + (x.Started is { } start && x.Finished is null ? $" · {DateTimeOffset.Now - start:hh\\:mm\\:ss}" : "") + $"\n{x.Stage}")
        .Concat(_operations.PersistenceWarning is { } warning ? new[] { warning } : Array.Empty<string>()));

    public bool HasActiveTasks => _persistentViews.Values.Any(IsTaskActive);
    public string ActiveTasksText
    {
        get
        {
            var labels = _persistentViews.Values.Where(IsTaskActive).Select(view => view switch
            {
                LocalApkViewModel => "Análisis o gestión de archivos",
                DecompileViewModel => "Descompilación",
                BuildViewModel => "Compilación o firma",
                PatchViewModel => "Parcheo",
                SettingsViewModel => "Descarga de herramientas",
                _ => "Operación"
            });
            return "En curso: " + string.Join(", ", labels);
        }
    }

    private static bool IsTaskActive(object view) => view switch
    {
        LocalApkViewModel local => local.Busy,
        DecompileViewModel decompile => decompile.IsRunning,
        BuildViewModel build => build.IsRunning,
        PatchViewModel patch => patch.IsRunning,
        SettingsViewModel settings => settings.IsDownloadingTools,
        _ => false
    };

    public MainViewModel(
        IServiceProvider serviceProvider,
        LocalizationService localizationService,
        ISystemService systemService,
        ISettingsService settingsService,
        OperationCoordinator? operations = null)
    {
        _serviceProvider = serviceProvider;
        _localizationService = localizationService;
        _systemService = systemService;
        _settingsService = settingsService;
        _operations = operations ?? new OperationCoordinator(null);
        _dispatcher = serviceProvider.GetService(typeof(IDispatcherService)) as IDispatcherService;
        _operations.Changed += HandleOperationsChanged;
        WindowTitle = "SendaAPK · Análisis local de APK";
        _localizationService.PropertyChanged += HandleLocalizationChanged;
        _settingsService.SettingsChanged += HandleSettingsChanged;
        SetInitialView();
    }

    public void RefreshOperationStatus()
    {
        OnPropertyChanged(nameof(HasOperationHistory));
        OnPropertyChanged(nameof(OperationQueueText));
        OnPropertyChanged(nameof(HasInterruptedOperations));
        OnPropertyChanged(nameof(IsQueuePaused));
        OpenPendingApkCommand.NotifyCanExecuteChanged();
        ReviewInterruptedCommand.NotifyCanExecuteChanged();
    }

    private void HandleOperationsChanged(object? sender, EventArgs e)
    {
        if (_dispatcher is not null && !_dispatcher.CheckAccess()) _ = _dispatcher.InvokeAsync(RefreshOperationStatus);
        else RefreshOperationStatus();
    }

    [RelayCommand(CanExecute = nameof(CanNavigateToDecompile))]
    private void NavigateToDecompile()
    {
        if (!CanNavigateToDecompile()) return;
        SetCurrentView(ResolvePersistent<DecompileViewModel>());
        SelectedMenu = "Decompile";
    }

    [RelayCommand(CanExecute = nameof(CanNavigateToSettings))]
    private void NavigateToSettings()
    {
        if (!CanNavigateToSettings()) return;
        SetCurrentView(ResolvePersistent<SettingsViewModel>());
        SelectedMenu = "Settings";
    }

    [RelayCommand(CanExecute = nameof(CanNavigateToBuild))]
    private void NavigateToBuild()
    {
        if (!CanNavigateToBuild()) return;
        SetCurrentView(ResolvePersistent<BuildViewModel>());
        SelectedMenu = "Build";
    }

    [RelayCommand(CanExecute = nameof(CanNavigateToPatch))]
    private void NavigateToPatch()
    {
        if (!CanNavigateToPatch()) return;
        SetCurrentView(ResolvePersistent<PatchViewModel>());
        SelectedMenu = "Patch";
    }

    [RelayCommand(CanExecute = nameof(CanNavigateToAnalyser))]
    private void NavigateToAnalyser()
    {
        if (!CanNavigateToAnalyser()) return;
        var analyser = ResolvePersistent<LocalApkViewModel>();
        analyser.RefreshQuarantineCommand.Execute(null);
        SetCurrentView(analyser);
        SelectedMenu = "Analyser";
    }

    [RelayCommand(CanExecute = nameof(CanNavigateToDeviceTools))]
    private void NavigateToDeviceTools()
    {
        if (!CanNavigateToDeviceTools()) return;
        var viewModel = Resolve<DeviceToolsViewModel>();
        SetCurrentView(viewModel);
        SelectedMenu = "DeviceTools";
        _ = viewModel.RefreshDevicesAsync();
    }

    [RelayCommand(CanExecute = nameof(CanNavigateToAbout))]
    private void NavigateToAbout()
    {
        if (!CanNavigateToAbout()) return;
        SetCurrentView(Resolve<AboutViewModel>());
        SelectedMenu = "About";
    }

    [RelayCommand]
    private void OpenSupportWork()
    {
        _systemService.OpenUrl(SupportWorkUrl);
    }

    partial void OnSelectedMenuChanged(string value)
    {
        OnPropertyChanged(nameof(IsDecompileSelected));
        OnPropertyChanged(nameof(IsBuildSelected));
        OnPropertyChanged(nameof(IsPatchSelected));
        OnPropertyChanged(nameof(IsAnalyserSelected));
        OnPropertyChanged(nameof(IsDeviceToolsSelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
        OnPropertyChanged(nameof(IsAboutSelected));
    }

    private bool CanNavigateToDecompile() => FeatureFlags.Decompile;
    private bool CanNavigateToBuild() => FeatureFlags.BuildApk;
    private bool CanNavigateToPatch() => FeatureFlags.PatchApk;
    private bool CanNavigateToAnalyser() => FeatureFlags.ApkAnalyser;
    private bool CanNavigateToDeviceTools() => IsDeviceToolsEnabled;
    private bool CanNavigateToSettings() => FeatureFlags.Settings;
    private bool CanNavigateToAbout() => FeatureFlags.About;


    private void HandleSettingsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsDeviceToolsEnabled));
        NavigateToDeviceToolsCommand.NotifyCanExecuteChanged();

        if (IsDeviceToolsSelected && !CanNavigateToDeviceTools())
        {
            NavigateToFirstAvailableEnabledTab();
        }
    }

    private void NavigateToFirstAvailableEnabledTab()
    {
        if (CanNavigateToDecompile())
        {
            NavigateToDecompile();
        }
        else if (CanNavigateToBuild())
        {
            NavigateToBuild();
        }
        else if (CanNavigateToPatch())
        {
            NavigateToPatch();
        }
        else if (CanNavigateToAnalyser())
        {
            NavigateToAnalyser();
        }
        else if (CanNavigateToSettings())
        {
            NavigateToSettings();
        }
        else if (CanNavigateToAbout())
        {
            NavigateToAbout();
        }
        else
        {
            CurrentView = "No features are enabled.";
            SelectedMenu = string.Empty;
        }
    }

    private void SetInitialView()
    {
        if (CanNavigateToAnalyser())
        {
            NavigateToAnalyser();
        }
        else if (CanNavigateToDecompile())
        {
            NavigateToDecompile();
        }
        else if (CanNavigateToBuild())
        {
            NavigateToBuild();
        }
        else if (CanNavigateToPatch())
        {
            NavigateToPatch();
        }
        else if (CanNavigateToAnalyser())
        {
            NavigateToAnalyser();
        }
        else if (CanNavigateToDeviceTools())
        {
            NavigateToDeviceTools();
        }
        else if (CanNavigateToSettings())
        {
            NavigateToSettings();
        }
        else if (CanNavigateToAbout())
        {
            NavigateToAbout();
        }
        else
        {
            CurrentView = "No features are enabled.";
            SelectedMenu = string.Empty;
        }
    }

    private void SetCurrentView(object nextView)
    {
        if (ReferenceEquals(CurrentView, nextView))
        {
            return;
        }

        if (CurrentView is IDisposable disposable && !_persistentViews.Values.Any(view => ReferenceEquals(view, CurrentView)))
        {
            disposable.Dispose();
        }

        CurrentView = nextView;
    }

    private T ResolvePersistent<T>() where T : class, INotifyPropertyChanged
    {
        if (_persistentViews.TryGetValue(typeof(T), out var existing)) return (T)existing;
        var view = Resolve<T>();
        _persistentViews.Add(typeof(T), view);
        view.PropertyChanged += HandleTaskStateChanged;
        return view;
    }

    private void HandleTaskStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LocalApkViewModel.Busy) or nameof(DecompileViewModel.IsRunning) or nameof(SettingsViewModel.IsDownloadingTools) or null or "")
        {
            OnPropertyChanged(nameof(HasActiveTasks));
            OnPropertyChanged(nameof(ActiveTasksText));
            OpenPendingApkCommand.NotifyCanExecuteChanged();
            ReviewInterruptedCommand.NotifyCanExecuteChanged();
        }
    }

    private T Resolve<T>() where T : notnull
    {
        var service = _serviceProvider.GetService(typeof(T));
        if (service == null)
            throw new InvalidOperationException($"Could not resolve service of type {typeof(T).Name}");
        return (T)service;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _localizationService.PropertyChanged -= HandleLocalizationChanged;
        _settingsService.SettingsChanged -= HandleSettingsChanged;
        _operations.Changed -= HandleOperationsChanged;
        foreach (var view in _persistentViews.Values)
        {
            if (view is INotifyPropertyChanged observable) observable.PropertyChanged -= HandleTaskStateChanged;
            if (view is IDisposable disposable) disposable.Dispose();
        }
        _persistentViews.Clear();
        _disposed = true;
    }

    private void HandleLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "Item[]")
        {
            return;
        }

        WindowTitle = _localizationService["AppTitle"];
        OnPropertyChanged(nameof(MenuDecompileLabel));
        OnPropertyChanged(nameof(MenuBuildLabel));
        OnPropertyChanged(nameof(MenuPatchLabel));
        OnPropertyChanged(nameof(MenuAnalyserLabel));
        OnPropertyChanged(nameof(MenuDeviceToolsLabel));
        OnPropertyChanged(nameof(MenuSettingsLabel));
        OnPropertyChanged(nameof(MenuAboutLabel));
    }
}
