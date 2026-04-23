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

Console.WriteLine("=== Cloudflare IP SpeedTest Client ===");
Console.WriteLine();

CleanupOldFiles();

// 读取配置
var serverUrl = GetArg(args, "server", "http://127.0.0.1:5000");
var explicitClientId = GetArg(args, "client-id", "");
var ispStr = GetArg(args, "isp", "Telecom");
var configuredClientName = GetArg(args, "name", Environment.MachineName);
var intervalStr = GetArg(args, "interval", "60"); // 默认60分钟
var autoUpdate = !HasFlag(args, "disable-auto-update");
var isService = HasFlag(args, "service");
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

var intervalMinutes = int.Parse(intervalStr);
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
    runtimeState.SetTesting(task.IpAddresses.Count, 0);

    while (pendingIps.Count > 0)
    {
        var ip = pendingIps.Dequeue();
        if (!testedIps.Add(ip))
            continue;

        runtimeState.SetTesting(task.IpAddresses.Count, testedIps.Count - 1);

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
            continue;
        }

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
        runtimeState.SetTesting(task.IpAddresses.Count, testedIps.Count);

        var qualifiedCount = allResults.Count(r => r.DownloadSpeedKBps >= task.MinDownloadSpeedKBps);
        if (qualifiedCount >= task.TopN)
            continue;

        if (pendingIps.Count == 0)
        {
            var additionalIps = await FetchAdditionalIpsAsync(serverUrl, clientId, runtimeProfile.Isp, testedIps, transportState.HttpClient);
            runtimeState.AppendLog($"Requested additional IP batch, received {additionalIps.Count} IP(s)");
            foreach (var extraIp in additionalIps)
            {
                if (!testedIps.Contains(extraIp))
                    pendingIps.Enqueue(extraIp);
            }
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
            .Take(1)
            .ToList();

    Console.WriteLine();
    if (qualifiedResults.Count > 0)
        Console.WriteLine($"=== Qualified Top {topResults.Count} Results ===");
    else
        Console.WriteLine($"=== No results met min speed {task.MinDownloadSpeedKBps:F1} KB/s, fallback to best 1 result ===");
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
    var reportResp = await transportState.HttpClient.PostAsync(
        $"{serverUrl}/api/report",
        new StringContent(reportJson, Encoding.UTF8, "application/json"));
    reportResp.EnsureSuccessStatusCode();
    Console.WriteLine("OK");
    runtimeState.SetCompleted(task.IpAddresses.Count, topResults.Count);
    runtimeState.AppendLog("Report completed successfully");
}

static async Task CheckForUpdateAsync(string serverUrl, string currentVersion, string clientPlatform, bool autoUpdate, bool isService, HttpClient httpClient)
{
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

        var tempFile = Path.Combine(Path.GetTempPath(), Path.GetFileName(new Uri(info.DownloadUrl).AbsolutePath));
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
        var stagingDir = Path.Combine(Path.GetTempPath(), $"cfspeedtest-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        Console.WriteLine("Extracting update package...");
        ZipFile.ExtractToDirectory(tempFile, stagingDir, true);

        Console.WriteLine("Applying update files...");
        await ApplyUpdateInPlaceAsync(stagingDir, targetDir);
        TryDeleteDirectory(stagingDir);
        TryDeleteFile(tempFile);

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
                Console.WriteLine("WebSocket failed repeatedly. Entering HTTP heartbeat fallback for 60 seconds.");
            }

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
                    if (nextIntervalSeconds != intervalSeconds)
                    {
                        intervalSeconds = nextIntervalSeconds;
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 心跳失败不打断主测速流程
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);
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
    try
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        var wsUrl = BuildWebSocketUrl(serverUrl, clientId, runtimeProfile.Isp);
        await ws.ConnectAsync(new Uri(wsUrl), cancellationToken);
        Console.WriteLine("WebSocket heartbeat connected.");

        var intervalSeconds = 30;
        var receiveTask = Task.Run(async () =>
        {
            var receiveBuffer = new byte[8192];
            while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var body = await ReceiveWebSocketTextMessageAsync(ws, receiveBuffer, cancellationToken);
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
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);
        }

        await receiveTask;

        Console.WriteLine("WebSocket heartbeat disconnected.");
        return 1;
    }
    catch (Exception ex)
    {
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
            var installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CfSpeedtestClient");
            return File.Exists(Path.Combine(installDir, "nssm", "nssm.exe"));
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
