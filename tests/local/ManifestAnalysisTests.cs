using PulseAPK.Core.Services;
using Xunit;

namespace PulseAPK.Local.Tests;

public sealed class ManifestAnalysisTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pulseapk-manifest", Guid.NewGuid().ToString("N"));
    public ManifestAnalysisTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private ManifestAnalysisResult Read(string content)
    {
        var path = Path.Combine(root, "AndroidManifest.xml"); File.WriteAllText(path, content);
        return ManifestAnalysisService.Analyse(path);
    }
    private const string Header = "<manifest xmlns:android='http://schemas.android.com/apk/res/android' package='example.fixture'>";
    [Fact] public void ListsPermissionsAndDoesNotTreatThemAsGranted()
    {
        var result = Read(Header + "<uses-sdk android:targetSdkVersion='34'/><uses-permission android:name='android.permission.READ_SMS'/><uses-permission android:name='android.permission.INTERNET'/><application/></manifest>");
        Assert.Equal("example.fixture", result.PackageName); Assert.Equal("34", result.TargetSdk);
        Assert.Equal(2, result.Permissions.Count); Assert.Single(result.Findings);
        Assert.Contains("Declara", result.Findings[0].Explanation); Assert.True(result.Findings[0].Line > 0);
    }
    [Fact] public void PermissionDuplicatesAreReportedOnceInInventory()
    {
        var result = Read(Header + "<uses-permission android:name='android.permission.INTERNET'/><uses-permission-sdk-23 android:name='android.permission.INTERNET'/><application/></manifest>");
        Assert.Single(result.Permissions);
    }
    [Fact] public void DoesNotGuessExportedDefaults()
    {
        var result = Read(Header + "<application><service android:name='.NotExplicit'/><activity android:name='.False' android:exported='false'/></application></manifest>");
        Assert.Empty(result.Findings);
    }
    [Fact] public void RecognizesInheritedComponentPermission()
    {
        var result = Read(Header + "<application android:permission='example.permission'><service android:name='.Protected' android:exported='true'/></application></manifest>");
        Assert.Empty(result.Findings);
    }
    [Fact] public void DisabledComponentDoesNotAppearAsActiveExport()
    {
        var result = Read(Header + "<application><service android:name='.Disabled' android:exported='true' android:enabled='false'/></application></manifest>");
        Assert.Empty(result.Findings);
    }
    [Fact] public void RecoversTargetSdkMovedByApktool()
    {
        File.WriteAllText(Path.Combine(root, "apktool.yml"), "sdkInfo:\n  minSdkVersion: 21\n  targetSdkVersion: 35\n");
        var result = Read(Header + "<application/></manifest>");
        Assert.Equal("35 (metadatos de Apktool)", result.TargetSdk);
    }
    [Fact] public void AdditionalCodeInventoryDoesNotClaimToAnalyseScripts()
    {
        var path = Path.Combine(root, "scripts.apk");
        using (var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            archive.CreateEntry("assets/game/script.rpyc"); archive.CreateEntry("assets/code.PY"); archive.CreateEntry("assets/picture.png");
        }
        var files = ApkInputService.AdditionalCodeInventory(path);
        Assert.Equal(2, files.Count); Assert.Contains("assets/game/script.rpyc", files);
        var report = new LocalAnalysisReport { AdditionalCodeFiles = files };
        Assert.Contains("COBERTURA LIMITADA", report.ToText());
    }
    [Fact] public void FlagsExplicitExportAndAccessibilityWithEvidence()
    {
        var result = Read(Header + "<application android:debuggable='true' android:usesCleartextTraffic='true'><receiver android:name='.Open' android:exported='true'/><service android:name='.Access' android:permission='android.permission.BIND_ACCESSIBILITY_SERVICE'/></application></manifest>");
        Assert.Equal(4, result.Findings.Count);
        Assert.Contains(result.Findings, f => f.Category == "Componente exportado" && f.Evidence.Contains(".Open"));
        Assert.Contains(result.Findings, f => f.Category == "Servicio especial");
    }
    [Fact] public void RejectsExternalEntityDeclarations()
    {
        Assert.Throws<System.Xml.XmlException>(() => Read("<!DOCTYPE manifest [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]>" + Header + "<application>&x;</application></manifest>"));
    }
    [Fact] public void RejectsMalformedAndUnrelatedXml()
    {
        Assert.Throws<System.Xml.XmlException>(() => Read("<manifest>"));
        Assert.Throws<InvalidDataException>(() => Read("<other/>"));
    }
    [Fact] public async Task WideModeIncludesKnownLibrariesWithoutChangingLegacyDefault()
    {
        var code = Path.Combine(root, "smali", "androidx", "fixture"); Directory.CreateDirectory(code);
        File.WriteAllText(Path.Combine(code, "Fixture.smali"), ".class public Landroidx/fixture/Fixture;\n.super Ljava/lang/Object;\n");
        var service = new SmaliAnalyserService();
        var quick = await service.AnalyseProjectAsync(root, null);
        var wide = await service.AnalyseProjectAsync(root, null, includeLibraries: true);
        Assert.Equal(1, quick.SkippedLibraryFiles); Assert.Equal(0, quick.AnalyzedFiles);
        Assert.Equal(0, wide.SkippedLibraryFiles); Assert.Equal(1, wide.AnalyzedFiles);
    }
}
