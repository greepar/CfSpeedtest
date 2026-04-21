using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CfSpeedtest.Shared;

// ============================================================
//  Cloudflare IP 测速客户端 (NativeAOT Compatible)
// ============================================================

Console.WriteLine("=== Cloudflare IP SpeedTest Client ===");
Console.WriteLine();

// 读取配置
var serverUrl = GetArg(args, "server", "http://127.0.0.1:5000");
var ispStr = GetArg(args, "isp", "Telecom");
var clientName = GetArg(args, "name", Environment.MachineName);
var intervalStr = GetArg(args, "interval", "60"); // 默认60分钟
var oneshot = HasFlag(args, "once");

if (!Enum.TryParse<IspType>(ispStr, true, out var isp))
{
    Console.WriteLine($"Invalid ISP: {ispStr}, options: Telecom, Unicom, Mobile");
    return 1;
}

var intervalMinutes = int.Parse(intervalStr);
Console.WriteLine($"Server:   {serverUrl}");
Console.WriteLine($"ISP:      {isp}");
Console.WriteLine($"Name:     {clientName}");
Console.WriteLine($"Default interval: {intervalMinutes}min");
Console.WriteLine();

using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var stateFile = Path.Combine(AppContext.BaseDirectory, "client-state.json");
var localState = LoadClientState(stateFile);
if (!string.IsNullOrWhiteSpace(localState.ClientId))
{
    Console.WriteLine($"Local clientId: {localState.ClientId}");
}

// NativeAOT-safe JSON options using source generators
var jsonOpts = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    TypeInfoResolverChain = { AppJsonContext.Default }
};

// ===== 注册客户端 =====
Console.WriteLine("[1] Registering with server...");
string clientId;
int heartbeatIntervalSeconds = 30;
try
{
    var regReq = new ClientRegisterRequest { ClientId = localState.ClientId, Isp = isp, Name = clientName };
    var regJson = JsonSerializer.Serialize(regReq, AppJsonContext.Default.ClientRegisterRequest);
    var regResp = await httpClient.PostAsync(
        $"{serverUrl}/api/client/register",
        new StringContent(regJson, Encoding.UTF8, "application/json"));
    regResp.EnsureSuccessStatusCode();
    var regBody = await regResp.Content.ReadAsStringAsync();
    var regResult = JsonSerializer.Deserialize(regBody, AppJsonContext.Default.ApiResponseClientRegisterResponse);
    if (regResult?.Success != true || regResult.Data is null)
    {
        Console.WriteLine($"Registration failed: {regResult?.Message}");
        return 1;
    }
    clientId = regResult.Data.ClientId;
    heartbeatIntervalSeconds = regResult.Data.HeartbeatIntervalSeconds > 0
        ? regResult.Data.HeartbeatIntervalSeconds
        : heartbeatIntervalSeconds;
    localState.ClientId = clientId;
    SaveClientState(stateFile, localState);
    Console.WriteLine($"Registered as: {clientId}");
}
catch (Exception ex)
{
    Console.WriteLine($"Failed to register: {ex.Message}");
    return 1;
}

using var heartbeatCts = new CancellationTokenSource();
var heartbeatTask = StartHeartbeatLoopAsync(serverUrl, clientId, isp, clientName, httpClient, heartbeatIntervalSeconds, heartbeatCts.Token);

// ===== 主循环 =====
while (true)
{
    Console.WriteLine();
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Fetching task...");

    try
    {
        var retryDelayMinutes = await RunTestCycleAsync(serverUrl, clientId, isp, httpClient, intervalMinutes);

        if (oneshot) break;

        if (retryDelayMinutes > 0)
        {
            Console.WriteLine($"Sleeping {retryDelayMinutes}min before next check...");
            await Task.Delay(TimeSpan.FromMinutes(retryDelayMinutes));
        }

        continue;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in test cycle: {ex.Message}");
    }

    if (oneshot) break;
    Console.WriteLine($"Sleeping {intervalMinutes}min before next check...");
    await Task.Delay(TimeSpan.FromMinutes(intervalMinutes));
}

heartbeatCts.Cancel();
try { await heartbeatTask; } catch { }
return 0;

// ============================================================
//  核心测速逻辑
// ============================================================
static async Task<int> RunTestCycleAsync(string serverUrl, string clientId, IspType isp,
    HttpClient httpClient, int fallbackIntervalMinutes)
{
    // 1. 获取任务
    var taskResp = await httpClient.GetAsync($"{serverUrl}/api/task/{clientId}");
    taskResp.EnsureSuccessStatusCode();
    var taskBody = await taskResp.Content.ReadAsStringAsync();
    var taskResult = JsonSerializer.Deserialize(taskBody, AppJsonContext.Default.ApiResponseSpeedTestTask);

    if (taskResult?.Success != true || taskResult.Data is null)
    {
        Console.WriteLine($"No task available: {taskResult?.Message}");
        return fallbackIntervalMinutes;
    }

    var task = taskResult.Data;
    Console.WriteLine($"Got task {task.TaskId[..8]}: {task.IpAddresses.Count} IPs to test");
    Console.WriteLine($"Test URL template: {task.TestUrl}");
    Console.WriteLine($"Host: {task.TestHost}, Port: {task.TestPort}");
    Console.WriteLine($"Server interval: {task.ClientIntervalMinutes}min");
    Console.WriteLine($"Scheduled start (UTC): {task.ScheduledAtUtc:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine();

    var wait = task.ScheduledAtUtc - DateTime.UtcNow;
    if (wait > TimeSpan.Zero)
    {
        Console.WriteLine($"Waiting {wait.TotalSeconds:F0}s for server-coordinated round start...");
        await Task.Delay(wait);
        Console.WriteLine();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Round started.");
    }

    var results = new List<IpTestResult>();

    // 2. 逐个IP测速
    for (int i = 0; i < task.IpAddresses.Count; i++)
    {
        var ip = task.IpAddresses[i];
        Console.Write($"  [{i + 1}/{task.IpAddresses.Count}] {ip,-16} ");

        var result = new IpTestResult { IpAddress = ip };

        // 2a. TCP 延迟/丢包测试
        await TestTcpAsync(result, ip, task.TestPort, task.TcpTestDurationSeconds);
        Console.Write($"TCP: {result.AvgLatencyMs,6:F1}ms loss:{result.PacketLossRate,5:P1} | ");

        // 如果丢包率太高,跳过下载测试
        if (result.PacketLossRate > 0.5)
        {
            Console.WriteLine("SKIP (high loss)");
            result.Score = 0;
            results.Add(result);
            continue;
        }

        // 2b. 下载速度测试
        await TestDownloadAsync(result, ip, task.TestUrl, task.TestHost, task.TestPort, task.DownloadDurationSeconds);
        Console.Write($"DL: {result.DownloadSpeedKBps,8:F1} KB/s | ");

        // 2c. 综合评分: 速度权重60%, 延迟权重25%, 丢包权重15%
        var speedScore = Math.Min(result.DownloadSpeedKBps / 1000.0, 100.0); // 归一化到0-100
        var latencyScore = Math.Max(0, 100.0 - result.AvgLatencyMs);         // 延迟越低越好
        var lossScore = (1.0 - result.PacketLossRate) * 100.0;               // 丢包越少越好
        result.Score = speedScore * 0.60 + latencyScore * 0.25 + lossScore * 0.15;

        Console.WriteLine($"Score: {result.Score:F1}");
        results.Add(result);
    }

    // 3. 排序取TopN
    var topResults = results
        .OrderByDescending(r => r.Score)
        .Take(task.TopN)
        .ToList();

    Console.WriteLine();
    Console.WriteLine($"=== Top {topResults.Count} Results ===");
    for (int i = 0; i < topResults.Count; i++)
    {
        var r = topResults[i];
        Console.WriteLine($"  #{i + 1} {r.IpAddress,-16} Speed:{r.DownloadSpeedKBps,8:F1} KB/s  " +
            $"Latency:{r.AvgLatencyMs,6:F1}ms  Loss:{r.PacketLossRate:P1}  Score:{r.Score:F1}");
    }

    // 4. 上报结果
    Console.WriteLine();
    Console.Write("Reporting results... ");
    var report = new SpeedTestReport
    {
        TaskId = task.TaskId,
        ClientId = clientId,
        Isp = isp,
        Results = topResults,
        CompletedAt = DateTime.UtcNow,
    };
    var reportJson = JsonSerializer.Serialize(report, AppJsonContext.Default.SpeedTestReport);
    var reportResp = await httpClient.PostAsync(
        $"{serverUrl}/api/report",
        new StringContent(reportJson, Encoding.UTF8, "application/json"));
    reportResp.EnsureSuccessStatusCode();
    Console.WriteLine("OK");

    return 0;
}

static Task StartHeartbeatLoopAsync(
    string serverUrl,
    string clientId,
    IspType isp,
    string clientName,
    HttpClient httpClient,
    int heartbeatIntervalSeconds,
    CancellationToken cancellationToken)
{
    return Task.Run(async () =>
    {
        var intervalSeconds = Math.Max(5, heartbeatIntervalSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                var req = new ClientHeartbeatRequest
                {
                    ClientId = clientId,
                    Isp = isp,
                    Name = clientName,
                };
                var json = JsonSerializer.Serialize(req, AppJsonContext.Default.ClientHeartbeatRequest);
                var resp = await httpClient.PostAsync(
                    $"{serverUrl}/api/client/heartbeat",
                    new StringContent(json, Encoding.UTF8, "application/json"),
                    cancellationToken);
                resp.EnsureSuccessStatusCode();

                var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize(body, AppJsonContext.Default.ApiResponseClientHeartbeatResponse);
                if (result?.Success == true && result.Data?.HeartbeatIntervalSeconds > 0)
                {
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
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            await StartHeartbeatLoopAsync(serverUrl, clientId, isp, clientName, httpClient, intervalSeconds, cancellationToken);
        }
    }, cancellationToken);
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
    string host, int port, int durationSeconds)
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

static ClientLocalState LoadClientState(string path)
{
    try
    {
        if (!File.Exists(path)) return new ClientLocalState();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, AppJsonContext.Default.ClientLocalState) ?? new ClientLocalState();
    }
    catch
    {
        return new ClientLocalState();
    }
}

static void SaveClientState(string path, ClientLocalState state)
{
    try
    {
        var json = JsonSerializer.Serialize(state, AppJsonContext.Default.ClientLocalState);
        File.WriteAllText(path, json);
    }
    catch
    {
        // 忽略本地持久化失败
    }
}
