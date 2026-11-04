// Modified for SendaAPK (2026); original work remains credited in NOTICE.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PulseAPK.Core.Abstractions;
using PulseAPK.Core.Services;
using PulseAPK.Core.ViewModels;
using Xunit;

namespace PulseAPK.Tests.ViewModels;

public class DecompileViewModelTests
{
    [Fact]
    public void RunCommand_IsDisabled_WithoutAnApk()
    {
        var viewModel = CreateViewModel(apktoolPath: string.Empty);

        Assert.False(viewModel.RunDecompileCommand.CanExecute(null));
    }

    [Fact]
    public async Task SelectingOutputParentProposesANewProjectFolder()
    {
        var picker = new TestFilePickerService { SelectedFolder = Path.GetTempPath() };
        using var viewModel = CreateViewModel(string.Empty, filePicker: picker);
        viewModel.ApkPath = Path.Combine(Path.GetTempPath(), "ejemplo con espacios.apk");
        await viewModel.BrowseOutputFolderCommand.ExecuteAsync(null);
        Assert.Equal(Path.Combine(picker.SelectedFolder, "ejemplo con espacios"), viewModel.OutputFolder);
    }

    [Fact]
    public void RunCommand_IsDisabledWhileRunning()
    {
        var viewModel = CreateViewModel(apktoolPath: string.Empty);
        var apkPath = Path.GetTempFileName();
        try
        {
            viewModel.ApkPath = apkPath;
            Assert.True(viewModel.RunDecompileCommand.CanExecute(null));
            viewModel.IsRunning = true;
            Assert.False(viewModel.RunDecompileCommand.CanExecute(null));
            viewModel.IsRunning = false;
            Assert.True(viewModel.RunDecompileCommand.CanExecute(null));
        }
        finally { File.Delete(apkPath); }
    }

    [Fact]
    public async Task RunCommand_RemainsEnabled_AfterValidationFailure()
    {
        var dialogService = new TestDialogService();
        var viewModel = CreateViewModel(apktoolPath: string.Empty, dialogService: dialogService);
        var apkPath = Path.GetTempFileName();
        try
        {
            viewModel.ApkPath = apkPath;
            await viewModel.RunDecompileCommand.ExecuteAsync(null);
            Assert.True(viewModel.RunDecompileCommand.CanExecute(null));
            Assert.Contains(dialogService.Warnings, warning => warning.message.Contains("apktool", StringComparison.OrdinalIgnoreCase));
        }
        finally { File.Delete(apkPath); }
    }

    [Fact]
    public async Task CancelCommand_StopsTheRunningToolAndRestoresTheInterface()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "senda-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tool = Path.Combine(root, "slow-tool.cmd");
        var apk = Path.Combine(root, "sample.apk");
        var started = Path.Combine(root, "started.marker");
        var finished = Path.Combine(root, "finished.marker");
        await File.WriteAllTextAsync(tool, "@echo off\r\necho started > \"%~dp0started.marker\"\r\nping -n 60 127.0.0.1 >nul\r\necho finished > \"%~dp0finished.marker\"\r\n");
        await File.WriteAllTextAsync(apk, "sample");
        using var viewModel = CreateViewModel(tool);
        Task? run = null;
        try
        {
            viewModel.ApkPath = apk;
            viewModel.OutputFolder = Path.Combine(root, "output");
            run = viewModel.RunDecompileCommand.ExecuteAsync(null);
            Assert.True(viewModel.IsRunning);
            Assert.True(viewModel.CancelDecompileCommand.CanExecute(null));
            await WaitForFileAsync(started);
            viewModel.CancelDecompileCommand.Execute(null);
            await run.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(viewModel.IsRunning);
            Assert.False(viewModel.CancelDecompileCommand.CanExecute(null));
            Assert.True(viewModel.RunDecompileCommand.CanExecute(null));
            Assert.False(File.Exists(finished));
        }
        finally
        {
            if (viewModel.IsRunning && run is not null)
            {
                viewModel.CancelDecompileCommand.Execute(null);
                await run.WaitAsync(TimeSpan.FromSeconds(15));
            }
            File.Delete(tool);
            File.Delete(apk);
            File.Delete(started);
            File.Delete(finished);
            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task QueuedDecompileUsesCapturedInputOptionsAndOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "senda cola á " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tool = Path.Combine(root, "tool.cmd");
        var firstApk = Path.Combine(root, "primero con espacios.apk");
        var secondApk = Path.Combine(root, "segundo.apk");
        var firstOutput = Path.Combine(root, "salida uno");
        var secondOutput = Path.Combine(root, "salida dos");
        await File.WriteAllTextAsync(tool, "@echo off\r\nchcp 65001 >nul\r\nmkdir \"%~4\"\r\necho %* > \"%~4\\argumentos.txt\"\r\n");
        await File.WriteAllTextAsync(firstApk, "first");
        await File.WriteAllTextAsync(secondApk, "second");
        var coordinator = new OperationCoordinator(null);
        using var blocker = coordinator.Enqueue("Tarea previa");
        await blocker.WaitAsync();
        using var viewModel = CreateViewModel(tool, operations: coordinator);
        Task? run = null;
        try
        {
            viewModel.ApkPath = firstApk;
            viewModel.OutputFolder = firstOutput;
            viewModel.DecodeResources = false;
            viewModel.DecodeSources = true;
            viewModel.KeepOriginalManifest = true;
            run = viewModel.RunDecompileCommand.ExecuteAsync(null);
            Assert.Contains(coordinator.Snapshot(), x => x.Name.StartsWith("Descompilación · ", StringComparison.Ordinal) && x.State == "En espera");

            viewModel.ApkPath = secondApk;
            viewModel.OutputFolder = secondOutput;
            viewModel.DecodeResources = true;
            viewModel.DecodeSources = false;
            viewModel.KeepOriginalManifest = false;
            blocker.Complete();
            blocker.Dispose();
            await run.WaitAsync(TimeSpan.FromSeconds(15));

            var arguments = await File.ReadAllTextAsync(Path.Combine(firstOutput, "argumentos.txt"));
            Assert.Contains(firstApk, arguments, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secondApk, arguments, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"-r\"", arguments, StringComparison.Ordinal);
            Assert.Contains("\"-m\"", arguments, StringComparison.Ordinal);
            Assert.DoesNotContain("\"-s\"", arguments, StringComparison.Ordinal);
            Assert.False(Directory.Exists(secondOutput));
            Assert.Equal("first", await File.ReadAllTextAsync(firstApk));
        }
        finally
        {
            if (viewModel.IsRunning && run is not null)
            {
                viewModel.CancelDecompileCommand.Execute(null);
                await run.WaitAsync(TimeSpan.FromSeconds(15));
            }
            File.Delete(Path.Combine(firstOutput, "argumentos.txt"));
            if (Directory.Exists(firstOutput)) Directory.Delete(firstOutput);
            File.Delete(tool);
            File.Delete(firstApk);
            File.Delete(secondApk);
            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task OutputCreatedWhileQueuedIsNotOverwritten()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "senda-colision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tool = Path.Combine(root, "tool.cmd");
        var apk = Path.Combine(root, "sample.apk");
        var output = Path.Combine(root, "resultado");
        var sentinel = Path.Combine(output, "original.txt");
        var marker = Path.Combine(root, "tool-started.marker");
        await File.WriteAllTextAsync(tool, "@echo off\r\necho started > \"%~dp0tool-started.marker\"\r\n");
        await File.WriteAllTextAsync(apk, "original");
        var coordinator = new OperationCoordinator(null);
        using var blocker = coordinator.Enqueue("Tarea previa");
        await blocker.WaitAsync();
        var dialogs = new TestDialogService();
        using var viewModel = CreateViewModel(tool, dialogs, operations: coordinator);
        Task? run = null;
        try
        {
            viewModel.ApkPath = apk;
            viewModel.OutputFolder = output;
            run = viewModel.RunDecompileCommand.ExecuteAsync(null);
            Assert.Contains(coordinator.Snapshot(), x => x.Name.StartsWith("Descompilación · ", StringComparison.Ordinal) && x.State == "En espera");
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(sentinel, "conservar");
            blocker.Complete();
            blocker.Dispose();
            await run.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal("conservar", await File.ReadAllTextAsync(sentinel));
            Assert.Equal("original", await File.ReadAllTextAsync(apk));
            Assert.False(File.Exists(marker));
            Assert.Contains(dialogs.Errors, e => e.message.Contains("ya existe", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (viewModel.IsRunning && run is not null)
            {
                viewModel.CancelDecompileCommand.Execute(null);
                await run.WaitAsync(TimeSpan.FromSeconds(15));
            }
            File.Delete(sentinel);
            if (Directory.Exists(output)) Directory.Delete(output);
            File.Delete(marker);
            File.Delete(tool);
            File.Delete(apk);
            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task CancellingQueuedDecompileNeverStartsTheTool()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "senda-cola-cancelada-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tool = Path.Combine(root, "tool.cmd");
        var apk = Path.Combine(root, "sample.apk");
        var output = Path.Combine(root, "resultado");
        var marker = Path.Combine(root, "tool-started.marker");
        await File.WriteAllTextAsync(tool, "@echo off\r\necho started > \"%~dp0tool-started.marker\"\r\n");
        await File.WriteAllTextAsync(apk, "original");
        var coordinator = new OperationCoordinator(null);
        using var blocker = coordinator.Enqueue("Tarea previa");
        await blocker.WaitAsync();
        using var viewModel = CreateViewModel(tool, operations: coordinator);
        Task? run = null;
        try
        {
            viewModel.ApkPath = apk;
            viewModel.OutputFolder = output;
            run = viewModel.RunDecompileCommand.ExecuteAsync(null);
            Assert.Contains(coordinator.Snapshot(), x => x.Name.StartsWith("Descompilación · ", StringComparison.Ordinal) && x.State == "En espera");
            Assert.True(viewModel.CancelDecompileCommand.CanExecute(null));
            viewModel.CancelDecompileCommand.Execute(null);
            await run.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.False(viewModel.IsRunning);
            Assert.True(viewModel.RunDecompileCommand.CanExecute(null));
            Assert.False(File.Exists(marker));
            Assert.False(Directory.Exists(output));
            Assert.Equal("original", await File.ReadAllTextAsync(apk));
            Assert.Contains(coordinator.Snapshot(), x => x.Name.StartsWith("Descompilación · ", StringComparison.Ordinal) && x.State == "Cancelada");
        }
        finally
        {
            if (viewModel.IsRunning && run is not null)
            {
                viewModel.CancelDecompileCommand.Execute(null);
                await run.WaitAsync(TimeSpan.FromSeconds(15));
            }
            File.Delete(marker);
            File.Delete(tool);
            File.Delete(apk);
            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task OutputCreatedDuringDecompilationPreservesBothExistingAndGeneratedFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "senda-publicacion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tool = Path.Combine(root, "tool.cmd");
        var apk = Path.Combine(root, "sample.apk");
        var output = Path.Combine(root, "resultado");
        var sentinel = Path.Combine(output, "original.txt");
        var started = Path.Combine(root, "started.marker");
        var release = Path.Combine(root, "release.marker");
        var script = "@echo off\r\nmkdir \"%~4\"\r\necho generado > \"%~4\\generated.txt\"\r\necho started > \"%~dp0started.marker\"\r\n" +
            ":again\r\nif exist \"%~dp0release.marker\" goto done\r\nping -n 2 127.0.0.1 >nul\r\ngoto again\r\n:done\r\nexit /b 0\r\n";
        await File.WriteAllTextAsync(tool, script);
        await File.WriteAllTextAsync(apk, "original");
        var dialogs = new TestDialogService();
        using var viewModel = CreateViewModel(tool, dialogs);
        Task? run = null;
        try
        {
            viewModel.ApkPath = apk;
            viewModel.OutputFolder = output;
            run = viewModel.RunDecompileCommand.ExecuteAsync(null);
            await WaitForFileAsync(started);

            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(sentinel, "conservar");
            await File.WriteAllTextAsync(release, "continuar");
            await run.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal("conservar", await File.ReadAllTextAsync(sentinel));
            Assert.Equal("original", await File.ReadAllTextAsync(apk));
            var temporary = Assert.Single(Directory.GetDirectories(root, ".sendaapk-*", SearchOption.TopDirectoryOnly));
            Assert.Contains("generado", await File.ReadAllTextAsync(Path.Combine(temporary, "generated.txt")));
            Assert.Contains(dialogs.Errors, e => e.message.Contains("conserv", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (viewModel.IsRunning && run is not null)
            {
                viewModel.CancelDecompileCommand.Execute(null);
                await run.WaitAsync(TimeSpan.FromSeconds(15));
            }
            foreach (var temporary in Directory.GetDirectories(root, ".sendaapk-*", SearchOption.TopDirectoryOnly))
            {
                File.Delete(Path.Combine(temporary, "generated.txt"));
                Directory.Delete(temporary);
            }
            File.Delete(sentinel);
            if (Directory.Exists(output)) Directory.Delete(output);
            File.Delete(started);
            File.Delete(release);
            File.Delete(tool);
            File.Delete(apk);
            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task RunCommand_DoesNotOverwriteFilesInExistingOutputDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "senda-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tool = Path.Combine(root, "tool.jar");
        var apk = Path.Combine(root, "sample.apk");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(output);
        var original = Path.Combine(output, "original.txt");
        await File.WriteAllTextAsync(tool, "unused");
        await File.WriteAllTextAsync(apk, "sample");
        await File.WriteAllTextAsync(original, "conservar");
        var dialogs = new TestDialogService();
        using var viewModel = CreateViewModel(tool, dialogs);
        try
        {
            viewModel.ApkPath = apk;
            viewModel.OutputFolder = output;
            await viewModel.RunDecompileCommand.ExecuteAsync(null);
            Assert.False(viewModel.IsRunning);
            Assert.Contains(dialogs.Errors, error => error.title == "Carpeta de salida ocupada");
            Assert.Equal("conservar", await File.ReadAllTextAsync(original));
        }
        finally
        {
            File.Delete(tool);
            File.Delete(apk);
            File.Delete(original);
            Directory.Delete(output);
            Directory.Delete(root);
        }
    }


    [Fact]
    public async Task RunCommand_ShowsError_WhenOutputFolderIsInvalid()
    {
        var apktoolPath = Path.GetTempFileName();
        var apkPath = Path.GetTempFileName();

        try
        {
            var dialogService = new TestDialogService();
            var viewModel = CreateViewModel(apktoolPath: apktoolPath, dialogService: dialogService);
            viewModel.ApkPath = apkPath;
            viewModel.OutputFolder = "invalid\0folder";

            await viewModel.RunDecompileCommand.ExecuteAsync(null);

            Assert.True(viewModel.RunDecompileCommand.CanExecute(null));
            Assert.Contains(dialogService.Errors, error => error.title == "Invalid output folder");
        }
        finally
        {
            File.Delete(apktoolPath);
            File.Delete(apkPath);
        }
    }

    private static async Task WaitForFileAsync(string path)
    {
        for (var i = 0; i < 200; i++)
        {
            if (File.Exists(path)) return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"La herramienta de prueba no creó {path}.");
    }

    private static DecompileViewModel CreateViewModel(string apktoolPath, TestDialogService? dialogService = null, TestFilePickerService? filePicker = null, OperationCoordinator? operations = null)
    {
        return new DecompileViewModel(
            filePicker ?? new TestFilePickerService(),
            new TestSettingsService(apktoolPath),
            new ApktoolRunner(new TestSettingsService(apktoolPath)),
            dialogService ?? new TestDialogService(),
            new ImmediateDispatcherService(),
            new TestSystemService(),
            operations: operations);
    }

    private sealed class TestFilePickerService : IFilePickerService
    {
        public string? SelectedFolder { get; init; }
        public Task<string?> OpenFileAsync(string filter) => Task.FromResult<string?>(null);
        public Task<string?> OpenFolderAsync(string? initialDirectory = null) => Task.FromResult(SelectedFolder);
    }

    private sealed class TestSettingsService : ISettingsService
    {
        public TestSettingsService(string apktoolPath)
        {
            Settings = new AppSettings { ApktoolPath = apktoolPath };
        }

        public AppSettings Settings { get; }
        public string SettingsDirectory => Environment.CurrentDirectory;
        public event EventHandler? SettingsChanged;
        public void Save() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class TestDialogService : IDialogService
    {
        public List<(string message, string? title)> Warnings { get; } = new();
        public List<(string message, string? title)> Errors { get; } = new();

        public Task ShowInfoAsync(string message, string? title = null) => Task.CompletedTask;

        public Task ShowWarningAsync(string message, string? title = null)
        {
            Warnings.Add((message, title));
            return Task.CompletedTask;
        }

        public Task ShowErrorAsync(string message, string? title = null)
        {
            Errors.Add((message, title));
            return Task.CompletedTask;
        }

        public Task<bool> ShowQuestionAsync(string message, string? title = null) => Task.FromResult(false);
    }

    private sealed class ImmediateDispatcherService : IDispatcherService
    {
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());

        public bool CheckAccess() => true;
    }

    private sealed class TestSystemService : ISystemService
    {
        public void OpenFolder(string folderPath) { }
        public void OpenUrl(string url) { }
    }
}
