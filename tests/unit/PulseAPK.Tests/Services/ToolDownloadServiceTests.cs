// Modified for SendaAPK (2026); original work remains credited in NOTICE.
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PulseAPK.Core.Abstractions;
using PulseAPK.Core.Services;
using PulseAPK.Core.ViewModels;
using Xunit;

namespace PulseAPK.Tests.Services;

public class ToolDownloadServiceTests
{
    [Fact]
    public async Task CancelQueuedToolDownloadDoesNotStartOrChangeSettings()
    {
        var coordinator = new OperationCoordinator(null);
        using var blocker = coordinator.Enqueue("Análisis");
        await blocker.WaitAsync();
        var settings = new TestSettings();
        var download = new BlockingToolDownload();
        using var vm = CreateSettingsViewModel(settings, download, coordinator);
        var originalPath = vm.UbersignPath;

        var pending = vm.DownloadUbersignerCommand.ExecuteAsync(null);
        Assert.True(vm.IsDownloadingTools);
        Assert.False(vm.CanModifyToolPaths);
        Assert.False(download.Started.Task.IsCompleted);
        Assert.True(vm.CancelDownloadCommand.CanExecute(null));
        vm.CancelDownloadCommand.Execute(null);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(download.Started.Task.IsCompleted);
        Assert.False(vm.IsDownloadingTools);
        Assert.Equal(originalPath, vm.UbersignPath);
        blocker.Complete();
    }

    [Fact]
    public async Task CancelActiveToolDownloadPropagatesCancellationAndReleasesQueue()
    {
        var coordinator = new OperationCoordinator(null);
        var settings = new TestSettings();
        var download = new BlockingToolDownload();
        using var vm = CreateSettingsViewModel(settings, download, coordinator);
        var originalPath = vm.UbersignPath;
        var running = vm.DownloadUbersignerCommand.ExecuteAsync(null);
        await download.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.CancelDownloadCommand.CanExecute(null));
        vm.CancelDownloadCommand.Execute(null);
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(download.WasCancelled);
        Assert.False(coordinator.HasWork);
        Assert.False(vm.IsDownloadingTools);
        Assert.Equal(originalPath, vm.UbersignPath);
    }

    private static SettingsViewModel CreateSettingsViewModel(TestSettings settings, IToolDownloadService download, OperationCoordinator coordinator)
        => new(settings, new TestPicker(), new TestDialogs(), new TestToolRepository(Path.GetTempPath()), download,
            new AdbService(settings), LocalizationService.Instance, new TestTheme(), coordinator);

    [Fact]
    public async Task DownloadUbersigner_VerifiesBeforePublishing()
    {
        var root = Path.Combine(Path.GetTempPath(), "senda-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var artifact = Path.Combine(root, "ubersigner.jar");
            var payload = Encoding.UTF8.GetBytes("signed test data");
            var checksum = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            using var http = new HttpClient(new StubHttpMessageHandler(request =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                if (url.EndsWith("/releases/latest", StringComparison.Ordinal)) return ReleaseResponse();
                if (url.EndsWith("/tool.jar", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
                if (url.EndsWith("/tool.sha256", StringComparison.Ordinal))
                {
                    Assert.False(File.Exists(artifact));
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(checksum + " *uber-apk-signer-1.jar") };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            var result = await new ToolDownloadService(http, new TestToolRepository(root)).DownloadUbersignerAsync();
            Assert.True(result.Downloaded);
            Assert.Equal(payload, await File.ReadAllBytesAsync(result.Path));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { DeleteFlatTestFolder(root); }
    }

    [Fact]
    public async Task DownloadUbersigner_BadChecksumPreservesExistingFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "senda-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var artifact = Path.Combine(root, "ubersigner.jar");
            await File.WriteAllTextAsync(artifact, "existing tool");
            using var http = new HttpClient(new StubHttpMessageHandler(request =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                if (url.EndsWith("/releases/latest", StringComparison.Ordinal)) return ReleaseResponse();
                if (url.EndsWith("/tool.jar", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("untrusted data") };
                if (url.EndsWith("/tool.sha256", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('0', 64) + " *uber-apk-signer-1.jar") };
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            var service = new ToolDownloadService(http, new TestToolRepository(root, forceCacheMiss: true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadUbersignerAsync());
            Assert.Equal("existing tool", await File.ReadAllTextAsync(artifact));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { DeleteFlatTestFolder(root); }
    }

    [Fact]
    public async Task DownloadUbersigner_CancelDuringVerificationLeavesNoPublishedFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "senda-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checksumRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        try
        {
            using var http = new HttpClient(new AsyncStubHttpMessageHandler(async (request, token) =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                if (url.EndsWith("/releases/latest", StringComparison.Ordinal)) return ReleaseResponse();
                if (url.EndsWith("/tool.jar", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("partial candidate") };
                checksumRequested.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("unreachable");
            }));
            var service = new ToolDownloadService(http, new TestToolRepository(root));
            var run = service.DownloadUbersignerAsync(cancellation.Token);
            await checksumRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(File.Exists(Path.Combine(root, "ubersigner.jar")));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { DeleteFlatTestFolder(root); }
    }

    private static HttpResponseMessage ReleaseResponse()
    {
        var release = new { assets = new[]
        {
            new { name = "uber-apk-signer-1.jar", browser_download_url = "https://example.test/tool.jar" },
            new { name = "sha256sums.txt", browser_download_url = "https://example.test/tool.sha256" }
        }};
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(release), Encoding.UTF8, "application/json") };
    }

    private static void DeleteFlatTestFolder(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.GetFiles(root)) File.Delete(path);
        Directory.Delete(root);
    }

    [Fact]
    public async Task DownloadApktoolAsync_DownloadsVersionedJarAssetName()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"pulseapk-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            const string versionedAssetName = "apktool_2.11.1.jar";
            const string downloadUrl = "https://example.test/assets/apktool_2.11.1.jar";
            var payload = Encoding.UTF8.GetBytes("fake-apktool-binary");

            using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
            {
                if (request.RequestUri?.AbsoluteUri == "https://api.github.com/repos/iBotPeaches/Apktool/releases/latest")
                {
                    var release = new
                    {
                        assets = new[]
                        {
                            new { name = "notes.txt", browser_download_url = "https://example.test/assets/notes.txt" },
                            new { name = versionedAssetName, browser_download_url = downloadUrl }
                        }
                    };

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(release), Encoding.UTF8, "application/json")
                    };
                }

                if (request.RequestUri?.AbsoluteUri == downloadUrl)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(payload)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));

            var toolRepository = new TestToolRepository(tempRoot);
            var service = new ToolDownloadService(httpClient, toolRepository);

            var result = await service.DownloadApktoolAsync();

            Assert.True(result.Downloaded);
            Assert.Equal(toolRepository.GetToolPath("apktool.jar"), result.Path);
            Assert.True(File.Exists(result.Path));
            Assert.Equal(payload, await File.ReadAllBytesAsync(result.Path));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class TestToolRepository : IToolRepository
    {
        private readonly bool _forceCacheMiss;
        public TestToolRepository(string toolsDirectory, bool forceCacheMiss = false)
        {
            ToolsDirectory = toolsDirectory;
            _forceCacheMiss = forceCacheMiss;
        }

        public string ToolsDirectory { get; }

        public string GetToolPath(string fileName)
        {
            return Path.Combine(ToolsDirectory, fileName);
        }

        public bool TryGetCachedToolPath(string fileName, out string path)
        {
            path = GetToolPath(fileName);
            return !_forceCacheMiss && File.Exists(path);
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class AsyncStubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }

    private sealed class TestSettings : ISettingsService
    {
        public AppSettings Settings { get; } = new() { ApktoolPath = "configured-apktool.jar", UbersignPath = "configured-ubersigner.jar", AdbPath = "configured-adb" };
        public string SettingsDirectory => Path.GetTempPath();
        public event EventHandler? SettingsChanged;
        public void Save() => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class TestPicker : IFilePickerService
    {
        public Task<string?> OpenFileAsync(string filter) => Task.FromResult<string?>(null);
        public Task<string?> OpenFolderAsync(string? initialDirectory = null) => Task.FromResult<string?>(null);
    }

    private sealed class TestDialogs : IDialogService
    {
        public Task ShowInfoAsync(string message, string? title = null) => Task.CompletedTask;
        public Task ShowWarningAsync(string message, string? title = null) => Task.CompletedTask;
        public Task ShowErrorAsync(string message, string? title = null) => Task.CompletedTask;
        public Task<bool> ShowQuestionAsync(string message, string? title = null) => Task.FromResult(false);
    }

    private sealed class TestTheme : IThemeService { public void ApplyTheme(string? themeMode) { } }

    private sealed class BlockingToolDownload : IToolDownloadService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasCancelled { get; private set; }
        public Task<ToolDownloadResult> DownloadApktoolAsync(CancellationToken cancellationToken = default) => DownloadUbersignerAsync(cancellationToken);
        public async Task<ToolDownloadResult> DownloadUbersignerAsync(CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { WasCancelled = true; throw; }
            throw new InvalidOperationException("unreachable");
        }
    }
}
