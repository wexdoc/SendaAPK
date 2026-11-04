using PulseAPK.Core.Abstractions;
using PulseAPK.Core.Services;
using PulseAPK.Core.ViewModels;

namespace PulseAPK.Tests.ViewModels;

public class MainViewModelNavigationTests
{
    [Fact]
    public void NavigationRetainsRunningViewAndTracksItsState()
    {
        var settings = new TestSettings();
        var provider = new TestProvider(settings);
        using var main = new MainViewModel(provider, LocalizationService.Instance, null!, settings);

        main.NavigateToDecompileCommand.Execute(null);
        var decompile = Assert.IsType<DecompileViewModel>(main.CurrentView);
        decompile.ApkPath = "selected.apk";
        decompile.IsRunning = true;
        Assert.True(main.HasActiveTasks);
        Assert.Contains("Descompilación", main.ActiveTasksText);

        main.NavigateToAnalyserCommand.Execute(null);
        Assert.True(main.HasActiveTasks);
        main.NavigateToDecompileCommand.Execute(null);
        Assert.Same(decompile, main.CurrentView);
        Assert.Equal("selected.apk", decompile.ApkPath);
        Assert.True(decompile.IsRunning);
        Assert.Equal(1, provider.DecompileCreations);

        decompile.IsRunning = false;
        Assert.False(main.HasActiveTasks);
    }

    [Fact]
    public void TracksTasksAcrossSectionsUntilAllHaveStopped()
    {
        var settings = new TestSettings();
        var provider = new TestProvider(settings);
        using var main = new MainViewModel(provider, LocalizationService.Instance, null!, settings);
        var local = Assert.IsType<LocalApkViewModel>(main.CurrentView);
        local.Busy = true;
        main.NavigateToBuildCommand.Execute(null);
        var build = Assert.IsType<BuildViewModel>(main.CurrentView);
        build.IsRunning = true;

        Assert.Contains("Análisis", main.ActiveTasksText);
        Assert.Contains("Compilación", main.ActiveTasksText);
        local.Busy = false;
        Assert.True(main.HasActiveTasks);
        main.NavigateToAnalyserCommand.Execute(null);
        main.NavigateToBuildCommand.Execute(null);
        Assert.Same(build, main.CurrentView);
        build.IsRunning = false;
        Assert.False(main.HasActiveTasks);
    }

    private sealed class TestProvider(TestSettings settings) : IServiceProvider
    {
        public int DecompileCreations { get; private set; }
        public object? GetService(Type type)
        {
            if (type == typeof(LocalApkViewModel))
                return new LocalApkViewModel(null!, new QuarantineService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))), null!, null!, null!);
            if (type == typeof(DecompileViewModel))
            {
                DecompileCreations++;
                return new DecompileViewModel(null!, settings, new ApktoolRunner(settings), null!, null!, null!);
            }
            if (type == typeof(BuildViewModel))
                return new BuildViewModel(null!, settings, new ApktoolRunner(settings), new UbersignRunner(settings), null!, null!);
            return null;
        }
    }

    private sealed class TestSettings : ISettingsService
    {
        public AppSettings Settings { get; } = new();
        public string SettingsDirectory => Path.GetTempPath();
        public event EventHandler? SettingsChanged;
        public void Save() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}
