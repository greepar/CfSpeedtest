using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CfSpeedtest.Server.Services;

public sealed class ServerUpdateService(
    DataStore store,
    IHttpClientFactory httpClientFactory,
    IHostApplicationLifetime lifetime,
    ILogger<ServerUpdateService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _updateSignal = new(0, 1);

    public bool TriggerUpdateCheck()
    {
        try
        {
            _updateSignal.Release();
            return true;
        }
        catch (SemaphoreFullException)
        {
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var manuallyTriggered = await _updateSignal.WaitAsync(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var config = store.GetConfig();
            if (manuallyTriggered || config.ServerAutoUpdateEnabled)
            {
                try
                {
                    await CheckAndInstallAsync(config.ServerUpdateRepository, config.ServerUpdateGhProxyPrefix, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Server update check failed");
                }
            }

            var intervalMinutes = Math.Max(15, config.ServerUpdateIntervalMinutes);
            manuallyTriggered = await _updateSignal.WaitAsync(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
        }
    }

    private async Task CheckAndInstallAsync(string repository, string proxyPrefix, CancellationToken cancellationToken)
    {
        if (IsContainer())
        {
            logger.LogInformation("Server auto-update skipped in a container; update the container image instead");
            return;
        }

        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) ||
            !Path.GetFileNameWithoutExtension(currentExe).Equals("CfSpeedtest.Server", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Server auto-update requires the native CfSpeedtest.Server executable; current host is {ProcessPath}", currentExe);
            return;
        }

        repository = (repository ?? string.Empty).Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(repository) || repository.Count(c => c == '/') != 1)
        {
            logger.LogWarning("Server update repository must use owner/name format");
            return;
        }

        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CfSpeedtest-Server-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var releaseApiUrl = ApplyProxy(proxyPrefix, $"https://api.github.com/repos/{repository}/releases/latest");
        using var releaseResponse = await client.GetAsync(releaseApiUrl, cancellationToken);
        releaseResponse.EnsureSuccessStatusCode();
        await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var release = await JsonDocument.ParseAsync(releaseStream, cancellationToken: cancellationToken);

        var root = release.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version();
        if (!TryParseVersion(tag, out var latestVersion) || latestVersion <= currentVersion)
            return;

        var platform = DetectPlatform();
        var fileName = $"cfspeedtest-server-{platform}.zip";
        var asset = root.GetProperty("assets")
            .EnumerateArray()
            .FirstOrDefault(candidate => string.Equals(candidate.GetProperty("name").GetString(), fileName, StringComparison.OrdinalIgnoreCase));
        var downloadUrl = asset.ValueKind == JsonValueKind.Object && asset.TryGetProperty("browser_download_url", out var urlProperty)
            ? urlProperty.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            logger.LogWarning("Release {Tag} does not contain {FileName}", tag, fileName);
            return;
        }

        logger.LogInformation("Downloading server update {CurrentVersion} -> {LatestVersion}", currentVersion, latestVersion);
        var updateRoot = Path.Combine(Path.GetTempPath(), $"cfspeedtest-server-update-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(updateRoot, fileName);
        var stagingDir = Path.Combine(updateRoot, "staging");
        Directory.CreateDirectory(stagingDir);

        try
        {
            using var packageResponse = await client.GetAsync(ApplyProxy(proxyPrefix, downloadUrl), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            packageResponse.EnsureSuccessStatusCode();
            await using (var download = await packageResponse.Content.ReadAsStreamAsync(cancellationToken))
            await using (var archive = File.Create(archivePath))
            {
                await download.CopyToAsync(archive, cancellationToken);
            }

            ZipFile.ExtractToDirectory(archivePath, stagingDir, overwriteFiles: true);
            var stagedExe = Path.Combine(stagingDir, OperatingSystem.IsWindows() ? "CfSpeedtest.Server.exe" : "CfSpeedtest.Server");
            if (!File.Exists(stagedExe) || new FileInfo(stagedExe).Length == 0)
                throw new InvalidDataException($"Update package does not contain {Path.GetFileName(stagedExe)}");

            if (OperatingSystem.IsWindows())
            {
                ScheduleWindowsUpdate(stagedExe, currentExe, Environment.GetCommandLineArgs()[1..], IsSupervised());
                logger.LogInformation("Server update {LatestVersion} staged; exiting for replacement", latestVersion);
                lifetime.StopApplication();
                Environment.Exit(0);
                return;
            }

            ReplaceUnixExecutable(stagedExe, currentExe);
            try { Directory.Delete(updateRoot, recursive: true); } catch { }
            logger.LogInformation("Server update {LatestVersion} installed; restarting", latestVersion);
            RestartOrExit(currentExe, Environment.GetCommandLineArgs()[1..]);
        }
        finally
        {
            try { Directory.Delete(updateRoot, recursive: true); } catch { }
        }
    }

    private static void ReplaceUnixExecutable(string stagedExe, string currentExe)
    {
        var replacement = currentExe + ".update";
        File.Copy(stagedExe, replacement, overwrite: true);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(replacement, File.GetUnixFileMode(stagedExe));
        File.Move(replacement, currentExe, overwrite: true);
    }

    private static void RestartOrExit(string executable, IReadOnlyList<string> args)
    {
        if (!IsSupervised())
        {
            var startInfo = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("while kill -0 \"$1\" 2>/dev/null; do sleep 1; done; shift; exec \"$@\"");
            startInfo.ArgumentList.Add("cfspeedtest-server-updater");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add(executable);
            foreach (var arg in args) startInfo.ArgumentList.Add(arg);
            Process.Start(startInfo);
        }

        Environment.Exit(0);
    }

    private static void ScheduleWindowsUpdate(string stagedExe, string executable, IReadOnlyList<string> args, bool supervised)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"cfspeedtest-server-update-{Guid.NewGuid():N}.ps1");
        var script = "param($PidToWait,$StagedExe,$Executable,$Arguments,$RestartProcess,$ScriptPath)\n" +
                     "$ErrorActionPreference='Stop'\n" +
                     "Wait-Process -Id $PidToWait -ErrorAction SilentlyContinue\n" +
                     "Copy-Item -LiteralPath $StagedExe -Destination $Executable -Force\n" +
                     "if ($RestartProcess -eq 'true') { Start-Process -FilePath $Executable -ArgumentList $Arguments -WorkingDirectory (Split-Path -Parent $Executable) }\n" +
                     "Remove-Item -LiteralPath (Split-Path -Parent (Split-Path -Parent $StagedExe)) -Recurse -Force -ErrorAction SilentlyContinue\n" +
                     "Remove-Item -LiteralPath $ScriptPath -Force -ErrorAction SilentlyContinue\n";
        File.WriteAllText(scriptPath, script);

        var startInfo = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[]
                 {
                     "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                     "-PidToWait", Environment.ProcessId.ToString(), "-StagedExe", stagedExe,
                     "-Executable", executable,
                     "-Arguments", string.Join(' ', args.Select(QuoteArgument)),
                     "-RestartProcess", supervised ? "false" : "true", "-ScriptPath", scriptPath
                 })
        {
            startInfo.ArgumentList.Add(arg);
        }
        Process.Start(startInfo);
    }

    private static string DetectPlatform()
    {
        var architecture = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsWindows())
            return architecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.Arm64 => "win-arm64",
                _ => throw new PlatformNotSupportedException($"No Windows server update package is published for {architecture}"),
            };

        if (OperatingSystem.IsMacOS())
            return architecture switch
            {
                Architecture.X64 => "osx-x64",
                Architecture.Arm64 => "osx-arm64",
                _ => throw new PlatformNotSupportedException($"No macOS server update package is published for {architecture}"),
            };

        if (OperatingSystem.IsLinux())
        {
            if (RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase))
                throw new PlatformNotSupportedException("No musl server update package is published");

            return architecture switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm64 => "linux-arm64",
                _ => throw new PlatformNotSupportedException($"No Linux server update package is published for {architecture}"),
            };
        }

        throw new PlatformNotSupportedException("No server update package is published for this operating system");
    }

    private static bool TryParseVersion(string value, out Version version) =>
        Version.TryParse(value.Trim().TrimStart('v', 'V'), out version!);

    private static bool IsContainer() =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase) ||
        File.Exists("/.dockerenv");

    private static bool IsSupervised() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INVOCATION_ID")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RC_SVCNAME")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XPC_SERVICE_NAME")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NSSM_SERVICE_NAME"));

    private static string ApplyProxy(string prefix, string url) =>
        string.IsNullOrWhiteSpace(prefix) ? url : prefix.TrimEnd('/') + "/" + url;

    private static string QuoteArgument(string value) =>
        value.Any(char.IsWhiteSpace) || value.Contains('"') ? '"' + value.Replace("\"", "\\\"") + '"' : value;
}
