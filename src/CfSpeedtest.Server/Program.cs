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
builder.Services.AddSingleton<RoundCoordinatorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RoundCoordinatorService>());
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// ============================================================
//  API: 客户端注册
// ============================================================
app.MapPost("/api/client/register", (ClientRegisterRequest req, DataStore store) =>
{
    var config = store.GetConfig();
    var clientId = req.ClientId;
    if (string.IsNullOrWhiteSpace(clientId))
    {
        if (config.ClientWhitelistOnly)
            return ApiResponse<ClientRegisterResponse>.Fail("ClientId is required in whitelist mode");

        clientId = Guid.NewGuid().ToString("N");
    }

    var existing = store.GetClient(clientId);
    if (existing is null && config.ClientWhitelistOnly)
        return ApiResponse<ClientRegisterResponse>.Fail("ClientId is not in whitelist");
    if (existing is not null && !existing.Allowed)
        return ApiResponse<ClientRegisterResponse>.Fail("Client is not allowed to connect");

    var info = new ClientInfo
    {
        ClientId = clientId,
        Isp = req.Isp,
        Name = req.Name ?? $"{req.Isp}-{clientId[..6]}",
        Version = string.IsNullOrWhiteSpace(req.Version) ? existing?.Version : req.Version,
        Platform = string.IsNullOrWhiteSpace(req.Platform) ? existing?.Platform : req.Platform,
        RegisteredAt = existing?.RegisteredAt ?? DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow,
        IsOnline = true,
        Allowed = existing?.Allowed ?? true,
    };
    store.UpsertClient(info);

    return ApiResponse<ClientRegisterResponse>.Ok(new ClientRegisterResponse
    {
        ClientId = clientId,
        Success = true,
        Message = "Registered",
        HeartbeatIntervalSeconds = config.HeartbeatIntervalSeconds,
    });
});

// ============================================================
//  API: 客户端心跳
// ============================================================
app.MapPost("/api/client/heartbeat", (ClientHeartbeatRequest req, DataStore store) =>
{
    if (string.IsNullOrWhiteSpace(req.ClientId))
        return ApiResponse<ClientHeartbeatResponse>.Fail("ClientId is required");

    var config = store.GetConfig();
    var client = store.GetClient(req.ClientId);
    if (client is null)
    {
        if (config.ClientWhitelistOnly)
            return ApiResponse<ClientHeartbeatResponse>.Fail("ClientId is not in whitelist");

        client = new ClientInfo
        {
            ClientId = req.ClientId,
            RegisteredAt = DateTime.UtcNow,
            Allowed = true,
        };
    }
    else if (!client.Allowed)
    {
        return ApiResponse<ClientHeartbeatResponse>.Fail("Client is not allowed to connect");
    }

    client.Isp = req.Isp;
    client.Name = string.IsNullOrWhiteSpace(req.Name) ? client.Name : req.Name;
    client.Version = string.IsNullOrWhiteSpace(req.Version) ? client.Version : req.Version;
    client.Platform = string.IsNullOrWhiteSpace(req.Platform) ? client.Platform : req.Platform;
    client.LastSeenAt = DateTime.UtcNow;
    client.IsOnline = true;
    store.UpsertClient(client);

    return ApiResponse<ClientHeartbeatResponse>.Ok(new ClientHeartbeatResponse
    {
        Success = true,
        Message = "Heartbeat received",
        HeartbeatIntervalSeconds = config.HeartbeatIntervalSeconds,
    });
});

// ============================================================
//  API: 客户端检查更新
// ============================================================
app.MapGet("/api/client/update", (HttpContext http, DataStore store, string version, string? platform) =>
{
    var config = store.GetConfig();
    var currentVersion = string.IsNullOrWhiteSpace(version) ? "0.0.0" : version.Trim();
    var latestVersion = string.IsNullOrWhiteSpace(config.LatestClientVersion) ? currentVersion : config.LatestClientVersion.Trim();
    var clientPlatform = string.IsNullOrWhiteSpace(platform) ? "win-x64" : platform.Trim();
    var repository = config.ClientUpdateRepository.Trim();
    var releaseTag = config.ClientUpdateReleaseTag.Trim();
    var ghProxyPrefix = config.ClientUpdateGhProxyPrefix.Trim();

    if (!config.ClientUpdateEnabled)
    {
        return ApiResponse<ClientUpdateInfo>.Ok(new ClientUpdateInfo
        {
            Enabled = false,
            CurrentVersion = currentVersion,
            LatestVersion = latestVersion,
            Platform = clientPlatform,
            HasUpdate = false,
            Message = "客户端自动更新未启用"
        });
    }

    if (string.IsNullOrWhiteSpace(latestVersion) || string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(releaseTag))
    {
        return ApiResponse<ClientUpdateInfo>.Ok(new ClientUpdateInfo
        {
            Enabled = true,
            CurrentVersion = currentVersion,
            LatestVersion = latestVersion,
            Platform = clientPlatform,
            HasUpdate = false,
            Message = "服务端未配置 GitHub Release 更新源"
        });
    }

    var hasUpdate = IsVersionNewer(latestVersion, currentVersion);
    var fileName = GetClientUpdateFileName(clientPlatform, latestVersion);
    var rawUrl = $"https://github.com/{repository}/releases/download/{releaseTag}/{fileName}";
    var downloadUrl = string.IsNullOrWhiteSpace(ghProxyPrefix)
        ? rawUrl
        : CombineProxyUrl(ghProxyPrefix, rawUrl);
    return ApiResponse<ClientUpdateInfo>.Ok(new ClientUpdateInfo
    {
        Enabled = true,
        CurrentVersion = currentVersion,
        LatestVersion = latestVersion,
        Platform = clientPlatform,
        HasUpdate = hasUpdate,
        DownloadUrl = hasUpdate ? downloadUrl : null,
        PackageFileName = fileName,
        Message = hasUpdate ? "发现新版本" : "当前已是最新版本"
    });
});

app.MapGet("/api/client/update/overview", (DataStore store) =>
{
    var config = store.GetConfig();
    var platforms = new[] { "win-x64", "linux-x64", "linux-musl-x64" };
    var packages = new List<ClientUpdatePackageStatus>();
    var repository = config.ClientUpdateRepository.Trim();
    var releaseTag = config.ClientUpdateReleaseTag.Trim();
    var ghProxyPrefix = config.ClientUpdateGhProxyPrefix.Trim();

    foreach (var platform in platforms)
    {
        var fileName = GetClientUpdateFileName(platform, config.LatestClientVersion);
        var rawUrl = string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(releaseTag)
            ? string.Empty
            : $"https://github.com/{repository}/releases/download/{releaseTag}/{fileName}";
        var downloadUrl = string.IsNullOrWhiteSpace(rawUrl)
            ? string.Empty
            : (string.IsNullOrWhiteSpace(ghProxyPrefix) ? rawUrl : CombineProxyUrl(ghProxyPrefix, rawUrl));

        packages.Add(new ClientUpdatePackageStatus
        {
            Platform = platform,
            FileName = fileName,
            DownloadUrl = downloadUrl,
        });
    }

    return ApiResponse<ClientUpdateOverview>.Ok(new ClientUpdateOverview
    {
        Enabled = config.ClientUpdateEnabled,
        LatestVersion = config.LatestClientVersion,
        Repository = repository,
        ReleaseTag = releaseTag,
        GhProxyPrefix = ghProxyPrefix,
        Packages = packages,
    });
});

// ============================================================
//  API: 客户端获取测速任务
// ============================================================
app.MapGet("/api/task/{clientId}", (string clientId, DataStore store, IpPoolService ipPool, RoundCoordinatorService rounds) =>
{
    var client = store.GetClient(clientId);
    if (client is null)
        return Results.Json(ApiResponse<SpeedTestTask>.Fail("Client not registered"));
    if (!client.Allowed)
        return Results.Json(ApiResponse<SpeedTestTask>.Fail("Client is not allowed to connect"));

    client.LastSeenAt = DateTime.UtcNow;
    client.IsOnline = true;
    store.UpsertClient(client);

    var config = store.GetConfig();
    var ips = ipPool.GetBatch(client.Isp.ToString());
    var round = rounds.RegisterClient(client.Isp, clientId);

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
        MinDownloadSpeedKBps = config.MinDownloadSpeedKBps,
        MaxDownloadSpeedKBps = config.MaxDownloadSpeedKBps,
        ClientIntervalMinutes = config.ClientIntervalMinutes,
        TaskId = round.TaskId,
        ScheduledAtUtc = round.ScheduledAtUtc,
    };

    return Results.Json(ApiResponse<SpeedTestTask>.Ok(task));
});

// ============================================================
//  API: 客户端提交测速结果
// ============================================================
app.MapPost("/api/report", async (SpeedTestReport report, DataStore store, RoundCoordinatorService rounds) =>
{
    if (report.Results.Count == 0)
        return ApiResponse<string>.Fail("No results");

    var client = store.GetClient(report.ClientId);
    if (client is null)
        return ApiResponse<string>.Fail("Client not registered");
    if (!client.Allowed)
        return ApiResponse<string>.Fail("Client is not allowed to connect");

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

    try
    {
        var msg = await rounds.HandleReportAsync(report);
        return ApiResponse<string>.Ok(msg);
    }
    catch (Exception ex)
    {
        return ApiResponse<string>.Fail($"Results saved, but round finalization failed: {ex.Message}");
    }
});

// ============================================================
//  API: 客户端补拉新的未测IP批次
// ============================================================
app.MapPost("/api/task/additional", (AdditionalIpBatchRequest req, DataStore store, IpPoolService ipPool) =>
{
    if (string.IsNullOrWhiteSpace(req.ClientId))
        return ApiResponse<AdditionalIpBatchResponse>.Fail("ClientId is required");

    var client = store.GetClient(req.ClientId);
    if (client is null)
        return ApiResponse<AdditionalIpBatchResponse>.Fail("Client not registered");
    if (!client.Allowed)
        return ApiResponse<AdditionalIpBatchResponse>.Fail("Client is not allowed to connect");

    client.LastSeenAt = DateTime.UtcNow;
    client.IsOnline = true;
    store.UpsertClient(client);

    var ips = ipPool.GetBatch(req.Isp.ToString(), req.ExcludeIps);
    return ApiResponse<AdditionalIpBatchResponse>.Ok(new AdditionalIpBatchResponse
    {
        IpAddresses = ips,
    });
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
//  API: WebUI - 预生成并保留允许连接的客户端 ID
// ============================================================
app.MapPost("/api/clients/reserve", (ClientReservationRequest req, DataStore store) =>
{
    var clientId = string.IsNullOrWhiteSpace(req.ClientId)
        ? Guid.NewGuid().ToString("N")
        : req.ClientId.Trim();

    if (store.GetClient(clientId) is not null)
        return ApiResponse<ClientReservationResponse>.Fail("ClientId already exists");

    var info = new ClientInfo
    {
        ClientId = clientId,
        Isp = req.Isp,
        Name = string.IsNullOrWhiteSpace(req.Name) ? $"{req.Isp}-{clientId[..6]}" : req.Name,
        RegisteredAt = DateTime.UtcNow,
        LastSeenAt = DateTime.MinValue,
        IsOnline = false,
        Allowed = true,
    };
    store.UpsertClient(info);

    return ApiResponse<ClientReservationResponse>.Ok(new ClientReservationResponse
    {
        ClientId = clientId,
        Message = "Reserved"
    });
});

// ============================================================
//  API: WebUI - 删除客户端
// ============================================================
app.MapDelete("/api/clients/{clientId}", (string clientId, DataStore store) =>
{
    var removed = store.RemoveClient(clientId);
    return removed
        ? ApiResponse<string>.Ok("Client removed")
        : ApiResponse<string>.Fail("Client not found");
});

// ============================================================
//  API: WebUI - 设置客户端是否允许连接
// ============================================================
app.MapPost("/api/clients/{clientId}/allow", (string clientId, bool allowed, DataStore store) =>
{
    var updated = store.SetClientAllowed(clientId, allowed);
    return updated
        ? ApiResponse<string>.Ok(allowed ? "Client allowed" : "Client blocked")
        : ApiResponse<string>.Fail("Client not found");
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
//  API: WebUI - 获取统一轮次状态
// ============================================================
app.MapGet("/api/rounds/status", (RoundCoordinatorService rounds) =>
{
    return ApiResponse<RoundStatusOverview>.Ok(rounds.GetStatusOverview());
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

static bool IsVersionNewer(string latestVersion, string currentVersion)
{
    if (Version.TryParse(latestVersion, out var latest) && Version.TryParse(currentVersion, out var current))
    {
        return latest > current;
    }

    return !string.Equals(latestVersion, currentVersion, StringComparison.OrdinalIgnoreCase);
}

static string GetClientUpdateFileName(string platform, string version)
{
    return $"CfSpeedtest.Client-{platform}-{version}.zip";
}

static string CombineProxyUrl(string proxyPrefix, string rawUrl)
{
    return proxyPrefix.TrimEnd('/') + "/" + rawUrl;
}
