using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PulseAPK.Core.Services;

public interface IDefenderScanService
{
    Task<DefenderResult> ScanAsync(string path, CancellationToken token);
}
public sealed class DefenderScanService(ILocalProcessRunner processes) : IDefenderScanService
{
    public static DefenderResult Interpret(LocalProcessResult result, string version, string signatures)
    {
        // Code 2 means either an unremediated detection OR a scan error. Never infer infection from it alone.
        var names = Regex.Matches(result.Output, @"(?im)^\s*Threat\s*:\s*(.+)$").Select(m => m.Groups[1].Value.Trim()).ToArray();
        var state = result.ExitCode == 0 ? AntivirusState.NoDetections
            : result.ExitCode == 2 && names.Length > 0 ? AntivirusState.Detected
            : result.ExitCode == 2 ? AntivirusState.ReviewRequired : AntivirusState.Error;
        return new(state, result.Output, version, signatures, result.ExitCode);
    }

    public async Task<DefenderResult> ScanAsync(string path, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return new(AntivirusState.Unavailable, "Defender requiere Windows.");
        try
        {
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var check = await processes.RunAsync(powershell, new[] { "-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; $s=Get-MpComputerStatus; $p=Get-MpPreference; [pscustomobject]@{Enabled=($s.AMServiceEnabled -and $s.AntivirusEnabled); Maps=[int]$p.MAPSReporting; Version=$s.AMProductVersion; Signatures=$s.AntivirusSignatureVersion} | ConvertTo-Json -Compress" }, token);
            if (check.ExitCode != 0) return new(AntivirusState.Unavailable, "No se pudo consultar Defender. " + check.Output);
            using var json = JsonDocument.Parse(check.Output);
            var data = json.RootElement;
            var version = data.GetProperty("Version").GetString() ?? "";
            var signatures = data.GetProperty("Signatures").GetString() ?? "";
            if (!data.GetProperty("Enabled").GetBoolean()) return new(AntivirusState.Unavailable, "Defender no está activo.", version, signatures);
            if (data.GetProperty("Maps").GetInt32() != 0)
                return new(AntivirusState.Unavailable, "Omitido para respetar el modo privado: Defender tiene participación en la nube activada. PulseAPK no cambia la protección de Windows. El análisis del código sigue disponible.", version, signatures);
            var executable = FindExecutable();
            if (executable is null) return new(AntivirusState.Unavailable, "No se encontró MpCmdRun.exe.", version, signatures);
            var result = await processes.RunAsync(executable, new[] { "-Scan", "-ScanType", "3", "-File", path, "-DisableRemediation" }, token);
            return Interpret(result, version, signatures);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(AntivirusState.Unavailable, "No se pudo usar Defender: " + ex.Message); }
    }

    public static string? FindExecutable()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender", "Platform");
        if (Directory.Exists(root))
        {
            foreach (var directory in Directory.GetDirectories(root).OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var candidate = Path.Combine(directory, "MpCmdRun.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        return File.Exists(fallback) ? fallback : null;
    }
}
