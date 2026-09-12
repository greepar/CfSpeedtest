using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using CfSpeedtest.Shared;

namespace CfSpeedtest.Server.Services;

public sealed class ServerUpdateService(
    DataStore store,
    IHttpClientFactory httpClientFactory,
    IHostApplicationLifetime lifetime,
    ILogger<ServerUpdateService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _updateSignal = new(0, 1);
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public bool TriggerUpdateInstall()
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
                    await InstallAvailableUpdateAsync(stoppingToken);
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

    public async Task<ServerUpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            return await CheckForUpdateCoreAsync(cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<ClientUpdateInfo> CheckClientUpdateAsync(string currentVersionText, string platform, CancellationToken cancellationToken)
    {
        var config = store.GetConfig();
        var currentVersion = ParseVersion(currentVersionText, "客户端版本号");
        var repository = ValidateRepository(GetClientUpdateRepository(config));
        using var release = await GetLatestReleaseAsync(repository, config.ClientUpdateGhProxyPrefix, cancellationToken);
        var root = release.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        var latestVersion = ParseVersion(tag, "GitHub Release 版本号");
        var fileName = GetClientUpdateFileName(platform);
        var asset = root.GetProperty("assets")
            .EnumerateArray()
            .FirstOrDefault(candidate => string.Equals(candidate.GetProperty("name").GetString(), fileName, StringComparison.OrdinalIgnoreCase));
        var downloadUrl = asset.ValueKind == JsonValueKind.Object && asset.TryGetProperty("browser_download_url", out var urlProperty)
            ? urlProperty.GetString()
            : null;
        var hasUpdate = latestVersion > currentVersion;

        return new ClientUpdateInfo
        {
            Enabled = config.ClientUpdateEnabled,
            CurrentVersion = currentVersion.ToString(3),
            LatestVersion = latestVersion.ToString(3),
            Platform = platform,
            HasUpdate = config.ClientUpdateEnabled && hasUpdate && !string.IsNullOrWhiteSpace(downloadUrl),
            DownloadUrl = config.ClientUpdateEnabled && hasUpdate && !string.IsNullOrWhiteSpace(downloadUrl)
                ? ApplyProxy(config.ClientUpdateGhProxyPrefix, downloadUrl)
                : null,
            DirectDownloadUrl = config.ClientUpdateEnabled && hasUpdate && !string.IsNullOrWhiteSpace(downloadUrl)
                ? downloadUrl
                : null,
            PackageFileName = fileName,
            Message = !config.ClientUpdateEnabled
                ? "客户端自动更新未启用"
                : !hasUpdate
                    ? "当前已是最新版本"
                    : string.IsNullOrWhiteSpace(downloadUrl)
                        ? $"Release {tag} 不包含当前平台安装包 {fileName}"
                        : "发现新版本"
        };
    }

    public async Task InstallAvailableUpdateAsync(CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var check = await CheckForUpdateCoreAsync(cancellationToken);
            if (!check.UpdateAvailable)
                return;

            await DownloadAndInstallAsync(check.LatestVersion, cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<ServerUpdateCheckResult> CheckForUpdateCoreAsync(CancellationToken cancellationToken)
    {
        var config = store.GetConfig();
        var repository = ValidateRepository(config.ServerUpdateRepository);
        using var release = await GetLatestReleaseAsync(repository, config.ServerUpdateGhProxyPrefix, cancellationToken);
        var tag = release.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version();
        if (!TryParseVersion(tag, out var latestVersion))
            throw new InvalidDataException($"GitHub Release 版本号无效：{tag}");

        var updateAvailable = latestVersion > currentVersion;
        return new ServerUpdateCheckResult
        {
            UpdateAvailable = updateAvailable,
            CurrentVersion = currentVersion.ToString(3),
            LatestVersion = latestVersion.ToString(3),
            Message = updateAvailable
                ? $"发现新版本 {latestVersion.ToString(3)}"
                : "目前已经是最新版本"
        };
    }

    private async Task DownloadAndInstallAsync(string expectedVersion, CancellationToken cancellationToken)
    {
        EnsureUpdateSupported();
        var config = store.GetConfig();
        var repository = ValidateRepository(config.ServerUpdateRepository);
        var proxyPrefix = config.ServerUpdateGhProxyPrefix;
        var currentExe = Environment.ProcessPath!;
        using var release = await GetLatestReleaseAsync(repository, proxyPrefix, cancellationToken);
        var root = release.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!TryParseVersion(tag, out var latestVersion) || latestVersion.ToString(3) != expectedVersion)
            throw new InvalidOperationException("最新版本在检查后发生变化，请重新检查更新");

        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version();

        var platform = DetectPlatform();
        var fileName = $"cfspeedtest-server-{platform}.zip";
        var asset = root.GetProperty("assets")
            .EnumerateArray()
            .FirstOrDefault(candidate => string.Equals(candidate.GetProperty("name").GetString(), fileName, StringComparison.OrdinalIgnoreCase));
        var downloadUrl = asset.ValueKind == JsonValueKind.Object && asset.TryGetProperty("browser_download_url", out var urlProperty)
            ? urlProperty.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidDataException($"Release {tag} 不包含当前平台安装包 {fileName}");

        logger.LogInformation("Downloading server update {CurrentVersion} -> {LatestVersion}", currentVersion, latestVersion);
        var updateRoot = Path.Combine(Path.GetTempPath(), $"cfspeedtest-server-update-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(updateRoot, fileName);
        var stagingDir = Path.Combine(updateRoot, "staging");
        Directory.CreateDirectory(stagingDir);

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(5);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CfSpeedtest-Server-Updater");
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

    private async Task<JsonDocument> GetLatestReleaseAsync(string repository, string proxyPrefix, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CfSpeedtest-Server-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var releaseApiUrl = ApplyProxy(proxyPrefix, $"https://api.github.com/repos/{repository}/releases/latest");
        using var response = await client.GetAsync(releaseApiUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string ValidateRepository(string repository)
    {
        repository = (repository ?? string.Empty).Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(repository) || repository.Count(c => c == '/') != 1)
            throw new InvalidOperationException("服务端更新仓库必须使用 owner/name 格式");
        return repository;
    }

    private static string GetClientUpdateRepository(ServerConfig config) =>
        !string.IsNullOrWhiteSpace(config.ClientUpdateRepository)
            ? config.ClientUpdateRepository
            : !string.IsNullOrWhiteSpace(config.ServerUpdateRepository)
                ? config.ServerUpdateRepository
                : "greepar/CfSpeedtest";

    private static void EnsureUpdateSupported()
    {
        if (IsContainer())
            throw new InvalidOperationException("Docker 部署不能在线更新，请更新容器镜像");

        var currentExe = Environment.ProcessPath;
        if (RuntimeFeature.IsDynamicCodeSupported || string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            throw new InvalidOperationException("当前不是 NativeAOT 单文件进程，不能执行在线更新");
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

    private static Version ParseVersion(string value, string fieldName)
    {
        var normalized = value.Trim().TrimStart('v', 'V');
        var metadataIndex = normalized.IndexOfAny(['+', '-']);
        if (metadataIndex >= 0)
            normalized = normalized[..metadataIndex];
        if (!Version.TryParse(normalized, out var version))
            throw new InvalidDataException($"{fieldName}无效：{value}");
        return version;
    }

    private static string GetClientUpdateFileName(string platform) => platform switch
    {
        "win-x86" => "cfspeedtest-client-win-x86.zip",
        "win-x64" => "cfspeedtest-client-win-x64.zip",
        "win-arm64" => "cfspeedtest-client-win-arm64.zip",
        "linux-x64" => "cfspeedtest-client-linux-x64.zip",
        "linux-musl-x64" => "cfspeedtest-client-linux-musl-x64.zip",
        "linux-arm64" => "cfspeedtest-client-linux-arm64.zip",
        "linux-musl-arm64" => "cfspeedtest-client-linux-musl-arm64.zip",
        "linux-arm" => "cfspeedtest-client-linux-arm.zip",
        "osx-x64" => "cfspeedtest-client-osx-x64.zip",
        "osx-arm64" => "cfspeedtest-client-osx-arm64.zip",
        _ => $"cfspeedtest-client-{platform}.zip"
    };

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
