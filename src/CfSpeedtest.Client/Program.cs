using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Net.WebSockets;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CfSpeedtest.Shared;

// ============================================================
//  Cloudflare IP 测速客户端 (NativeAOT Compatible)
// ============================================================

if (args.Length == 0 || HasFlag(args, "help") || HasFlag(args, "h") || HasFlag(args, "?"))
{
    PrintHelp();
    return args.Length == 0 ? 1 : 0;
}

// 处理 --install / --uninstall 命令（优先于正常启动）
if (HasFlag(args, "install"))
{
    if (!ValidateRequiredArgs(args, installing: true))
        return 1;
    return ServiceInstaller.Install(args);
}
if (HasFlag(args, "uninstall"))
{
    return ServiceInstaller.Uninstall();
}

if (OperatingSystem.IsWindows() && HasFlag(args, "service") && !HasFlag(args, "service-worker"))
{
    return WindowsServiceWrapper.Run(args);
}

if (!ValidateRequiredArgs(args, installing: false))
    return 1;

Console.WriteLine("=== Cloudflare IP SpeedTest Client ===");
Console.WriteLine();

CleanupOldFiles();

// 读取配置
var serverUrl = GetArg(args, "server", string.Empty);
var explicitClientId = GetArg(args, "client-id", "");
var ispStr = GetArg(args, "isp", "Telecom");
var configuredClientName = GetArg(args, "name", Environment.MachineName);
var intervalStr = GetArg(args, "interval", "60"); // 默认60分钟
var autoUpdate = !HasFlag(args, "disable-auto-update");
var isService = HasFlag(args, "service") || HasFlag(args, "service-worker");
var oneshot = HasFlag(args, "once");
var currentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
var clientPlatform = DetectClientPlatform();

if (!Enum.TryParse<IspType>(ispStr, true, out var configuredIsp))
{
    Console.WriteLine($"Invalid ISP: {ispStr}, options: Telecom, Unicom, Mobile");
    return 1;
}

var runtimeProfile = new ClientRuntimeProfile(configuredIsp, configuredClientName);
var runtimeState = new ClientRuntimeState();
var proxySettings = new ClientProxySettings();
using var transportState = new ClientTransportState(proxySettings);
runtimeState.AppendLog("Client process starting");

if (!int.TryParse(intervalStr, out var intervalMinutes) || intervalMinutes <= 0)
{
    Console.WriteLine($"Invalid interval: {intervalStr}");
    PrintHelp();
    return 1;
}
Console.WriteLine($"Server:   {serverUrl}");
Console.WriteLine($"ISP:      {runtimeProfile.Isp}");
Console.WriteLine($"Name:     {runtimeProfile.Name}");
Console.WriteLine($"Version:  {currentVersion}");
Console.WriteLine($"Platform: {clientPlatform}");
Console.WriteLine($"Default interval: {intervalMinutes}min");
Console.WriteLine();
runtimeState.AppendLog($"Server={serverUrl}, ISP={runtimeProfile.Isp}, Name={runtimeProfile.Name}, Version={currentVersion}, Platform={clientPlatform}");

var currentClientId = string.IsNullOrWhiteSpace(explicitClientId) ? string.Empty : explicitClientId.Trim();
if (!string.IsNullOrWhiteSpace(currentClientId))
{
    Console.WriteLine($"ClientId: {currentClientId}");
}

const int StartupRetryDelaySeconds = 5;
await CheckForUpdateAsync(serverUrl, currentVersion, clientPlatform, autoUpdate, isService, transportState.HttpClient);

// NativeAOT-safe JSON options using source generators
var jsonOpts = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    TypeInfoResolverChain = { AppJsonContext.Default }
};

// ===== 注册客户端 =====
Console.WriteLine("[1] Registering with server...");
runtimeState.AppendLog("Registering with server");
string clientId;
int heartbeatIntervalSeconds = 30;
while (true)
{
    try
    {
        var regReq = new ClientRegisterRequest
        {
            ClientId = currentClientId,
            Isp = runtimeProfile.Isp,
            Name = runtimeProfile.Name,
            Version = currentVersion,
            Platform = clientPlatform,
        };
        var regJson = JsonSerializer.Serialize(regReq, AppJsonContext.Default.ClientRegisterRequest);
        var regResp = await transportState.HttpClient.PostAsync(
            $"{serverUrl}/api/client/register",
            new StringContent(regJson, Encoding.UTF8, "application/json"));
        regResp.EnsureSuccessStatusCode();
        var regBody = await regResp.Content.ReadAsStringAsync();
        var regResult = JsonSerializer.Deserialize(regBody, AppJsonContext.Default.ApiResponseClientRegisterResponse);
        if (regResult?.Success != true || regResult.Data is null)
        {
            Console.WriteLine($"Registration failed: {regResult?.Message}");
            runtimeState.AppendLog($"Registration failed: {regResult?.Message}");
            await Task.Delay(TimeSpan.FromSeconds(StartupRetryDelaySeconds));
            continue;
        }

        clientId = regResult.Data.ClientId;
        heartbeatIntervalSeconds = regResult.Data.HeartbeatIntervalSeconds > 0
            ? regResult.Data.HeartbeatIntervalSeconds
            : heartbeatIntervalSeconds;
        currentClientId = clientId;
        ApplyAuthoritativeClientMetadata(serverUrl, currentClientId, isService, runtimeProfile, proxySettings, transportState, regResult.Data.EffectiveIsp, regResult.Data.EffectiveName, regResult.Data.EffectiveProxyMode, regResult.Data.EffectiveProxyUrl);
        if (ShouldBackwriteServiceArguments(isService) && !string.IsNullOrWhiteSpace(currentClientId))
        {
            TryUpdateServiceArguments(serverUrl, currentClientId, runtimeProfile.Isp, runtimeProfile.Name);
        }
        Console.WriteLine($"Registered as: {clientId}");
        runtimeState.AppendLog($"Registered as {clientId}");
        break;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to register: {ex.Message}");
        runtimeState.AppendLog($"Failed to register: {ex.Message}");
        Console.WriteLine($"Retrying in {StartupRetryDelaySeconds}s...");
        await Task.Delay(TimeSpan.FromSeconds(StartupRetryDelaySeconds));
    }
}

using var heartbeatCts = new CancellationTokenSource();
using var immediateFetchSignal = new SemaphoreSlim(0, 1);
var heartbeatTask = StartHeartbeatLoopAsync(serverUrl, clientId, runtimeProfile, runtimeState, proxySettings, transportState, currentVersion, clientPlatform, autoUpdate, isService, transportState.HttpClient, heartbeatIntervalSeconds, immediateFetchSignal, heartbeatCts.Token);

// ===== 主循环 =====
while (true)
{
    Console.WriteLine();
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Waiting for server trigger...");
    runtimeState.SetWaiting();
    await WaitForFetchSignalAsync(immediateFetchSignal);
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Fetching task...");
    runtimeState.AppendLog("Server trigger received. Fetching task");

    try
    {
        await RunTestCycleAsync(serverUrl, clientId, runtimeProfile, runtimeState, transportState, immediateFetchSignal);

        if (oneshot) break;

        continue;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in test cycle: {ex.Message}");
        runtimeState.AppendLog($"Error in test cycle: {ex.Message}");
    }

    if (oneshot) break;
}

heartbeatCts.Cancel();
try { await heartbeatTask; } catch { }
return 0;

// ============================================================
//  核心测速逻辑
// ============================================================
static async Task RunTestCycleAsync(string serverUrl, string clientId, ClientRuntimeProfile runtimeProfile,
    ClientRuntimeState runtimeState, ClientTransportState transportState, SemaphoreSlim immediateFetchSignal)
{
    SpeedTestTask? task = null;
    while (task is null)
    {
        var taskResp = await transportState.HttpClient.GetAsync($"{serverUrl}/api/task/{clientId}");
        taskResp.EnsureSuccessStatusCode();
        var taskBody = await taskResp.Content.ReadAsStringAsync();
        var taskResult = JsonSerializer.Deserialize(taskBody, AppJsonContext.Default.ApiResponseSpeedTestTask);
        if (taskResult?.Success == true && taskResult.Data is not null)
        {
            task = taskResult.Data;
            break;
        }

        var message = taskResult?.Message ?? "No task available";
        Console.WriteLine($"No task available: {message}");
        runtimeState.AppendLog($"No task available: {message}");
        var retryMatch = Regex.Match(message, @"Retry after\s+(\d+)\s+seconds", RegexOptions.IgnoreCase);
        if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out var retrySeconds))
        {
            await WaitForRetryOrFetchSignalAsync(TimeSpan.FromSeconds(Math.Max(1, retrySeconds)), immediateFetchSignal);
            continue;
        }

        throw new InvalidOperationException(message);
    }

    Console.WriteLine($"Got task {task.TaskId[..8]}: {task.IpAddresses.Count} IPs to test");
    runtimeState.AppendLog($"Got task {task.TaskId}: {task.IpAddresses.Count} IPs");
    Console.WriteLine($"Test URL template: {task.TestUrl}");
    Console.WriteLine($"Host: {task.TestHost}, Port: {task.TestPort}");
    Console.WriteLine($"Server interval: {task.ClientIntervalMinutes}min");
    Console.WriteLine();

    var allResults = new List<IpTestResult>();
    var testedIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var pendingIps = new Queue<string>(task.IpAddresses);
    var maxTestIpCount = Math.Max(task.IpAddresses.Count, task.MaxTestIpCount);
    var currentBatchRemaining = pendingIps.Count;
    Console.WriteLine($"MaxTestIpCount: {maxTestIpCount} (server={task.MaxTestIpCount}, batch={task.IpAddresses.Count})");
    runtimeState.AppendLog($"MaxTestIpCount={maxTestIpCount}");
    runtimeState.SetTesting(maxTestIpCount, 0);

    while (pendingIps.Count > 0)
    {
        if (testedIps.Count >= maxTestIpCount)
        {
            runtimeState.AppendLog($"Reached max test IP count limit: {maxTestIpCount}");
            break;
        }

        var ip = pendingIps.Dequeue();
        if (!testedIps.Add(ip))
            continue;
        currentBatchRemaining--;

        runtimeState.SetTesting(maxTestIpCount, testedIps.Count - 1);

        Console.Write($"  [{testedIps.Count}] {ip,-16} ");
        runtimeState.AppendLog($"[{testedIps.Count}] Testing {ip}");

        var result = new IpTestResult { IpAddress = ip };

        // 2a. TCP 延迟/丢包测试
        await TestTcpAsync(result, ip, task.TestPort, task.TcpTestDurationSeconds);
        Console.Write($"TCP: {result.AvgLatencyMs,6:F1}ms loss:{result.PacketLossRate,5:P1} | ");

        // 如果丢包率太高,跳过下载测试
        if (result.PacketLossRate > 0.5)
        {
            Console.WriteLine("SKIP (high loss)");
            runtimeState.AppendLog($"{ip} skipped due to high loss");
            result.Score = 0;
            allResults.Add(result);
        }
        else
        {
            // 2b. 下载速度测试
            await TestDownloadAsync(result, ip, task.TestUrl, task.TestHost, task.TestPort, task.DownloadDurationSeconds, task.MaxDownloadSpeedKBps, transportState.ProxySettings);
            Console.Write($"DL: {result.DownloadSpeedKBps,8:F1} KB/s | ");

            // 2c. 综合评分: 速度权重60%, 延迟权重25%, 丢包权重15%
            var speedScore = Math.Min(result.DownloadSpeedKBps / 1000.0, 100.0); // 归一化到0-100
            var latencyScore = Math.Max(0, 100.0 - result.AvgLatencyMs);         // 延迟越低越好
            var lossScore = (1.0 - result.PacketLossRate) * 100.0;               // 丢包越少越好
            result.Score = speedScore * 0.60 + latencyScore * 0.25 + lossScore * 0.15;

            Console.WriteLine($"Score: {result.Score:F1}");
            runtimeState.AppendLog($"{ip} TCP={result.AvgLatencyMs:F1}ms loss={result.PacketLossRate:P1} DL={result.DownloadSpeedKBps:F1}KB/s Score={result.Score:F1}");
            allResults.Add(result);
        }
        runtimeState.SetTesting(maxTestIpCount, testedIps.Count);

        // 当前批次测完后再判断是否达标；只要已有达标结果就停止，不继续拉下一批
        if (currentBatchRemaining == 0 && pendingIps.Count == 0 && testedIps.Count < maxTestIpCount)
        {
            var qualifiedCount = allResults.Count(r => r.DownloadSpeedKBps >= task.MinDownloadSpeedKBps);
            if (qualifiedCount > 0)
            {
                runtimeState.AppendLog($"Batch completed and found {qualifiedCount} qualified result(s), stopping");
                break;
            }

            var additionalIps = await FetchAdditionalIpsAsync(serverUrl, clientId, runtimeProfile.Isp, testedIps, transportState.HttpClient);
            runtimeState.AppendLog($"Requested additional IP batch, received {additionalIps.Count} IP(s)");
            foreach (var extraIp in additionalIps)
            {
                if (!testedIps.Contains(extraIp))
                    pendingIps.Enqueue(extraIp);
            }
            currentBatchRemaining = pendingIps.Count;
        }
        else if (pendingIps.Count == 0 && testedIps.Count >= maxTestIpCount)
        {
            runtimeState.AppendLog($"Reached max test IP count limit: {maxTestIpCount}, no more fetching");
        }
    }

    // 3. 优先保留达标结果；如果一个都不达标，至少回传 1 个最优结果，避免完全空结果
    var qualifiedResults = allResults
        .Where(r => r.DownloadSpeedKBps >= task.MinDownloadSpeedKBps)
        .OrderByDescending(r => r.Score)
        .Take(task.TopN)
        .ToList();

    var topResults = qualifiedResults.Count > 0
        ? qualifiedResults
        : allResults
            .OrderByDescending(r => r.Score)
            .Take(task.TopN)
            .ToList();

    Console.WriteLine();
    if (qualifiedResults.Count > 0)
        Console.WriteLine($"=== Qualified Top {topResults.Count} Results ===");
    else
        Console.WriteLine($"=== No results met min speed {task.MinDownloadSpeedKBps:F1} KB/s, fallback to top {topResults.Count} by score ===");
    for (int i = 0; i < topResults.Count; i++)
    {
        var r = topResults[i];
        Console.WriteLine($"  #{i + 1} {r.IpAddress,-16} Speed:{r.DownloadSpeedKBps,8:F1} KB/s  " +
            $"Latency:{r.AvgLatencyMs,6:F1}ms  Loss:{r.PacketLossRate:P1}  Score:{r.Score:F1}");
    }

    // 4. 上报结果
    Console.WriteLine();
    Console.Write("Reporting results... ");
    runtimeState.AppendLog($"Reporting {topResults.Count} result(s)");
    var report = new SpeedTestReport
    {
        TaskId = task.TaskId,
        ClientId = clientId,
        Isp = runtimeProfile.Isp,
        Results = topResults,
        CompletedAt = DateTime.UtcNow,
    };
    var reportJson = JsonSerializer.Serialize(report, AppJsonContext.Default.SpeedTestReport);
    await PostReportWithRetryAsync(serverUrl, reportJson, transportState, runtimeState);
    Console.WriteLine("OK");
    runtimeState.SetCompleted(task.IpAddresses.Count, topResults.Count);
    runtimeState.AppendLog("Report completed successfully");
}

static async Task PostReportWithRetryAsync(string serverUrl, string reportJson, ClientTransportState transportState, ClientRuntimeState runtimeState)
{
    const int maxAttempts = 3;
    Exception? lastError = null;

    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            using var reportResp = await transportState.HttpClient.PostAsync(
                $"{serverUrl}/api/report",
                new StringContent(reportJson, Encoding.UTF8, "application/json"));
            reportResp.EnsureSuccessStatusCode();
            return;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            lastError = ex;
            runtimeState.AppendLog($"Report attempt {attempt}/{maxAttempts} failed: {ex.Message}, retrying...");
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            lastError = ex;
        }
    }

    throw new InvalidOperationException($"Report failed after {maxAttempts} attempts: {lastError?.Message}", lastError);
}

static async Task CheckForUpdateAsync(string serverUrl, string currentVersion, string clientPlatform, bool autoUpdate, bool isService, HttpClient httpClient)
{
    if (!await ClientUpdateLock.Semaphore.WaitAsync(0))
    {
        Console.WriteLine("Update check skipped: another update check is already running.");
        return;
    }

    string? downloadDir = null;
    string? stagingDir = null;
    try
    {
        var resp = await httpClient.GetAsync($"{serverUrl}/api/client/update?version={Uri.EscapeDataString(currentVersion)}&platform={Uri.EscapeDataString(clientPlatform)}");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(body, AppJsonContext.Default.ApiResponseClientUpdateInfo);
        if (result?.Success != true || result.Data is null)
            return;

        var info = result.Data;
        if (!info.Enabled)
        {
            Console.WriteLine($"Update check: {info.Message}");
            return;
        }

        if (!info.HasUpdate || string.IsNullOrWhiteSpace(info.DownloadUrl))
        {
            Console.WriteLine($"Update check: {info.Message}");
            return;
        }

        Console.WriteLine($"New client version available for {info.Platform}: {info.LatestVersion} ({info.DownloadUrl})");
        if (!autoUpdate)
        {
            Console.WriteLine("Auto-update disabled. Start client without --disable-auto-update to update automatically.");
            return;
        }

        downloadDir = Path.Combine(Path.GetTempPath(), $"cfspeedtest-update-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(downloadDir);
        var tempFile = Path.Combine(downloadDir, Path.GetFileName(new Uri(info.DownloadUrl).AbsolutePath));
        Console.WriteLine("Downloading update package...");
        using (var download = await httpClient.GetStreamAsync(info.DownloadUrl))
        using (var file = File.Create(tempFile))
        {
            await download.CopyToAsync(file);
        }
        Console.WriteLine($"Update package downloaded to: {tempFile}");

        var currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(currentExe))
        {
            Console.WriteLine("Cannot determine current executable path. Please replace manually.");
            return;
        }

        var targetDir = Path.GetDirectoryName(currentExe)!;
        stagingDir = Path.Combine(Path.GetTempPath(), $"cfspeedtest-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        Console.WriteLine("Extracting update package...");
        ZipFile.ExtractToDirectory(tempFile, stagingDir, true);

        if (isService && OperatingSystem.IsWindows())
        {
            Console.WriteLine("Scheduling Windows service update and restart...");
            ScheduleWindowsServiceUpdate(stagingDir, targetDir);
            TryDeleteDirectory(downloadDir);
            stagingDir = null;
            downloadDir = null;
            Environment.Exit(0);
        }

        Console.WriteLine("Applying update files...");
        await ApplyUpdateInPlaceAsync(stagingDir, targetDir);
        TryDeleteDirectory(stagingDir);
        TryDeleteDirectory(downloadDir);
        stagingDir = null;
        downloadDir = null;

        if (isService)
        {
            Console.WriteLine("Update installed. Please restart the service or service manager to load the new version.");
            Environment.Exit(0);
        }
        else
        {
            Console.WriteLine("Update installed. Restarting client process...");
            RestartCurrentProcess(currentExe, Environment.GetCommandLineArgs().Skip(1).Where(a => !string.Equals(a, "--apply-update", StringComparison.OrdinalIgnoreCase)).ToArray());
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Update check failed: {ex.Message}");
    }
    finally
    {
        TryDeleteDirectory(stagingDir);
        TryDeleteDirectory(downloadDir);
        ClientUpdateLock.Semaphore.Release();
    }
}

static void RestartCurrentProcess(string currentExe, IReadOnlyList<string> args)
{
    Process.Start(new ProcessStartInfo
    {
        FileName = currentExe,
        Arguments = string.Join(" ", args.Select(QuoteArg)),
        UseShellExecute = false,
        WorkingDirectory = Path.GetDirectoryName(currentExe) ?? AppContext.BaseDirectory,
    });

    Environment.Exit(0);
}

static string DetectClientPlatform()
{
    if (OperatingSystem.IsWindows())
    {
        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => "win-x64",
        };
    }

    if (OperatingSystem.IsLinux())
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        var isMusl = rid.Contains("musl", StringComparison.OrdinalIgnoreCase);
        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => isMusl ? "linux-musl-arm64" : "linux-arm64",
            Architecture.Arm => "linux-arm",
            _ => isMusl ? "linux-musl-x64" : "linux-x64",
        };
    }

    if (OperatingSystem.IsMacOS())
    {
        return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
    }

    return "win-x64";
}

static async Task<List<string>> FetchAdditionalIpsAsync(
    string serverUrl,
    string clientId,
    IspType isp,
    HashSet<string> testedIps,
    HttpClient httpClient)
{
    Console.WriteLine("  Qualified results not enough, requesting additional IP batch...");

    var req = new AdditionalIpBatchRequest
    {
        ClientId = clientId,
        Isp = isp,
        ExcludeIps = testedIps.ToList(),
    };

    var json = JsonSerializer.Serialize(req, AppJsonContext.Default.AdditionalIpBatchRequest);
    var resp = await httpClient.PostAsync(
        $"{serverUrl}/api/task/additional",
        new StringContent(json, Encoding.UTF8, "application/json"));
    resp.EnsureSuccessStatusCode();
    var body = await resp.Content.ReadAsStringAsync();
    var result = JsonSerializer.Deserialize(body, AppJsonContext.Default.ApiResponseAdditionalIpBatchResponse);

    if (result?.Success != true || result.Data is null)
    {
        Console.WriteLine($"  Additional batch unavailable: {result?.Message}");
        return [];
    }

    var ips = result.Data.IpAddresses
        .Where(ip => !testedIps.Contains(ip))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    Console.WriteLine($"  Received {ips.Count} additional IP(s)");
    return ips;
}

static Task StartHeartbeatLoopAsync(
    string serverUrl,
    string clientId,
    ClientRuntimeProfile runtimeProfile,
    ClientRuntimeState runtimeState,
    ClientProxySettings proxySettings,
    ClientTransportState transportState,
    string currentVersion,
    string clientPlatform,
    bool autoUpdate,
    bool isService,
    HttpClient httpClient,
    int heartbeatIntervalSeconds,
    SemaphoreSlim immediateFetchSignal,
    CancellationToken cancellationToken)
{
    return Task.Run(async () =>
    {
        var intervalSeconds = Math.Max(5, heartbeatIntervalSeconds);
        var wsEverConnected = false;
        var wsConsecutiveFailures = 0;
        DateTime? wsFallbackUntilUtc = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var nowUtc = DateTime.UtcNow;
            var inFallbackWindow = wsFallbackUntilUtc.HasValue && nowUtc < wsFallbackUntilUtc.Value;

            if (!inFallbackWindow)
            {
            var wsResult = await TryStartWebSocketHeartbeatAsync(serverUrl, clientId, runtimeProfile, runtimeState, proxySettings, transportState, currentVersion, clientPlatform, autoUpdate, isService, immediateFetchSignal, cancellationToken);
                if (wsResult == 1)
                {
                    wsEverConnected = true;
                    wsConsecutiveFailures = 0;
                    wsFallbackUntilUtc = null;
                    continue;
                }

                wsConsecutiveFailures++;
                var shouldFallbackHttp = wsEverConnected || wsConsecutiveFailures >= 3;
                if (!shouldFallbackHttp)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    continue;
                }

                wsFallbackUntilUtc = DateTime.UtcNow.AddSeconds(60);
                inFallbackWindow = true;
                Console.WriteLine("WebSocket failed repeatedly. Entering HTTP heartbeat fallback for 60 seconds.");
            }

            var heartbeatSucceeded = false;
            Exception? heartbeatError = null;
            try
            {
                var snapshot = runtimeProfile.GetSnapshot();
                var req = new ClientHeartbeatRequest
                {
                    ClientId = clientId,
                    Isp = snapshot.Isp,
                    Name = snapshot.Name,
                    Version = currentVersion,
                    Platform = clientPlatform,
                    RuntimeStatus = runtimeState.Status,
                    CurrentTaskTotalIps = runtimeState.TotalIps,
                    CurrentTaskTestedIps = runtimeState.TestedIps,
                    CurrentTaskStartedAt = runtimeState.StartedAt,
                    RuntimeLog = runtimeState.LogText,
                };
                var json = JsonSerializer.Serialize(req, AppJsonContext.Default.ClientHeartbeatRequest);
                var resp = await transportState.HttpClient.PostAsync(
                    $"{serverUrl}/api/client/heartbeat",
                    new StringContent(json, Encoding.UTF8, "application/json"),
                    cancellationToken);
                resp.EnsureSuccessStatusCode();

                var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize(body, AppJsonContext.Default.ApiResponseClientHeartbeatResponse);
                if (result?.Success == true && result.Data?.HeartbeatIntervalSeconds > 0)
                {
                    heartbeatSucceeded = true;
                    ApplyAuthoritativeClientMetadata(serverUrl, clientId, isService, runtimeProfile, proxySettings, transportState, result.Data.EffectiveIsp, result.Data.EffectiveName, result.Data.EffectiveProxyMode, result.Data.EffectiveProxyUrl);
                    if (result.Data.ForceFetchTask && immediateFetchSignal.CurrentCount == 0)
                    {
                        immediateFetchSignal.Release();
                    }
                    if (result.Data.ForceCheckUpdate)
                    {
                        Console.WriteLine("Manual update trigger received. Checking update immediately...");
                        await CheckForUpdateAsync(serverUrl, currentVersion, clientPlatform, autoUpdate, isService, transportState.HttpClient);
                    }

                    var nextIntervalSeconds = Math.Max(5, result.Data.HeartbeatIntervalSeconds);
                    intervalSeconds = nextIntervalSeconds;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 心跳失败不打断主测速流程
                heartbeatError = ex;
            }

            if (heartbeatSucceeded)
            {
                wsConsecutiveFailures = 0;
                wsFallbackUntilUtc = null;
                Console.WriteLine("HTTP heartbeat restored. Retrying WebSocket heartbeat.");
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                continue;
            }

            if (heartbeatError is not null)
            {
                Console.WriteLine($"HTTP heartbeat fallback failed: {heartbeatError.Message}");
            }

            var retrySeconds = inFallbackWindow ? Math.Min(intervalSeconds, 10) : intervalSeconds;
            await Task.Delay(TimeSpan.FromSeconds(retrySeconds), cancellationToken);
        }
    }, cancellationToken);
}

static async Task<int> TryStartWebSocketHeartbeatAsync(
    string serverUrl,
    string clientId,
    ClientRuntimeProfile runtimeProfile,
    ClientRuntimeState runtimeState,
    ClientProxySettings proxySettings,
    ClientTransportState transportState,
    string currentVersion,
    string clientPlatform,
    bool autoUpdate,
    bool isService,
    SemaphoreSlim immediateFetchSignal,
    CancellationToken cancellationToken)
{
    const int WsConnectTimeoutSeconds = 10;
    const int WsIdleTimeoutSeconds = 90;
    var connected = false;
    try
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        var wsUrl = BuildWebSocketUrl(serverUrl, clientId, runtimeProfile.Isp);
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(WsConnectTimeoutSeconds));
            await ws.ConnectAsync(new Uri(wsUrl), connectCts.Token);
        }
        connected = true;
        Console.WriteLine("WebSocket heartbeat connected.");

        var intervalSeconds = 30;
        var receiveTask = Task.Run(async () =>
        {
            var receiveBuffer = new byte[8192];
            while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                receiveCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(WsIdleTimeoutSeconds, intervalSeconds * 3)));
                var body = await ReceiveWebSocketTextMessageAsync(ws, receiveBuffer, receiveCts.Token);
                if (body is null)
                    break;
                var msg = JsonSerializer.Deserialize(body, AppJsonContext.Default.ClientWsMessage);
                if (msg is not null)
                {
                    ApplyAuthoritativeClientMetadata(serverUrl, clientId, isService, runtimeProfile, proxySettings, transportState, msg.EffectiveIsp, msg.EffectiveName, msg.EffectiveProxyMode, msg.EffectiveProxyUrl);
                    intervalSeconds = Math.Max(5, msg.HeartbeatIntervalSeconds > 0 ? msg.HeartbeatIntervalSeconds : intervalSeconds);
                    if ((msg.ForceFetchTask || string.Equals(msg.Type, "trigger-test", StringComparison.OrdinalIgnoreCase)) && immediateFetchSignal.CurrentCount == 0)
                        immediateFetchSignal.Release();
                    if (msg.ForceCheckUpdate)
                    {
                        Console.WriteLine("Manual update trigger received via WebSocket. Checking update immediately...");
                        await CheckForUpdateAsync(serverUrl, currentVersion, clientPlatform, autoUpdate, isService, transportState.HttpClient);
                    }
                }
            }
        }, cancellationToken);

        while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var snapshot = runtimeProfile.GetSnapshot();
            var heartbeat = new ClientWsMessage
            {
                Type = "heartbeat",
                ClientId = clientId,
                Isp = snapshot.Isp,
                Name = snapshot.Name,
                Version = currentVersion,
                Platform = clientPlatform,
                RuntimeStatus = runtimeState.Status,
                CurrentTaskTotalIps = runtimeState.TotalIps,
                CurrentTaskTestedIps = runtimeState.TestedIps,
                CurrentTaskStartedAt = runtimeState.StartedAt,
                RuntimeLog = runtimeState.LogText,
            };
            var json = JsonSerializer.Serialize(heartbeat, AppJsonContext.Default.ClientWsMessage);
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            var delayTask = Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);
            var completed = await Task.WhenAny(delayTask, receiveTask);
            if (completed == receiveTask)
            {
                break;
            }
        }

        await receiveTask;

        Console.WriteLine("WebSocket heartbeat disconnected.");
        return 1;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception ex)
    {
        if (connected)
        {
            Console.WriteLine($"WebSocket heartbeat disconnected: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"WebSocket heartbeat unavailable: {ex.Message}");
        return 0;
    }
}

static string BuildWebSocketUrl(string serverUrl, string clientId, IspType isp)
{
    var baseUri = new Uri(serverUrl);
    var scheme = baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
    return $"{scheme}://{baseUri.Authority}/api/client/ws?clientId={Uri.EscapeDataString(clientId)}&isp={Uri.EscapeDataString(isp.ToString())}";
}

static void ApplyAuthoritativeClientMetadata(string serverUrl, string clientId, bool isService, ClientRuntimeProfile runtimeProfile, ClientProxySettings proxySettings, ClientTransportState transportState, IspType effectiveIsp, string? effectiveName, string? effectiveProxyMode, string? effectiveProxyUrl)
{
    var metadataChanged = false;
    if (!string.IsNullOrWhiteSpace(effectiveName))
    {
        metadataChanged = runtimeProfile.Update(effectiveIsp, effectiveName);
        if (metadataChanged)
        {
            Console.WriteLine($"Server metadata updated: ISP={runtimeProfile.Isp}, Name={runtimeProfile.Name}");
            if (ShouldBackwriteServiceArguments(isService) && !string.IsNullOrWhiteSpace(clientId))
            {
                TryUpdateServiceArguments(serverUrl, clientId, runtimeProfile.Isp, runtimeProfile.Name);
            }
        }
    }

    if (proxySettings.Update(effectiveProxyMode, effectiveProxyUrl))
    {
        transportState.RecreateHttpClient();
        Console.WriteLine($"Server proxy config updated: Mode={proxySettings.Mode}, Url={proxySettings.Url}");
    }
}

static bool ShouldBackwriteServiceArguments(bool isService)
{
    return isService || HasManagedServiceInstall();
}

static bool HasManagedServiceInstall()
{
    try
    {
        if (OperatingSystem.IsWindows())
        {
            // 检测 NSSM 方式安装
            var installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CfSpeedtestClient");
            if (File.Exists(Path.Combine(installDir, "nssm", "nssm.exe")))
                return true;

            // 检测 sc.exe 原生服务注册
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = "query CfSpeedtestClient",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit();
                    return proc.ExitCode == 0;
                }
            }
            catch { }

            return false;
        }

        if (OperatingSystem.IsLinux())
        {
            return File.Exists("/etc/systemd/system/cfspeedtest-client.service")
                || File.Exists("/etc/init.d/cfspeedtest-client");
        }

        if (OperatingSystem.IsMacOS())
        {
            return File.Exists("/Library/LaunchDaemons/uk.greepar.cfspeedtest.client.plist");
        }
    }
    catch
    {
        // ignored
    }

    return false;
}

static async Task WaitForFetchSignalAsync(SemaphoreSlim immediateFetchSignal)
{
    await immediateFetchSignal.WaitAsync();
    while (immediateFetchSignal.CurrentCount > 0)
    {
        await immediateFetchSignal.WaitAsync();
    }
    Console.WriteLine("Server test trigger received. Fetching task immediately...");
}

static async Task WaitForRetryOrFetchSignalAsync(TimeSpan delay, SemaphoreSlim immediateFetchSignal)
{
    var delayTask = Task.Delay(delay);
    var signalTask = immediateFetchSignal.WaitAsync();
    var completed = await Task.WhenAny(delayTask, signalTask);
    if (completed == signalTask)
    {
        while (immediateFetchSignal.CurrentCount > 0)
        {
            await immediateFetchSignal.WaitAsync();
        }
        Console.WriteLine("Server test trigger received during retry wait. Fetching task immediately...");
    }
}

// ============================================================
//  TCP 延迟与丢包测试
// ============================================================
static async Task TestTcpAsync(IpTestResult result, string ip, int port, int durationSeconds)
{
    var latencies = new List<double>();
    int total = 0, success = 0;
    var deadline = Stopwatch.StartNew();

    while (deadline.Elapsed.TotalSeconds < durationSeconds)
    {
        total++;
        var sw = Stopwatch.StartNew();
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.ReceiveTimeout = 3000;
            socket.SendTimeout = 3000;

            var cts = new CancellationTokenSource(3000);
            await socket.ConnectAsync(IPAddress.Parse(ip), port, cts.Token);
            sw.Stop();

            success++;
            latencies.Add(sw.Elapsed.TotalMilliseconds);

            try { socket.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
        }
        catch
        {
            sw.Stop();
            // 连接失败算丢包
        }

        // 短暂间隔避免过于密集
        await Task.Delay(50);
    }

    result.TcpTotalCount = total;
    result.TcpSuccessCount = success;
    result.PacketLossRate = total > 0 ? 1.0 - (double)success / total : 1.0;

    if (latencies.Count > 0)
    {
        result.AvgLatencyMs = latencies.Average();
        result.MinLatencyMs = latencies.Min();
    }
    else
    {
        result.AvgLatencyMs = 9999;
        result.MinLatencyMs = 9999;
    }
}

// ============================================================
//  HTTPS 下载速度测试
// ============================================================
static async Task TestDownloadAsync(IpTestResult result, string ip, string urlTemplate,
    string host, int port, int durationSeconds, double maxDownloadSpeedKBps, ClientProxySettings proxySettings)
{
    try
    {
        // 构造handler:将请求发送到指定IP但使用正确的SNI
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Parse(ip), port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            },
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        };

        if (string.Equals(proxySettings.Mode, "system", StringComparison.OrdinalIgnoreCase))
        {
            handler.UseProxy = true;
        }
        else if (string.Equals(proxySettings.Mode, "custom", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(proxySettings.Url))
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(proxySettings.Url);
        }
        else
        {
            handler.UseProxy = false;
            handler.Proxy = null;
        }

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(durationSeconds + 10) };
        client.DefaultRequestHeaders.Host = host;

        // 这里故意把 {ip} 替换为 host。
        // 真正的 TCP 连接会被 ConnectCallback 强制连到指定 ip，
        // 但 HTTPS / SNI / Host 必须仍然使用真实域名，否则证书和路由会异常。
        var url = urlTemplate.Replace("{ip}", host);

        // 如果URL模板本身不是完整URL, 构建默认的
        if (!url.StartsWith("http"))
            url = $"https://{host}/__down?bytes=104857600";

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds + 5));
        var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();

        using var dlStream = await response.Content.ReadAsStreamAsync(cts.Token);
        var buffer = new byte[65536];
        long totalBytes = 0;
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed.TotalSeconds < durationSeconds)
        {
            var read = await dlStream.ReadAsync(buffer, cts.Token);
            if (read == 0) break;
            totalBytes += read;

            if (maxDownloadSpeedKBps > 0)
            {
                var elapsedSeconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                var actualKBps = totalBytes / 1024.0 / elapsedSeconds;
                if (actualKBps > maxDownloadSpeedKBps)
                {
                    var targetSeconds = (totalBytes / 1024.0) / maxDownloadSpeedKBps;
                    var delayMs = (int)Math.Ceiling((targetSeconds - elapsedSeconds) * 1000);
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, cts.Token);
                    }
                }
            }
        }

        sw.Stop();
        if (sw.Elapsed.TotalSeconds > 0)
            result.DownloadSpeedKBps = totalBytes / 1024.0 / sw.Elapsed.TotalSeconds;
    }
    catch (Exception ex)
    {
        result.DownloadSpeedKBps = 0;
        var msg = ex.Message;
        if (msg.Length > 30) msg = msg[..30];
        Console.Write($"[DL ERR: {msg}] ");
    }
}

// ============================================================
//  参数解析帮助函数
// ============================================================
static string GetArg(string[] args, string key, string defaultValue)
{
    var normalizedKey = NormalizeArgKey(key);
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (NormalizeArgKey(args[i]).Equals(normalizedKey, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return defaultValue;
}

static bool HasFlag(string[] args, string flag)
{
    var normalizedFlag = NormalizeArgKey(flag);
    for (int i = 0; i < args.Length; i++)
    {
        if (NormalizeArgKey(args[i]).Equals(normalizedFlag, StringComparison.OrdinalIgnoreCase))
            return true;
    }
    return false;
}

static bool HasArgValue(string[] args, string key)
{
    var normalizedKey = NormalizeArgKey(key);
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (NormalizeArgKey(args[i]).Equals(normalizedKey, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(args[i + 1]) &&
            !args[i + 1].StartsWith('-'))
        {
            return true;
        }
    }
    return false;
}

static bool ValidateRequiredArgs(string[] args, bool installing)
{
    if (!HasArgValue(args, "server"))
    {
        Console.WriteLine(installing
            ? "Missing required option for install: --server <url>"
            : "Missing required option: --server <url>");
        Console.WriteLine();
        PrintHelp();
        return false;
    }

    if (HasArgValue(args, "isp"))
    {
        var isp = GetArg(args, "isp", string.Empty);
        if (!Enum.TryParse<IspType>(isp, true, out _))
        {
            Console.WriteLine($"Invalid ISP: {isp}, options: Telecom, Unicom, Mobile");
            Console.WriteLine();
            PrintHelp();
            return false;
        }
    }

    return true;
}

static void PrintHelp()
{
    Console.WriteLine("=== Cloudflare IP SpeedTest Client ===");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  CfSpeedtest.Client --server <url> [options]");
    Console.WriteLine("  CfSpeedtest.Client --install --server <url> [options]");
    Console.WriteLine("  CfSpeedtest.Client --uninstall");
    Console.WriteLine();
    Console.WriteLine("Required:");
    Console.WriteLine("  --server <url>              Server URL, e.g. http://127.0.0.1:5000");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --client-id <id>            Existing or reserved client id (optional)");
    Console.WriteLine("  --isp <Telecom|Unicom|Mobile>  ISP, default: Telecom");
    Console.WriteLine("  --name <name>               Client display name, default: machine name");
    Console.WriteLine("  --interval <minutes>        Default local interval, default: 60");
    Console.WriteLine("  --once                      Run one test cycle after server trigger and exit");
    Console.WriteLine("  --disable-auto-update       Disable client auto update");
    Console.WriteLine("  --service                   Internal flag for service mode");
    Console.WriteLine("  --install                   Install and start native OS service");
    Console.WriteLine("  --uninstall                 Stop and remove native OS service");
    Console.WriteLine("  --help                      Show this help");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  CfSpeedtest.Client --server http://127.0.0.1:5000 --isp Telecom --name node-1");
    Console.WriteLine("  CfSpeedtest.Client --install --server http://127.0.0.1:5000 --client-id abc --isp Unicom --name node-2");
    Console.WriteLine("  CfSpeedtest.Client --uninstall");
}

static string NormalizeArgKey(string value)
{
    return value.Trim().TrimStart('-').TrimStart('-');
}

static string QuoteArg(string value)
{
    if (string.IsNullOrEmpty(value)) return "\"\"";
    if (!value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
    return "\"" + value.Replace("\"", "\\\"") + "\"";
}

static void TryUpdateServiceArguments(string serverUrl, string clientId, IspType isp, string clientName)
{
    try
    {
        if (OperatingSystem.IsWindows())
        {
            var installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CfSpeedtestClient");
            var nssmExe = Path.Combine(installDir, "nssm", "nssm.exe");
            var exePath = Path.Combine(installDir, "CfSpeedtest.Client.exe");
            if (File.Exists(nssmExe) && File.Exists(exePath))
            {
                var args = $"--server \"{serverUrl}\" --client-id {clientId} --isp {isp} --name \"{clientName}\" --service";
                Process.Start(new ProcessStartInfo
                {
                    FileName = nssmExe,
                    Arguments = $"set CfSpeedtestClient Application {exePath}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })?.WaitForExit();
                Process.Start(new ProcessStartInfo
                {
                    FileName = nssmExe,
                    Arguments = $"set CfSpeedtestClient AppParameters {args}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })?.WaitForExit();
            }
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            var serviceName = "cfspeedtest-client";
            var serviceFile = $"/etc/systemd/system/{serviceName}.service";
            var openRcFile = $"/etc/init.d/{serviceName}";
            var installDir = "/opt/cfspeedtest-client";
            var execLine = $"ExecStart={installDir}/CfSpeedtest.Client --server {serverUrl} --client-id {clientId} --isp {isp} --name \"{clientName}\" --service";

            if (File.Exists(serviceFile))
            {
                var lines = File.ReadAllLines(serviceFile)
                    .Select(line => line.StartsWith("ExecStart=") ? execLine : line)
                    .ToArray();
                File.WriteAllLines(serviceFile, lines);
                Process.Start("systemctl", "daemon-reload")?.WaitForExit();
                return;
            }

            if (File.Exists(openRcFile))
            {
                var content = File.ReadAllText(openRcFile);
                if (content.Contains("procd_set_param command "))
                {
                    var procdLines = File.ReadAllLines(openRcFile)
                        .Select(line => line.Contains("procd_set_param command ")
                            ? $"  procd_set_param command {installDir}/CfSpeedtest.Client --server {serverUrl} --client-id {clientId} --isp {isp} --name {clientName} --service"
                            : line)
                        .ToArray();
                    File.WriteAllLines(openRcFile, procdLines);
                    return;
                }

                var lines = File.ReadAllLines(openRcFile)
                    .Select(line => line.StartsWith("command_args=")
                        ? $"command_args=\"--server {serverUrl} --client-id {clientId} --isp {isp} --name {clientName} --service\""
                        : line)
                    .ToArray();
                File.WriteAllLines(openRcFile, lines);
                return;
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            var plistName = "uk.greepar.cfspeedtest.client";
            var plistPath = $"/Library/LaunchDaemons/{plistName}.plist";
            var installDir = "/usr/local/cfspeedtest-client";
            if (File.Exists(plistPath))
            {
                var args = new[]
                {
                    $"    <string>{installDir}/CfSpeedtest.Client</string>",
                    "    <string>--server</string>",
                    $"    <string>{serverUrl}</string>",
                    "    <string>--client-id</string>",
                    $"    <string>{clientId}</string>",
                    "    <string>--isp</string>",
                    $"    <string>{isp}</string>",
                    "    <string>--name</string>",
                    $"    <string>{clientName}</string>",
                    "    <string>--service</string>"
                };
                var lines = File.ReadAllLines(plistPath).ToList();
                var start = lines.FindIndex(l => l.Contains("<key>ProgramArguments</key>"));
                if (start >= 0)
                {
                    var arrayStart = start + 2;
                    var arrayEnd = lines.FindIndex(arrayStart, l => l.Contains("</array>"));
                    if (arrayEnd > arrayStart)
                    {
                        lines.RemoveRange(arrayStart, arrayEnd - arrayStart);
                        lines.InsertRange(arrayStart, args);
                        File.WriteAllLines(plistPath, lines);
                    }
                }
            }
        }
    }
    catch
    {
        // ignore service config update failures
    }
}

static void CleanupOldFiles()
{
    try
    {
        var baseDir = AppContext.BaseDirectory;
        TryDeleteFile(Path.Combine(baseDir, "client-state.json"));
        foreach (var file in Directory.GetFiles(baseDir, "*.bak", SearchOption.TopDirectoryOnly))
        {
            TryDeleteFile(file);
        }

        var updateTemp = Path.Combine(baseDir, "UpdateTemp");
        TryDeleteDirectory(updateTemp);

        var tempDir = Path.GetTempPath();
        foreach (var bat in Directory.GetFiles(tempDir, "cfspeedtest-restart-*.bat", SearchOption.TopDirectoryOnly))
        {
            TryDeleteFile(bat);
        }
    }
    catch
    {
        // ignored
    }
}

static async Task ApplyUpdateInPlaceAsync(string stagingDir, string targetDir)
{
    foreach (var sourceFile in Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories))
    {
        var relativePath = Path.GetRelativePath(stagingDir, sourceFile);
        var destinationFile = Path.Combine(targetDir, relativePath);
        var destinationDir = Path.GetDirectoryName(destinationFile)!;
        Directory.CreateDirectory(destinationDir);

        if (File.Exists(destinationFile))
        {
            var bakFile = destinationFile + ".bak";
            TryDeleteFile(bakFile);
            File.Move(destinationFile, bakFile);
        }

        File.Move(sourceFile, destinationFile);
    }

    await Task.CompletedTask;
}

static void ScheduleWindowsServiceUpdate(string stagingDir, string targetDir)
{
    var scriptPath = Path.Combine(Path.GetTempPath(), $"cfspeedtest-service-update-{Guid.NewGuid():N}.ps1");
    var script = """
        param(
            [string]$ServiceName,
            [string]$StagingDir,
            [string]$TargetDir,
            [string]$ScriptPath
        )

        $ErrorActionPreference = 'Stop'
        Start-Sleep -Seconds 2

        try { sc.exe stop $ServiceName | Out-Null } catch { }

        $deadline = (Get-Date).AddSeconds(60)
        do {
            Start-Sleep -Seconds 1
            $status = sc.exe query $ServiceName | Out-String
            if ($status -match 'STATE\s+:\s+\d+\s+STOPPED') { break }
        } while ((Get-Date) -lt $deadline)

        Get-ChildItem -LiteralPath $StagingDir -Recurse -File | ForEach-Object {
            $relative = $_.FullName.Substring($StagingDir.Length).TrimStart('\', '/')
            $destination = Join-Path $TargetDir $relative
            $destinationDir = Split-Path -Parent $destination
            if (-not (Test-Path -LiteralPath $destinationDir)) {
                New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null
            }
            Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
        }

        Remove-Item -LiteralPath $StagingDir -Recurse -Force -ErrorAction SilentlyContinue
        sc.exe start $ServiceName | Out-Null
        Start-Sleep -Seconds 3
        Remove-Item -LiteralPath $ScriptPath -Force -ErrorAction SilentlyContinue
        """;

    File.WriteAllText(scriptPath, script, Encoding.UTF8);

    var psi = new ProcessStartInfo
    {
        FileName = "powershell.exe",
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    psi.ArgumentList.Add("-NoProfile");
    psi.ArgumentList.Add("-ExecutionPolicy");
    psi.ArgumentList.Add("Bypass");
    psi.ArgumentList.Add("-File");
    psi.ArgumentList.Add(scriptPath);
    psi.ArgumentList.Add("-ServiceName");
    psi.ArgumentList.Add("CfSpeedtestClient");
    psi.ArgumentList.Add("-StagingDir");
    psi.ArgumentList.Add(stagingDir);
    psi.ArgumentList.Add("-TargetDir");
    psi.ArgumentList.Add(targetDir);
    psi.ArgumentList.Add("-ScriptPath");
    psi.ArgumentList.Add(scriptPath);

    Process.Start(psi);
}

static void TryDeleteDirectory(string? path)
{
    try
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            Directory.Delete(path, true);
    }
    catch { }
}

static void TryDeleteFile(string? path)
{
    try
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            File.Delete(path);
    }
    catch { }
}

static async Task<string?> ReceiveWebSocketTextMessageAsync(WebSocket ws, byte[] buffer, CancellationToken cancellationToken)
{
    using var ms = new MemoryStream();
    while (true)
    {
        var result = await ws.ReceiveAsync(buffer, cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close)
            return null;

        ms.Write(buffer, 0, result.Count);
        if (result.EndOfMessage)
            break;
    }

    return Encoding.UTF8.GetString(ms.ToArray());
}

sealed class ClientRuntimeProfile
{
    private readonly Lock _lock = new();
    private IspType _isp;
    private string _name;

    public ClientRuntimeProfile(IspType isp, string name)
    {
        _isp = isp;
        _name = name;
    }

    public IspType Isp
    {
        get
        {
            lock (_lock) return _isp;
        }
    }

    public string Name
    {
        get
        {
            lock (_lock) return _name;
        }
    }

    public (IspType Isp, string Name) GetSnapshot()
    {
        lock (_lock) return (_isp, _name);
    }

    public bool Update(IspType isp, string name)
    {
        lock (_lock)
        {
            if (_isp == isp && string.Equals(_name, name, StringComparison.Ordinal))
            {
                return false;
            }

            _isp = isp;
            _name = name;
            return true;
        }
    }
}

sealed class ClientRuntimeState
{
    private readonly object _lock = new();
    private string _status = "等待触发";
    private int _totalIps;
    private int _testedIps;
    private DateTime? _startedAt;
    private readonly Queue<string> _logLines = new();
    private const int MaxLogLines = 80;
    private const int MaxLogChars = 4000;

    public string Status
    {
        get { lock (_lock) return _status; }
    }

    public int TotalIps
    {
        get { lock (_lock) return _totalIps; }
    }

    public int TestedIps
    {
        get { lock (_lock) return _testedIps; }
    }

    public DateTime? StartedAt
    {
        get { lock (_lock) return _startedAt; }
    }

    public string LogText
    {
        get
        {
            lock (_lock)
            {
                var text = string.Join("\n", _logLines);
                if (text.Length <= MaxLogChars) return text;
                return text[^MaxLogChars..];
            }
        }
    }

    public void SetWaiting()
    {
        lock (_lock)
        {
            _status = "等待触发";
            _totalIps = 0;
            _testedIps = 0;
            _startedAt = null;
        }
    }

    public void SetTesting(int totalIps, int testedIps)
    {
        lock (_lock)
        {
            _status = "正在测速";
            _totalIps = totalIps;
            _testedIps = testedIps;
            _startedAt ??= DateTime.UtcNow;
        }
    }

    public void SetCompleted(int totalIps, int resultCount)
    {
        lock (_lock)
        {
            _status = $"已完成测速（保留 {resultCount} 个结果）";
            _totalIps = totalIps;
            _testedIps = totalIps;
        }
    }

    public void AppendLog(string line)
    {
        lock (_lock)
        {
            _logLines.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}");
            while (_logLines.Count > MaxLogLines)
            {
                _logLines.Dequeue();
            }
        }
    }
}

sealed class ClientProxySettings
{
    private readonly object _lock = new();
    private string _mode = "direct";
    private string _url = string.Empty;

    public string Mode { get { lock (_lock) return _mode; } }
    public string Url { get { lock (_lock) return _url; } }

    public bool Update(string? mode, string? url)
    {
        var newMode = string.IsNullOrWhiteSpace(mode) ? "direct" : mode.Trim().ToLowerInvariant();
        if (newMode != "direct" && newMode != "system" && newMode != "custom")
            newMode = "direct";
        var newUrl = newMode == "custom" ? (url ?? string.Empty).Trim() : string.Empty;

        lock (_lock)
        {
            if (_mode == newMode && _url == newUrl)
                return false;
            _mode = newMode;
            _url = newUrl;
            return true;
        }
    }
}

sealed class ClientTransportState : IDisposable
{
    private readonly object _lock = new();
    private readonly ClientProxySettings _proxySettings;
    private HttpClient _httpClient;

    public ClientTransportState(ClientProxySettings proxySettings)
    {
        _proxySettings = proxySettings;
        _httpClient = BuildHttpClient();
    }

    public HttpClient HttpClient
    {
        get { lock (_lock) return _httpClient; }
    }

    public ClientProxySettings ProxySettings => _proxySettings;

    public void RecreateHttpClient()
    {
        lock (_lock)
        {
            var old = _httpClient;
            _httpClient = BuildHttpClient();
            old.Dispose();
        }
    }

    private HttpClient BuildHttpClient()
    {
        var handler = new HttpClientHandler();
        if (string.Equals(_proxySettings.Mode, "system", StringComparison.OrdinalIgnoreCase))
        {
            handler.UseProxy = true;
        }
        else if (string.Equals(_proxySettings.Mode, "custom", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_proxySettings.Url))
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(_proxySettings.Url);
        }
        else
        {
            handler.UseProxy = false;
            handler.Proxy = null;
        }

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _httpClient.Dispose();
        }
    }
}

// ============================================================
//  服务安装/卸载 (跨平台)
// ============================================================
static class ServiceInstaller
{
    private const string ServiceName = "CfSpeedtestClient";
    private const string DisplayName = "CfSpeedtest Client";
    private const string Description = "Cloudflare IP SpeedTest Client Service";

    public static int Install(string[] args)
    {
        if (OperatingSystem.IsWindows())
            return InstallWindows(args);
        if (OperatingSystem.IsLinux())
            return InstallLinux(args);
        if (OperatingSystem.IsMacOS())
            return InstallMacOS(args);

        Console.WriteLine("Unsupported platform for service installation.");
        return 1;
    }

    public static int Uninstall()
    {
        if (OperatingSystem.IsWindows())
            return UninstallWindows();
        if (OperatingSystem.IsLinux())
            return UninstallLinux();
        if (OperatingSystem.IsMacOS())
            return UninstallMacOS();

        Console.WriteLine("Unsupported platform for service uninstallation.");
        return 1;
    }

    // ---- Windows: sc.exe ----

    private static int InstallWindows(string[] args)
    {
        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath))
        {
            Console.WriteLine("Error: Cannot determine executable path.");
            return 1;
        }

        // 构建服务运行时参数（排除 --install 和 --uninstall）
        var serviceArgs = BuildServiceArgs(args);
        var binPath = string.IsNullOrWhiteSpace(serviceArgs)
            ? $"\"{exePath}\" --service"
            : $"\"{exePath}\" {serviceArgs} --service";

        Console.WriteLine($"Installing Windows service '{ServiceName}'...");

        var exists = RunProcess("sc.exe", "query", ServiceName) == 0;
        var result = exists
            ? RunProcess("sc.exe", "config", ServiceName, "binPath=", binPath, "start=", "auto", "DisplayName=", DisplayName)
            : RunProcess("sc.exe", "create", ServiceName, "binPath=", binPath, "start=", "auto", "DisplayName=", DisplayName);
        if (result != 0)
        {
            Console.WriteLine(exists
                ? "Failed to update service. Make sure you run as Administrator."
                : "Failed to create service. Make sure you run as Administrator.");
            return 1;
        }

        // 设置描述
        RunProcess("sc.exe", "description", ServiceName, Description);

        // 设置失败后自动重启
        RunProcess("sc.exe", "failure", ServiceName, "reset=", "60", "actions=", "restart/5000/restart/10000/restart/30000");

        // 启动服务
        Console.WriteLine("Starting service...");
        result = RunProcess("sc.exe", "start", ServiceName);
        if (result != 0)
        {
            Console.WriteLine("Service created but failed to start. You can start it manually with: sc start CfSpeedtestClient");
        }
        else
        {
            Console.WriteLine("Service installed and started successfully.");
        }

        return 0;
    }

    private static int UninstallWindows()
    {
        Console.WriteLine($"Stopping Windows service '{ServiceName}'...");
        RunProcess("sc.exe", "stop", ServiceName);

        // 等待服务停止
        Thread.Sleep(2000);

        Console.WriteLine($"Removing Windows service '{ServiceName}'...");
        var result = RunProcess("sc.exe", "delete", ServiceName);
        if (result != 0)
        {
            Console.WriteLine("Failed to remove service. Make sure you run as Administrator.");
            return 1;
        }

        Console.WriteLine("Service uninstalled successfully.");
        return 0;
    }

    // ---- Linux: systemd ----

    private static int InstallLinux(string[] args)
    {
        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath))
        {
            Console.WriteLine("Error: Cannot determine executable path.");
            return 1;
        }

        var serviceArgs = BuildServiceArgs(args);
        var serviceName = "cfspeedtest-client";
        var unitPath = $"/etc/systemd/system/{serviceName}.service";

        var unitContent = $"""
            [Unit]
            Description={Description}
            After=network-online.target
            Wants=network-online.target

            [Service]
            Type=simple
            ExecStart={QuoteSystemdArg(exePath)} {serviceArgs} --service
            Restart=always
            RestartSec=5
            WorkingDirectory={Path.GetDirectoryName(exePath)}

            [Install]
            WantedBy=multi-user.target
            """;

        Console.WriteLine($"Installing systemd service '{serviceName}'...");

        try
        {
            File.WriteAllText(unitPath, unitContent);
        }
        catch (UnauthorizedAccessException)
        {
            Console.WriteLine("Error: Permission denied. Run with sudo.");
            return 1;
        }

        RunProcess("systemctl", "daemon-reload");
        RunProcess("systemctl", $"enable {serviceName}");

        Console.WriteLine("Starting service...");
        var result = RunProcess("systemctl", $"start {serviceName}");
        if (result != 0)
        {
            Console.WriteLine("Service installed but failed to start. Check: systemctl status cfspeedtest-client");
        }
        else
        {
            Console.WriteLine("Service installed and started successfully.");
        }

        return 0;
    }

    private static int UninstallLinux()
    {
        var serviceName = "cfspeedtest-client";
        var unitPath = $"/etc/systemd/system/{serviceName}.service";

        Console.WriteLine($"Stopping systemd service '{serviceName}'...");
        RunProcess("systemctl", $"stop {serviceName}");
        RunProcess("systemctl", $"disable {serviceName}");

        if (File.Exists(unitPath))
        {
            try
            {
                File.Delete(unitPath);
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("Error: Permission denied. Run with sudo.");
                return 1;
            }
        }

        RunProcess("systemctl", "daemon-reload");
        Console.WriteLine("Service uninstalled successfully.");
        return 0;
    }

    // ---- macOS: launchd ----

    private static int InstallMacOS(string[] args)
    {
        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath))
        {
            Console.WriteLine("Error: Cannot determine executable path.");
            return 1;
        }

        var serviceArgs = BuildServiceArgs(args);
        var plistLabel = "uk.greepar.cfspeedtest.client";
        var plistPath = $"/Library/LaunchDaemons/{plistLabel}.plist";

        // 构建 ProgramArguments 数组
        var programArgs = new List<string> { exePath };
        programArgs.AddRange(SplitServiceArgs(serviceArgs));
        programArgs.Add("--service");

        var argsXml = string.Join("\n", programArgs.Select(a => $"    <string>{EscapeXml(a)}</string>"));

        var plistContent = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key>
              <string>{plistLabel}</string>
              <key>ProgramArguments</key>
              <array>
            {argsXml}
              </array>
              <key>RunAtLoad</key>
              <true/>
              <key>KeepAlive</key>
              <true/>
              <key>WorkingDirectory</key>
              <string>{Path.GetDirectoryName(exePath)}</string>
              <key>StandardOutPath</key>
              <string>/tmp/cfspeedtest-client.log</string>
              <key>StandardErrorPath</key>
              <string>/tmp/cfspeedtest-client.err</string>
            </dict>
            </plist>
            """;

        Console.WriteLine($"Installing launchd service '{plistLabel}'...");

        try
        {
            File.WriteAllText(plistPath, plistContent);
        }
        catch (UnauthorizedAccessException)
        {
            Console.WriteLine("Error: Permission denied. Run with sudo.");
            return 1;
        }

        Console.WriteLine("Loading service...");
        var result = RunProcess("launchctl", $"load {plistPath}");
        if (result != 0)
        {
            Console.WriteLine("Service installed but failed to load. Check: sudo launchctl list | grep cfspeedtest");
        }
        else
        {
            Console.WriteLine("Service installed and started successfully.");
        }

        return 0;
    }

    private static int UninstallMacOS()
    {
        var plistLabel = "uk.greepar.cfspeedtest.client";
        var plistPath = $"/Library/LaunchDaemons/{plistLabel}.plist";

        Console.WriteLine($"Unloading launchd service '{plistLabel}'...");
        RunProcess("launchctl", $"unload {plistPath}");

        if (File.Exists(plistPath))
        {
            try
            {
                File.Delete(plistPath);
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("Error: Permission denied. Run with sudo.");
                return 1;
            }
        }

        Console.WriteLine("Service uninstalled successfully.");
        return 0;
    }

    // ---- Helpers ----

    private static string BuildServiceArgs(string[] args)
    {
        // 保留除 --install / --uninstall / --service 之外的所有参数
        var filtered = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            var normalized = args[i].Trim().TrimStart('-');
            if (normalized.Equals("install", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("uninstall", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("service", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            filtered.Add(args[i]);
        }

        return string.Join(" ", filtered.Select(QuoteArgIfNeeded));
    }

    private static List<string> SplitServiceArgs(string serviceArgs)
    {
        // 简单拆分，保留引号内的内容
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        foreach (var ch in serviceArgs)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (ch == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }

    private static string QuoteArgIfNeeded(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        if (!value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static string QuoteSystemdArg(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        if (!value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string EscapeXml(string value)
    {
        return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private static int RunProcess(string fileName, params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            foreach (var argument in arguments)
                psi.ArgumentList.Add(argument);

            using var proc = Process.Start(psi);
            if (proc == null) return -1;

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (!string.IsNullOrWhiteSpace(stdout))
                Console.WriteLine(stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(stderr) && proc.ExitCode != 0)
                Console.WriteLine($"  [stderr] {stderr.TrimEnd()}");

            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error running {fileName}: {ex.Message}");
            return -1;
        }
    }

    private static int RunProcess(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return -1;

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (!string.IsNullOrWhiteSpace(stdout))
                Console.WriteLine(stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(stderr) && proc.ExitCode != 0)
                Console.WriteLine($"  [stderr] {stderr.TrimEnd()}");

            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error running {fileName}: {ex.Message}");
            return -1;
        }
    }
}

// ============================================================
//  Windows Service wrapper
// ============================================================
static class WindowsServiceWrapper
{
    private const string ServiceName = "CfSpeedtestClient";
    private const int SERVICE_WIN32_OWN_PROCESS = 0x00000010;
    private const int SERVICE_STOPPED = 0x00000001;
    private const int SERVICE_START_PENDING = 0x00000002;
    private const int SERVICE_STOP_PENDING = 0x00000003;
    private const int SERVICE_RUNNING = 0x00000004;
    private const int SERVICE_ACCEPT_STOP = 0x00000001;
    private const int SERVICE_ACCEPT_SHUTDOWN = 0x00000004;
    private const int SERVICE_CONTROL_STOP = 0x00000001;
    private const int SERVICE_CONTROL_SHUTDOWN = 0x00000005;

    private static string[] _args = [];
    private static IntPtr _statusHandle;
    private static Process? _worker;
    private static ServiceMainDelegate? _serviceMain;
    private static ServiceControlHandlerEx? _controlHandler;

    public static int Run(string[] args)
    {
        _args = args;
        _serviceMain = ServiceMain;

        var serviceTable = new SERVICE_TABLE_ENTRY[2];
        serviceTable[0] = new SERVICE_TABLE_ENTRY
        {
            lpServiceName = ServiceName,
            lpServiceProc = _serviceMain,
        };

        if (!StartServiceCtrlDispatcher(serviceTable))
        {
            Console.WriteLine($"StartServiceCtrlDispatcher failed: {Marshal.GetLastWin32Error()}");
            return 1;
        }

        return 0;
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _controlHandler = ControlHandler;
        _statusHandle = RegisterServiceCtrlHandlerEx(ServiceName, _controlHandler, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
            return;

        SetStatus(SERVICE_START_PENDING, 0, 30000);

        try
        {
            StartWorkerProcess();
            SetStatus(SERVICE_RUNNING, SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN, 0);
            _worker?.WaitForExit();
        }
        catch
        {
            // 服务入口不能把异常抛回 SCM，否则服务状态会残留在启动中。
        }
        finally
        {
            SetStatus(SERVICE_STOPPED, 0, 0);
        }
    }

    private static int ControlHandler(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        if (control == SERVICE_CONTROL_STOP || control == SERVICE_CONTROL_SHUTDOWN)
        {
            SetStatus(SERVICE_STOP_PENDING, 0, 30000);
            StopWorkerProcess();
        }

        return 0;
    }

    private static void StartWorkerProcess()
    {
        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(exePath))
            throw new InvalidOperationException("Cannot determine executable path.");

        var workerArgs = new List<string>();
        var replaced = false;
        foreach (var arg in _args)
        {
            if (NormalizeWorkerArgKey(arg).Equals("service", StringComparison.OrdinalIgnoreCase))
            {
                workerArgs.Add("--service-worker");
                replaced = true;
                continue;
            }
            workerArgs.Add(arg);
        }
        if (!replaced)
            workerArgs.Add("--service-worker");

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
        };
        foreach (var arg in workerArgs)
            psi.ArgumentList.Add(arg);

        _worker = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start service worker process.");
    }

    private static string NormalizeWorkerArgKey(string value)
    {
        return value.Trim().TrimStart('-').TrimStart('-');
    }

    private static void StopWorkerProcess()
    {
        try
        {
            if (_worker is { HasExited: false })
            {
                _worker.Kill(entireProcessTree: true);
                _worker.WaitForExit(10000);
            }
        }
        catch
        {
            // ignore stop cleanup failures
        }
    }

    private static void SetStatus(int state, int controlsAccepted, int waitHint)
    {
        if (_statusHandle == IntPtr.Zero)
            return;

        var status = new SERVICE_STATUS
        {
            dwServiceType = SERVICE_WIN32_OWN_PROCESS,
            dwCurrentState = state,
            dwControlsAccepted = controlsAccepted,
            dwWin32ExitCode = 0,
            dwServiceSpecificExitCode = 0,
            dwCheckPoint = state == SERVICE_START_PENDING || state == SERVICE_STOP_PENDING ? 1 : 0,
            dwWaitHint = waitHint,
        };
        SetServiceStatus(_statusHandle, ref status);
    }

    private delegate void ServiceMainDelegate(int argc, IntPtr argv);
    private delegate int ServiceControlHandlerEx(int control, int eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVICE_TABLE_ENTRY
    {
        public string? lpServiceName;
        public ServiceMainDelegate? lpServiceProc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public int dwServiceType;
        public int dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartServiceCtrlDispatcher([In] SERVICE_TABLE_ENTRY[] lpServiceStartTable);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string lpServiceName, ServiceControlHandlerEx lpHandlerProc, IntPtr lpContext);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr hServiceStatus, ref SERVICE_STATUS lpServiceStatus);
}

static class ClientUpdateLock
{
    public static readonly SemaphoreSlim Semaphore = new(1, 1);
}
