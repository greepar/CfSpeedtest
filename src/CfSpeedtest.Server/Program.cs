using System.Text.Json;
using System.Net.WebSockets;
using CfSpeedtest.Server.Services;
using CfSpeedtest.Shared;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);
var clientUpdatesDir = Path.Combine(builder.Environment.ContentRootPath, "client-updates");
Directory.CreateDirectory(clientUpdatesDir);

builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.TypeInfoResolverChain.Add(AppJsonContext.Default);
});
builder.Services.Configure<HostOptions>(o =>
{
    o.ShutdownTimeout = TimeSpan.FromSeconds(5);
});

builder.Services.AddSingleton<DataStore>();
builder.Services.AddSingleton<IpPoolService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IpPoolService>());
builder.Services.AddSingleton<DnsUpdateService>();
builder.Services.AddSingleton<RoundCoordinatorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RoundCoordinatorService>());
builder.Services.AddSingleton<ClientWsHub>();
builder.Services.AddSingleton<WebUiAuthService>();
builder.Services.AddHttpClient();

var app = builder.Build();
app.Services.GetRequiredService<WebUiAuthService>().EnsureInitialized(app.Services.GetRequiredService<DataStore>());
app.Lifetime.ApplicationStopping.Register(() =>
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    try
    {
        app.Services.GetRequiredService<ClientWsHub>()
            .CloseAllAsync(WebSocketCloseStatus.EndpointUnavailable, "server shutting down", cts.Token)
            .GetAwaiter()
            .GetResult();
    }
    catch
    {
        // ignore shutdown-time websocket close failures
    }
});

app.Use(async (context, next) =>
{
    if (!RequiresWebUiAuth(context.Request.Path))
    {
        await next();
        return;
    }

    var auth = context.RequestServices.GetRequiredService<WebUiAuthService>();
    var store = context.RequestServices.GetRequiredService<DataStore>();
    if (auth.TryAuthenticate(context, store, out var username))
    {
        context.Items["webui_username"] = username;
        await next();
        return;
    }

    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    context.Response.ContentType = "application/json; charset=utf-8";
    await context.Response.WriteAsJsonAsync(ApiResponse<string>.Fail("Unauthorized"), AppJsonContext.Default.ApiResponseString);
});

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(15)
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(clientUpdatesDir),
    RequestPath = "/client-updates"
});

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
        Isp = existing?.Isp ?? req.Isp,
        Name = existing?.Name ?? (req.Name ?? $"{req.Isp}-{clientId[..6]}"),
        Version = string.IsNullOrWhiteSpace(req.Version) ? existing?.Version : req.Version,
        Platform = string.IsNullOrWhiteSpace(req.Platform) ? existing?.Platform : req.Platform,
        RuntimeStatus = existing?.RuntimeStatus,
        CurrentTaskTotalIps = existing?.CurrentTaskTotalIps ?? 0,
        CurrentTaskTestedIps = existing?.CurrentTaskTestedIps ?? 0,
        CurrentTaskStartedAt = existing?.CurrentTaskStartedAt,
        RuntimeLog = existing?.RuntimeLog,
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
        EffectiveIsp = info.Isp,
        EffectiveName = info.Name ?? $"{info.Isp}-{clientId[..6]}",
        EffectiveProxyMode = config.ClientProxyMode,
        EffectiveProxyUrl = config.ClientProxyUrl,
    });
});

// ============================================================
//  API: 客户端心跳
// ============================================================
app.MapPost("/api/client/heartbeat", (ClientHeartbeatRequest req, DataStore store, RoundCoordinatorService rounds) =>
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
            Isp = req.Isp,
            Name = string.IsNullOrWhiteSpace(req.Name) ? $"{req.Isp}-{req.ClientId[..6]}" : req.Name,
            RegisteredAt = DateTime.UtcNow,
            Allowed = true,
        };
    }
    else if (!client.Allowed)
    {
        return ApiResponse<ClientHeartbeatResponse>.Fail("Client is not allowed to connect");
    }

    client.Version = string.IsNullOrWhiteSpace(req.Version) ? client.Version : req.Version;
    client.Platform = string.IsNullOrWhiteSpace(req.Platform) ? client.Platform : req.Platform;
    client.RuntimeStatus = string.IsNullOrWhiteSpace(req.RuntimeStatus) ? client.RuntimeStatus : req.RuntimeStatus;
    client.CurrentTaskTotalIps = req.CurrentTaskTotalIps;
    client.CurrentTaskTestedIps = req.CurrentTaskTestedIps;
    client.CurrentTaskStartedAt = req.CurrentTaskStartedAt;
    client.RuntimeLog = string.IsNullOrWhiteSpace(req.RuntimeLog) ? client.RuntimeLog : req.RuntimeLog;
    client.LastSeenAt = DateTime.UtcNow;
    client.IsOnline = true;
    store.UpsertClient(client);

    return ApiResponse<ClientHeartbeatResponse>.Ok(new ClientHeartbeatResponse
    {
        Success = true,
        Message = "Heartbeat received",
        HeartbeatIntervalSeconds = config.HeartbeatIntervalSeconds,
        ForceFetchTask = rounds.ConsumeImmediateTrigger(req.ClientId, client.Isp),
        ForceCheckUpdate = rounds.ConsumeClientUpdateTrigger(req.ClientId),
        EffectiveIsp = client.Isp,
        EffectiveName = client.Name ?? $"{client.Isp}-{client.ClientId[..6]}",
        EffectiveProxyMode = config.ClientProxyMode,
        EffectiveProxyUrl = config.ClientProxyUrl,
    });
});

app.Map("/api/client/ws", async (HttpContext context, DataStore store, RoundCoordinatorService rounds, ClientWsHub hub) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var clientId = context.Request.Query["clientId"].ToString();
    if (string.IsNullOrWhiteSpace(clientId))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var client = store.GetClient(clientId);
    if (client is null || !client.Allowed)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    hub.SetConnection(clientId, socket);

    var hello = new ClientWsMessage
    {
        Type = "hello",
        ClientId = clientId,
        HeartbeatIntervalSeconds = store.GetConfig().HeartbeatIntervalSeconds,
        Message = "ws connected",
        EffectiveIsp = client.Isp,
        EffectiveName = client.Name ?? $"{client.Isp}-{client.ClientId[..6]}",
        EffectiveProxyMode = store.GetConfig().ClientProxyMode,
        EffectiveProxyUrl = store.GetConfig().ClientProxyUrl,
    };
    await hub.SendAsync(clientId, hello, context.RequestAborted);

    var buffer = new byte[8192];
    try
    {
        while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            var json = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
            var msg = JsonSerializer.Deserialize(json, AppJsonContext.Default.ClientWsMessage);
            if (msg is null) continue;

            client = store.GetClient(clientId) ?? client;
    client.Version = string.IsNullOrWhiteSpace(msg.Version) ? client.Version : msg.Version;
    client.Platform = string.IsNullOrWhiteSpace(msg.Platform) ? client.Platform : msg.Platform;
    client.RuntimeStatus = string.IsNullOrWhiteSpace(msg.RuntimeStatus) ? client.RuntimeStatus : msg.RuntimeStatus;
            client.CurrentTaskTotalIps = msg.CurrentTaskTotalIps;
            client.CurrentTaskTestedIps = msg.CurrentTaskTestedIps;
            client.CurrentTaskStartedAt = msg.CurrentTaskStartedAt;
            client.RuntimeLog = string.IsNullOrWhiteSpace(msg.RuntimeLog) ? client.RuntimeLog : msg.RuntimeLog;
            client.LastSeenAt = DateTime.UtcNow;
    client.IsOnline = true;
    store.UpsertClient(client);

            var response = new ClientWsMessage
            {
                Type = "heartbeat-ack",
                ClientId = clientId,
                HeartbeatIntervalSeconds = store.GetConfig().HeartbeatIntervalSeconds,
                ForceFetchTask = rounds.ConsumeImmediateTrigger(clientId, client.Isp),
                ForceCheckUpdate = rounds.ConsumeClientUpdateTrigger(clientId),
                EffectiveIsp = client.Isp,
                EffectiveName = client.Name ?? $"{client.Isp}-{client.ClientId[..6]}",
                EffectiveProxyMode = store.GetConfig().ClientProxyMode,
                EffectiveProxyUrl = store.GetConfig().ClientProxyUrl,
            };
            await hub.SendAsync(clientId, response, context.RequestAborted);
        }
    }
    finally
    {
        hub.RemoveConnection(clientId, socket);
        if (socket.State == WebSocketState.Open)
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", closeCts.Token);
        }
    }
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
    var sourceType = string.IsNullOrWhiteSpace(config.ClientUpdateSourceType) ? "github" : config.ClientUpdateSourceType.Trim().ToLowerInvariant();
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

    if (string.IsNullOrWhiteSpace(latestVersion))
    {
        return ApiResponse<ClientUpdateInfo>.Ok(new ClientUpdateInfo
        {
            Enabled = true,
            CurrentVersion = currentVersion,
            LatestVersion = latestVersion,
            Platform = clientPlatform,
            HasUpdate = false,
            Message = "服务端未配置最新客户端版本号"
        });
    }

    var hasUpdate = IsVersionNewer(latestVersion, currentVersion);
    var fileName = GetClientUpdateFileName(clientPlatform);
    string? downloadUrl = null;

    if (sourceType == "local")
    {
        var packageFile = Path.Combine(clientUpdatesDir, fileName);
        if (!File.Exists(packageFile))
        {
            return ApiResponse<ClientUpdateInfo>.Ok(new ClientUpdateInfo
            {
                Enabled = true,
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                Platform = clientPlatform,
                HasUpdate = false,
                Message = $"服务端本地更新目录缺少文件 {fileName}"
            });
        }

        var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
        downloadUrl = $"{baseUrl}/client-updates/{fileName}";
    }
    else
    {
        if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(releaseTag))
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

        var rawUrl = $"https://github.com/{repository}/releases/download/{releaseTag}/{fileName}";
        downloadUrl = string.IsNullOrWhiteSpace(ghProxyPrefix)
            ? rawUrl
            : CombineProxyUrl(ghProxyPrefix, rawUrl);
    }

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
    var platforms = new[] { "win-x86", "win-x64", "win-arm64", "linux-x64", "linux-musl-x64", "linux-arm64", "linux-musl-arm64", "linux-arm", "osx-x64", "osx-arm64" };
    var packages = new List<ClientUpdatePackageStatus>();
    var sourceType = string.IsNullOrWhiteSpace(config.ClientUpdateSourceType) ? "github" : config.ClientUpdateSourceType.Trim().ToLowerInvariant();
    var repository = config.ClientUpdateRepository.Trim();
    var releaseTag = config.ClientUpdateReleaseTag.Trim();
    var ghProxyPrefix = config.ClientUpdateGhProxyPrefix.Trim();

    foreach (var platform in platforms)
    {
        var fileName = GetClientUpdateFileName(platform);
        string downloadUrl;
        if (sourceType == "local")
        {
            var packageFile = Path.Combine(clientUpdatesDir, fileName);
            downloadUrl = File.Exists(packageFile) ? $"/client-updates/{fileName}" : string.Empty;
        }
        else
        {
            var rawUrl = string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(releaseTag)
                ? string.Empty
                : $"https://github.com/{repository}/releases/download/{releaseTag}/{fileName}";
            downloadUrl = string.IsNullOrWhiteSpace(rawUrl)
                ? string.Empty
                : (string.IsNullOrWhiteSpace(ghProxyPrefix) ? rawUrl : CombineProxyUrl(ghProxyPrefix, rawUrl));
        }

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
        SourceType = sourceType,
        Repository = repository,
        ReleaseTag = releaseTag,
        GhProxyPrefix = ghProxyPrefix,
        LocalDirectory = clientUpdatesDir,
        Packages = packages,
    });
});

app.MapGet("/api/auth/status", (HttpContext http, DataStore store, WebUiAuthService auth) =>
{
    var ok = auth.TryAuthenticate(http, store, out var username);
    var conf = store.GetConfig().WebUiAuth;
    return ApiResponse<WebUiAuthStatus>.Ok(new WebUiAuthStatus
    {
        Enabled = conf.Enabled,
        Authenticated = ok,
        Username = ok ? username : string.Empty
    });
});

app.MapPost("/api/auth/login", (HttpContext http, WebUiLoginRequest req, DataStore store, WebUiAuthService auth) =>
{
    var conf = store.GetConfig().WebUiAuth;
    if (!conf.Enabled)
    {
        return ApiResponse<WebUiAuthStatus>.Ok(new WebUiAuthStatus
        {
            Enabled = false,
            Authenticated = true,
            Username = conf.Username
        });
    }

    if (!auth.ValidateLogin(store, req.Username, req.Password))
        return ApiResponse<WebUiAuthStatus>.Fail("用户名或密码错误");

    var token = auth.CreateSession(store, http, conf.Username);
    auth.SignIn(http, token);
    return ApiResponse<WebUiAuthStatus>.Ok(new WebUiAuthStatus
    {
        Enabled = true,
        Authenticated = true,
        Username = conf.Username
    });
});

app.MapPost("/api/auth/logout", (HttpContext http, DataStore store, WebUiAuthService auth) =>
{
    auth.SignOut(http, store);
    return ApiResponse<string>.Ok("Logged out");
});

app.MapGet("/api/auth/sessions", (DataStore store, WebUiAuthService auth) =>
{
    return ApiResponse<List<WebUiSessionOverview>>.Ok(auth.GetSessionOverviews(store));
});

app.MapPost("/api/auth/change-password", (WebUiChangePasswordRequest req, DataStore store, WebUiAuthService auth) =>
{
    if (string.IsNullOrWhiteSpace(req.NewUsername) || string.IsNullOrWhiteSpace(req.NewPassword))
        return ApiResponse<string>.Fail("用户名和新密码不能为空");
    if (req.NewPassword.Length < 8)
        return ApiResponse<string>.Fail("新密码长度至少 8 位");

    var changed = auth.ChangeCredentials(store, req.CurrentPassword, req.NewUsername, req.NewPassword);
    return changed
        ? ApiResponse<string>.Ok("登录凭据已更新，请重新登录")
        : ApiResponse<string>.Fail("当前密码错误或新凭据无效");
});

app.MapPost("/api/clients/{clientId}/trigger-update", (string clientId, DataStore store, RoundCoordinatorService rounds) =>
{
    var client = store.GetClient(clientId);
    if (client is null)
        return ApiResponse<string>.Fail("Client not found");

    rounds.TriggerClientUpdate(clientId);
    return ApiResponse<string>.Ok("客户端将在下一次心跳后立即检查更新");
});

app.MapPost("/api/clients/{clientId}/trigger-test", async (string clientId, RoundCoordinatorService rounds, ClientWsHub hub, DataStore store) =>
{
    var ok = rounds.TriggerImmediateRoundForClient(clientId, out var isp);
    if (ok)
    {
        var client = store.GetClient(clientId);
        if (client is not null)
        {
            await hub.SendAsync(clientId, new ClientWsMessage
            {
                Type = "trigger-test",
                ClientId = clientId,
                HeartbeatIntervalSeconds = store.GetConfig().HeartbeatIntervalSeconds,
                ForceFetchTask = true,
                EffectiveIsp = isp,
                EffectiveName = client.Name ?? $"{isp}-{clientId[..6]}",
                Message = "manual retest triggered by server"
            });
        }
    }

    return ok
        ? ApiResponse<string>.Ok("客户端将在下一次心跳后立即重新测速")
        : ApiResponse<string>.Fail("Client not found or not allowed");
});

app.MapPost("/api/clients/{clientId}/metadata", async (string clientId, ClientMetadataUpdateRequest req, DataStore store, ClientWsHub hub) =>
{
    if (!Enum.TryParse<IspType>(req.Isp, true, out var isp))
        return ApiResponse<string>.Fail("Invalid ISP");

    var name = req.Name.Trim();
    if (string.IsNullOrWhiteSpace(name))
        return ApiResponse<string>.Fail("Name is required");

    var updated = store.UpdateClientMetadata(clientId, isp, name);
    if (!updated)
        return ApiResponse<string>.Fail("Client not found");

    var client = store.GetClient(clientId);
    if (client is not null)
    {
        await hub.SendAsync(clientId, new ClientWsMessage
        {
            Type = "metadata-update",
            ClientId = clientId,
            HeartbeatIntervalSeconds = store.GetConfig().HeartbeatIntervalSeconds,
            Message = "client metadata updated by server",
            EffectiveIsp = client.Isp,
            EffectiveName = client.Name ?? $"{client.Isp}-{client.ClientId[..6]}",
        });
    }

    return ApiResponse<string>.Ok("Client metadata updated");
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
    var round = rounds.RegisterClient(client.Isp, clientId);

    var wait = round.ScheduledAtUtc - DateTime.UtcNow;
    if (!round.IsImmediateDispatch && wait > TimeSpan.Zero)
    {
        return Results.Json(ApiResponse<SpeedTestTask>.Fail($"Round not started yet. Retry after {Math.Ceiling(wait.TotalSeconds)} seconds."));
    }

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
        MinDownloadSpeedKBps = config.MinDownloadSpeedKBps,
        MaxDownloadSpeedKBps = config.MaxDownloadSpeedKBps,
        ClientIntervalMinutes = config.ClientIntervalMinutes,
        TaskId = round.TaskId,
        ScheduledAtUtc = round.ScheduledAtUtc,
    };

    if (round.IsImmediateDispatch)
    {
        rounds.MarkTriggerTaskDispatched(clientId, client.Isp);
    }

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
    client.RuntimeStatus = "已完成测速";
    client.CurrentTaskTestedIps = report.Results.Count;
    client.CurrentTaskTotalIps = Math.Max(client.CurrentTaskTotalIps, report.Results.Count);
    client.RuntimeLog = client.RuntimeLog;
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
    if (!Enum.TryParse<IspType>(req.Isp, out var isp))
        return ApiResponse<ClientReservationResponse>.Fail("Invalid ISP");

    var clientId = string.IsNullOrWhiteSpace(req.ClientId)
        ? Guid.NewGuid().ToString("N")
        : req.ClientId.Trim();

    if (store.GetClient(clientId) is not null)
        return ApiResponse<ClientReservationResponse>.Fail("ClientId already exists");

    var info = new ClientInfo
    {
        ClientId = clientId,
        Isp = isp,
        Name = string.IsNullOrWhiteSpace(req.Name) ? $"{isp}-{clientId[..6]}" : req.Name,
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

app.MapPost("/api/history/cleanup", (DataStore store) =>
{
    var removed = store.ApplyHistoryRetention();
    return ApiResponse<string>.Ok($"已清理 {removed} 条过期测速记录");
});

app.MapPost("/api/history/clear", (DataStore store) =>
{
    var removed = store.ClearHistory();
    return ApiResponse<string>.Ok($"已清空 {removed} 条测速记录");
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

app.MapPost("/api/dns/trigger-test", async (DnsUpdateTriggerRequest? req, RoundCoordinatorService rounds, ClientWsHub hub, DataStore store) =>
{
    var clientIds = rounds.TriggerImmediateRound(req?.Isp);
    foreach (var clientId in clientIds)
    {
        var client = store.GetClient(clientId);
        if (client is null) continue;

        await hub.SendAsync(clientId, new ClientWsMessage
        {
            Type = "trigger-test",
            ClientId = clientId,
            HeartbeatIntervalSeconds = store.GetConfig().HeartbeatIntervalSeconds,
            ForceFetchTask = true,
            EffectiveIsp = client.Isp,
            EffectiveName = client.Name ?? $"{client.Isp}-{clientId[..6]}",
            Message = "manual round triggered by server"
        });
    }

    return ApiResponse<string>.Ok("测速触发已下发，在线客户端将在下一次心跳后立即拉取任务并开始测速");
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

app.MapPost("/api/dns/test-record", async (DnsUpdateTriggerRequest? req, DnsUpdateService dns) =>
{
    if (string.IsNullOrWhiteSpace(req?.Isp))
        return ApiResponse<string>.Fail("Isp is required");

    var result = await dns.TestRecordConfigAsync(req.Isp);
    return result.Success
        ? ApiResponse<string>.Ok(result.Message)
        : ApiResponse<string>.Fail(result.Message);
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

static string GetClientUpdateFileName(string platform)
{
    return $"cfspeedtest-client-{platform}.zip";
}

static string CombineProxyUrl(string proxyPrefix, string rawUrl)
{
    return proxyPrefix.TrimEnd('/') + "/" + rawUrl;
}

static bool RequiresWebUiAuth(PathString path)
{
    var value = path.Value ?? string.Empty;
    if (string.IsNullOrWhiteSpace(value) || value == "/" || value.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
        return false;

    if (value.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase))
        return false;

    if (value.StartsWith("/api/client/", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("/api/task/", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("/api/report", StringComparison.OrdinalIgnoreCase))
        return false;

    if (value.StartsWith("/client-updates/", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("/install/client/", StringComparison.OrdinalIgnoreCase))
        return false;

    return value.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
}
