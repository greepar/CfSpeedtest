using System.Text.Json;
using CfSpeedtest.Server.Services;
using CfSpeedtest.Shared;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.TypeInfoResolverChain.Add(AppJsonContext.Default);
});

builder.Services.AddSingleton<DataStore>();
builder.Services.AddSingleton<IpPoolService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IpPoolService>());
builder.Services.AddSingleton<DnsUpdateService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DnsUpdateService>());
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// ============================================================
//  API: 客户端注册
// ============================================================
app.MapPost("/api/client/register", (ClientRegisterRequest req, DataStore store) =>
{
    var clientId = req.ClientId;
    if (string.IsNullOrEmpty(clientId))
        clientId = Guid.NewGuid().ToString("N");

    var info = new ClientInfo
    {
        ClientId = clientId,
        Isp = req.Isp,
        Name = req.Name ?? $"{req.Isp}-{clientId[..6]}",
        RegisteredAt = DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow,
        IsOnline = true,
    };
    store.UpsertClient(info);

    return ApiResponse<ClientRegisterResponse>.Ok(new ClientRegisterResponse
    {
        ClientId = clientId,
        Success = true,
        Message = "Registered"
    });
});

// ============================================================
//  API: 客户端获取测速任务
// ============================================================
app.MapGet("/api/task/{clientId}", (string clientId, DataStore store, IpPoolService ipPool) =>
{
    var client = store.GetClient(clientId);
    if (client is null)
        return Results.Json(ApiResponse<SpeedTestTask>.Fail("Client not registered"));

    client.LastSeenAt = DateTime.UtcNow;
    client.IsOnline = true;
    store.UpsertClient(client);

    var config = store.GetConfig();
    var ips = ipPool.GetBatch(client.Isp.ToString());

    if (ips.Count == 0)
        return Results.Json(ApiResponse<SpeedTestTask>.Fail($"No IPs in pool for ISP {client.Isp}"));

    var task = new SpeedTestTask
    {
        IpAddresses = ips,
        TestUrl = config.TestUrl,
        TestHost = config.TestHost,
        TestPort = config.TestPort,
        DownloadDurationSeconds = config.DownloadDurationSeconds,
        TcpTestDurationSeconds = config.TcpTestDurationSeconds,
        TopN = config.TopN,
        ClientIntervalMinutes = config.ClientIntervalMinutes,
    };

    return Results.Json(ApiResponse<SpeedTestTask>.Ok(task));
});

// ============================================================
//  API: 客户端提交测速结果
// ============================================================
app.MapPost("/api/report", async (SpeedTestReport report, DataStore store, DnsUpdateService dns) =>
{
    if (report.Results.Count == 0)
        return ApiResponse<string>.Fail("No results");

    var client = store.GetClient(report.ClientId);
    if (client is null)
        return ApiResponse<string>.Fail("Client not registered");

    client.LastSeenAt = DateTime.UtcNow;
    store.UpsertClient(client);

    var history = new TestHistory
    {
        TaskId = report.TaskId,
        ClientId = report.ClientId,
        Isp = report.Isp,
        Results = report.Results,
        CompletedAt = report.CompletedAt,
    };
    store.AddHistory(history);

    // 自动清理：综合该运营商所有客户端最新结果，只保留 TopN 最优 IP，其余从池中删除
    var config = store.GetConfig();
    int cleaned = 0;
    if (config.AutoCleanupEnabled && report.Results.Count > 0)
    {
        // 综合所有客户端的最新结果，取 TopN 最优 IP
        var topResults = dns.AggregateLatestResultsForIsp(report.Isp, config.TopN);
        var keepIps = topResults.Select(r => r.IpAddress).ToHashSet();

        // 池中所有 IP 减去 TopN 保留的，即为要删除的
        var ispKey = report.Isp.ToString();
        var poolIps = store.GetApiIpPool(ispKey);
        var removeIps = poolIps.Where(ip => !keepIps.Contains(ip)).ToList();

        if (removeIps.Count > 0)
        {
            cleaned = store.RemoveIpsFromPool(ispKey, removeIps);
        }
    }

    // 触发DNS更新
    try
    {
        await dns.UpdateDnsAsync(report.Isp, report.Results);
    }
    catch (Exception ex)
    {
        return ApiResponse<string>.Fail($"Results saved, but DNS update failed: {ex.Message}");
    }

    var msg = "Report received, DNS update triggered";
    if (cleaned > 0) msg += $", {cleaned} underperforming IPs removed from pool";
    return ApiResponse<string>.Ok(msg);
});

// ============================================================
//  API: WebUI - 获取配置
// ============================================================
app.MapGet("/api/config", (DataStore store) =>
{
    return ApiResponse<ServerConfig>.Ok(store.GetConfig());
});

// ============================================================
//  API: WebUI - 保存配置
// ============================================================
app.MapPost("/api/config", (ServerConfig config, DataStore store) =>
{
    store.SaveConfig(config);
    return ApiResponse<string>.Ok("Config saved");
});

// ============================================================
//  API: WebUI - 获取所有客户端
// ============================================================
app.MapGet("/api/clients", (DataStore store) =>
{
    return ApiResponse<List<ClientInfo>>.Ok(store.GetClients());
});

// ============================================================
//  API: WebUI - 获取历史记录
// ============================================================
app.MapGet("/api/history", (DataStore store, int? limit) =>
{
    return ApiResponse<List<TestHistory>>.Ok(store.GetHistory(limit ?? 100));
});

// ============================================================
//  API: WebUI - 获取所有运营商的IP池聚合数据
// ============================================================
app.MapGet("/api/ippool", (DataStore store) =>
{
    var config = store.GetConfig();
    var result = new Dictionary<string, List<string>>();
    
    foreach (var isp in new[] { "Telecom", "Unicom", "Mobile" })
    {
        var manualIps = config.IpSources.TryGetValue(isp, out var source) ? source.ManualIps : [];
        var apiIps = store.GetApiIpPool(isp);
        result[isp] = manualIps.Concat(apiIps).Distinct().ToList();
    }
    
    return ApiResponse<Dictionary<string, List<string>>>.Ok(result);
});

// ============================================================
//  API: WebUI - 手动添加IP
// ============================================================
app.MapPost("/api/ippool/add", (IpPoolAddRequest req, DataStore store) =>
{
    var config = store.GetConfig();
    if (!config.IpSources.ContainsKey(req.Isp))
        config.IpSources[req.Isp] = new();

    var manualIps = config.IpSources[req.Isp].ManualIps;
    foreach (var ip in req.Ips)
    {
        var trimmed = ip.Trim();
        if (!string.IsNullOrEmpty(trimmed) && !manualIps.Contains(trimmed))
            manualIps.Add(trimmed);
    }
    store.SaveConfig(config);
    return ApiResponse<string>.Ok($"Added {req.Ips.Count} IPs to {req.Isp}");
});

// ============================================================
//  API: WebUI - 覆盖当前池内容
// ============================================================
app.MapPost("/api/ippool/replace", (IpPoolReplaceRequest req, DataStore store) =>
{
    store.ReplaceIpPool(req.Isp, req.Ips);
    return ApiResponse<string>.Ok($"Replaced pool for {req.Isp}");
});

// ============================================================
//  API: WebUI - 手动触发API拉取IP
// ============================================================
app.MapPost("/api/ippool/refresh", async (string? isp, IpPoolService ipPool) =>
{
    await ipPool.RefreshFromApiAsync(isp);
    return ApiResponse<string>.Ok($"Refresh triggered{(isp != null ? " for " + isp : "")}");
});

// ============================================================
//  API: WebUI - 预览拉取源
// ============================================================
app.MapPost("/api/ippool/preview", async (FetchSource source, IpPoolService ipPool) =>
{
    var ips = await ipPool.FetchIpsFromSourceAsync(source);
    return ApiResponse<List<string>>.Ok(ips);
});

// ============================================================
//  API: WebUI - 获取 DNS 更新状态
// ============================================================
app.MapGet("/api/dns/status", (DnsUpdateService dns) =>
{
    return ApiResponse<List<DnsUpdateStatus>>.Ok(dns.GetStatus());
});

// ============================================================
//  API: WebUI - 手动触发 DNS 更新
// ============================================================
app.MapPost("/api/dns/update", async (DnsUpdateTriggerRequest? req, DnsUpdateService dns) =>
{
    var results = await dns.ManualUpdateAsync(req?.Isp);
    return ApiResponse<List<DnsUpdateStatus>>.Ok(results);
});

// ============================================================
//  API: WebUI - 测试华为云凭证
// ============================================================
app.MapPost("/api/dns/test-auth", async (DnsUpdateService dns) =>
{
    var success = await dns.TestAuthAsync();
    if (success)
        return ApiResponse<string>.Ok("Success");
    else
        return ApiResponse<string>.Fail("Auth failed. Check server logs for details.");
});

app.Run();
