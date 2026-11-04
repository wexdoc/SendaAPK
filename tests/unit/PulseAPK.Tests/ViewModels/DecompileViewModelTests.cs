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
        await File.WriteAllTextAsync(tool, "@echo off\r\nping -n 60 127.0.0.1 >nul\r\n");
        await File.WriteAllTextAsync(apk, "sample");
        using var viewModel = CreateViewModel(tool);
        try
        {
            viewModel.ApkPath = apk;
            viewModel.OutputFolder = Path.Combine(root, "output");
            var run = viewModel.RunDecompileCommand.ExecuteAsync(null);
            Assert.True(viewModel.IsRunning);
            Assert.True(viewModel.CancelDecompileCommand.CanExecute(null));
            viewModel.CancelDecompileCommand.Execute(null);
            await run.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(viewModel.IsRunning);
            Assert.False(viewModel.CancelDecompileCommand.CanExecute(null));
            Assert.True(viewModel.RunDecompileCommand.CanExecute(null));
        }
        finally
        {
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

    private static DecompileViewModel CreateViewModel(string apktoolPath, TestDialogService? dialogService = null, TestFilePickerService? filePicker = null)
    {
        return new DecompileViewModel(
            filePicker ?? new TestFilePickerService(),
            new TestSettingsService(apktoolPath),
            new ApktoolRunner(new TestSettingsService(apktoolPath)),
            dialogService ?? new TestDialogService(),
            new ImmediateDispatcherService(),
            new TestSystemService());
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
