using System.IO.Compression;
using PulseAPK.Core.Services;
using Xunit;

namespace PulseAPK.Local.Tests;

public sealed class LocalWorkflowTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PulseAPK-local-tests", Guid.NewGuid().ToString("N"));
    public LocalWorkflowTests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private string Apk(string name = "aplicación con espacios.apk", string entry = "AndroidManifest.xml")
    {
        var path = Path.Combine(root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
        writer.Write("fixture; not executable");
        return path;
    }
    [Fact] public void AcceptsApkWithSpacesAndAccents() => ApkInputService.Validate(Apk());
    [Fact] public void RejectsWrongExtension() => Assert.Throws<InvalidDataException>(() => ApkInputService.Validate(Apk("sample.zip")));
    [Fact] public void RejectsMissingManifest() => Assert.Throws<InvalidDataException>(() => ApkInputService.Validate(Apk(entry: "other")));
    [Fact] public void RejectsCorruptArchive()
    {
        var path = Path.Combine(root, "broken.apk"); File.WriteAllText(path, "not a ZIP");
        Assert.Throws<InvalidDataException>(() => ApkInputService.Validate(path));
    }
    [Theory] [InlineData("../escape")] [InlineData("/absolute")] [InlineData("C:/file")]
    public void RejectsUnsafeExtractionPaths(string entry)
    {
        var path = Apk(); using (var archive = ZipFile.Open(path, ZipArchiveMode.Update)) archive.CreateEntry(entry);
        Assert.Throws<InvalidDataException>(() => ApkInputService.Validate(path));
    }
    [Theory]
    [InlineData(0, "No threats", AntivirusState.NoDetections)]
    [InlineData(2, "Threat : Test:Fixture", AntivirusState.Detected)]
    [InlineData(2, "error", AntivirusState.ReviewRequired)]
    [InlineData(5, "access denied", AntivirusState.Error)]
    public void DefenderDoesNotConfuseErrorsWithDetections(int exit, string output, AntivirusState expected)
        => Assert.Equal(expected, DefenderScanService.Interpret(new(exit, output), "version", "signatures").State);

    [Fact] public async Task QuarantineRoundTripPreservesBytesAndMetadata()
    {
        var file = Apk(); var hash = await ApkInputService.HashAsync(file);
        var service = new QuarantineService(Path.Combine(root, "quarantine"));
        var entry = await service.QuarantineAsync(file, hash);
        Assert.False(File.Exists(file)); Assert.Single(service.List());
        Assert.Equal(hash, entry.Sha256); Assert.Equal(file, entry.OriginalPath);
        await service.RestoreAsync(entry);
        Assert.True(File.Exists(file)); Assert.Equal(hash, await ApkInputService.HashAsync(file)); Assert.Empty(service.List());
    }
    [Fact] public async Task QuarantineWrongHashPreservesOriginal()
    {
        var file = Apk(); var hash = await ApkInputService.HashAsync(file);
        var service = new QuarantineService(Path.Combine(root, "quarantine"));
        await Assert.ThrowsAsync<IOException>(() => service.QuarantineAsync(file, new string('0', 64)));
        Assert.Equal(hash, await ApkInputService.HashAsync(file));
    }
    [Fact] public async Task RestoreDoesNotOverwriteCollision()
    {
        var file = Apk(); var service = new QuarantineService(Path.Combine(root, "quarantine"));
        var entry = await service.QuarantineAsync(file, await ApkInputService.HashAsync(file));
        await File.WriteAllTextAsync(file, "existing");
        await Assert.ThrowsAsync<IOException>(() => service.RestoreAsync(entry));
        Assert.Equal("existing", await File.ReadAllTextAsync(file)); Assert.Single(service.List());
    }
    [Fact] public async Task RestoreRejectsModifiedQuarantine()
    {
        var file = Apk(); var directory = Path.Combine(root, "quarantine"); var service = new QuarantineService(directory);
        var entry = await service.QuarantineAsync(file, await ApkInputService.HashAsync(file));
        await File.AppendAllTextAsync(Path.Combine(directory, entry.Id + ".quarantine"), "changed");
        await Assert.ThrowsAsync<IOException>(() => service.RestoreAsync(entry)); Assert.False(File.Exists(file));
    }
    [Fact] public async Task CopyFailurePreservesOriginal()
    {
        var file = Apk(); var hash = await ApkInputService.HashAsync(file); var directory = Path.Combine(root, "blocked");
        File.WriteAllText(directory, "not a directory");
        await Assert.ThrowsAsync<IOException>(() => new QuarantineService(directory).QuarantineAsync(file, hash));
        Assert.Equal(hash, await ApkInputService.HashAsync(file));
    }
    [Fact] public async Task LockedOriginalIsNotRemoved()
    {
        var file = Apk(); var hash = await ApkInputService.HashAsync(file);
        using var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => new QuarantineService(Path.Combine(root, "quarantine")).QuarantineAsync(file, hash));
        Assert.True(File.Exists(file));
    }
    [Fact] public async Task ConcurrentQuarantineAttemptsNeverRemoveUnverifiedFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Apk();
        var hash = await ApkInputService.HashAsync(file);
        var directory = Path.Combine(root, "quarantine");
        async Task<bool> TryMove()
        {
            try { await new QuarantineService(directory).QuarantineAsync(file, hash); return true; }
            catch (IOException) { return false; }
        }
        var outcomes = await Task.WhenAll(TryMove(), TryMove());
        Assert.Equal(1, outcomes.Count(success => success));
        Assert.False(File.Exists(file));
        var entry = Assert.Single(new QuarantineService(directory).List());
        Assert.Equal(hash, await ApkInputService.HashAsync(Path.Combine(directory, entry.Id + ".quarantine")));
    }
    [Fact] public async Task InvalidMetadataDoesNotHideValidQuarantineEntries()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Apk();
        var directory = Path.Combine(root, "quarantine");
        var service = new QuarantineService(directory);
        var entry = await service.QuarantineAsync(file, await ApkInputService.HashAsync(file));
        await File.WriteAllTextAsync(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), "{\"Sha256\":null}");
        Assert.Equal(entry.Id, Assert.Single(service.List()).Id);
    }
    [Fact] public async Task CancelledCodeAnalysisStops()
    {
        File.WriteAllText(Path.Combine(root, "test.smali"), "const-string v0, \"http://test.invalid\"");
        using var token = new CancellationTokenSource(); token.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new SmaliAnalyserService().AnalyseProjectAsync(root, null, token.Token));
    }
    [Fact] public async Task ProcessCancellationDoesNotWaitForSleep()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LocalProcessRunner().RunAsync(shell, new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" }, cancel.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }
    [Fact] public async Task MissingJavaIsReportedByProcessRunner()
    {
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => new LocalProcessRunner().RunAsync(Path.Combine(root, "missing-java.exe"), new[] { "-version" }, CancellationToken.None));
    }
    private sealed class PrefProcess(string json) : ILocalProcessRunner
    {
        public int Calls { get; private set; }
        public Task<LocalProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token)
        {
            Calls++; return Task.FromResult(new LocalProcessResult(0, json));
        }
    }
    [Theory]
    [InlineData("{\"Enabled\":true,\"Maps\":2,\"Version\":\"test\",\"Signatures\":\"1\"}", "modo privado")]
    [InlineData("{\"Enabled\":false,\"Maps\":0,\"Version\":\"test\",\"Signatures\":\"1\"}", "no está activo")]
    public async Task DefenderIsNotRunWhenPrivacyOrAvailabilityPreflightFails(string json, string detail)
    {
        if (!OperatingSystem.IsWindows()) return;
        var processes = new PrefProcess(json);
        var result = await new DefenderScanService(processes).ScanAsync("fixture.apk", CancellationToken.None);
        Assert.Equal(AntivirusState.Unavailable, result.State); Assert.Contains(detail, result.Detail); Assert.Equal(1, processes.Calls);
    }
    [Fact] public async Task ReportsRetainPartialStageStatusAndEvidence()
    {
        var report = new LocalAnalysisReport { ApkPath = "sample.apk", Sha256 = new string('A', 64), Finished = DateTimeOffset.Now };
        report.Stages.Add(new() { Name = "Código", State = StageState.Failed, Detail = "parcial" });
        report.Findings.Add(new("Red", "No confirma malware", "sample.smali", 42, "http://test.invalid"));
        await report.SaveAsync(root);
        var json = await File.ReadAllTextAsync(Path.Combine(root, report.Id + ".json"));
        Assert.Contains("Failed", json); Assert.Contains("sample.smali", json);
        Assert.Contains("42", report.ToText()); Assert.Contains("LÍMITES", report.ToText());
    }
    private sealed class FixedDefender : IDefenderScanService
    {
        public Task<DefenderResult> ScanAsync(string path, CancellationToken token) => Task.FromResult(new DefenderResult(AntivirusState.Unavailable, "fixture"));
    }
    private sealed class WaitingDefender : IDefenderScanService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DefenderResult> ScanAsync(string path, CancellationToken token)
        {
            Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token);
            return new(AntivirusState.NotRun, "unreachable");
        }
    }
    [Fact] public async Task CancellingCoordinatorReleasesGateAndRejectsConcurrentRuns()
    {
        var settings = new FakeSettings(root); var defender = new WaitingDefender();
        var service = new LocalAnalysisService(new(settings), new(), defender, new LocalProcessRunner(), settings);
        var file = Apk(); using var cancel = new CancellationTokenSource();
        var active = service.AnalyseAsync(file, _ => { }, cancel.Token);
        await defender.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AnalyseAsync(file, _ => { }));
        cancel.Cancel(); var result = await active;
        Assert.Equal(StageState.Cancelled, result.Stages[1].State);
        Assert.NotNull(result.Finished);
        var retry = await service.AnalyseAsync(Path.Combine(root, "missing.apk"), _ => { });
        Assert.Equal(StageState.Failed, retry.Stages[0].State);
    }
    [Fact] public async Task MissingApktoolProducesPartialReportAndAllowsRetry()
    {
        var settings = new FakeSettings(root);
        var service = new LocalAnalysisService(new(settings), new(), new FixedDefender(), new LocalProcessRunner(), settings);
        foreach (var attempt in Enumerable.Range(0, 2))
        {
            var result = await service.AnalyseAsync(attempt == 0 ? Apk() : Path.Combine(root, "aplicación con espacios.apk"), _ => { });
            Assert.Equal(StageState.Completed, result.Stages[0].State);
            Assert.Equal(StageState.Failed, result.Stages[2].State);
            Assert.Equal(StageState.Unavailable, result.Stages[3].State);
        }
    }
    private sealed class FakeSettings(string directory) : ISettingsService
    {
        public AppSettings Settings { get; } = new() { ApktoolPath = Path.Combine(directory, "missing.jar") };
        public string SettingsDirectory => directory;
        public event EventHandler? SettingsChanged { add { } remove { } }
        public void Save() { }
    }
    private sealed class JavaProcess(int exit) : ILocalProcessRunner
    {
        public Task<LocalProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token)
            => Task.FromResult(new LocalProcessResult(exit, exit == 0 ? "Java fixture" : "Java no disponible (fixture)"));
    }
    [Fact] public async Task JavaFailureLeavesCodeStageUnavailable()
    {
        var settings = new FakeSettings(root); var tool = Path.Combine(root, "tool.jar"); File.WriteAllText(tool, "fixture"); settings.Settings.ApktoolPath = tool;
        var service = new LocalAnalysisService(new(settings), new(), new FixedDefender(), new JavaProcess(1), settings);
        var report = await service.AnalyseAsync(Apk(), _ => { });
        Assert.Equal(StageState.Failed, report.Stages[2].State); Assert.Contains("Java", report.Stages[2].Detail);
        Assert.Equal(StageState.Unavailable, report.Stages[3].State);
    }
    [Fact] public async Task NonzeroApktoolExitIsNotPresentedAsCompleted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var settings = new FakeSettings(root); var tool = Path.Combine(root, "failed.cmd");
        File.WriteAllText(tool, "@echo off\r\nexit /b 9\r\n"); settings.Settings.ApktoolPath = tool;
        var service = new LocalAnalysisService(new(settings), new(), new FixedDefender(), new JavaProcess(0), settings);
        var report = await service.AnalyseAsync(Apk(), _ => { });
        Assert.Equal(StageState.Failed, report.Stages[2].State); Assert.Contains("9", report.Stages[2].Detail);
    }
    [Fact] public async Task CancellationStopsApktoolProcessTree()
    {
        if (!OperatingSystem.IsWindows()) return;
        var settings = new FakeSettings(root); var tool = Path.Combine(root, "slow.cmd");
        File.WriteAllText(tool, "@echo off\r\nping -n 31 127.0.0.1 >nul\r\n"); settings.Settings.ApktoolPath = tool;
        var service = new LocalAnalysisService(new(settings), new(), new FixedDefender(), new JavaProcess(0), settings);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(1000));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var report = await service.AnalyseAsync(Apk(), _ => { }, cancel.Token);
        Assert.Equal(StageState.Cancelled, report.Stages[2].State);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }
    [Fact] public async Task RealApkIntegrationWhenExplicitlyConfigured()
    {
        var path = Environment.GetEnvironmentVariable("PULSEAPK_INTEGRATION_APK");
        if (string.IsNullOrWhiteSpace(path)) return;
        var processes = new LocalProcessRunner(); var settings = new SettingsService();
        var service = new LocalAnalysisService(new(settings), new(), new DefenderScanService(processes), processes, settings);
        var report = await service.AnalyseAsync(path, Console.WriteLine);
        await report.SaveAsync(LocalAnalysisService.ReportsRoot);
        var export = Environment.GetEnvironmentVariable("PULSEAPK_INTEGRATION_REPORTS");
        if (!string.IsNullOrWhiteSpace(export)) await report.SaveAsync(export);
        Assert.Equal(StageState.Completed, report.Stages[0].State);
        Assert.Equal(StageState.Completed, report.Stages[2].State);
        Assert.Equal(StageState.Completed, report.Stages[3].State);
        Assert.Equal(StageState.Completed, report.Stages[4].State);
        Assert.True(report.IncludesLibraries);
        Assert.Equal(report.SmaliFilesFound, report.AnalyzedFiles);
        Assert.Equal(0, report.SkippedLibraryFiles);
        Assert.NotEmpty(report.Manifest.PackageName);
        Assert.Equal(report.Sha256, await ApkInputService.HashAsync(path));
    }
}
