using System.Text.Json;
using System.Net.WebSockets;
using System.IO;
using System.Reflection;
using CfSpeedtest.Server.Services;
using CfSpeedtest.Shared;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.StaticFiles;

var builder = WebApplication.CreateBuilder(args);
var clientUpdatesDir = Path.Combine(builder.Environment.ContentRootPath, "client-updates");
Directory.CreateDirectory(clientUpdatesDir);
var materialWebDir = new[]
{
    Path.Combine(builder.Environment.ContentRootPath, "material-web"),
    Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "material-web")),
}
.FirstOrDefault(Directory.Exists);

builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.TypeInfoResolverChain.Add(AppJsonContext.Default);
});
builder.Services.Configure<HostOptions>(o =>
{
    o.ShutdownTimeout = TimeSpan.FromSeconds(5);
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost;
    // 接受任意上游反代（生产建议改成具体 KnownIPNetworks/KnownProxies）
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
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
var embeddedWebAssets = BuildEmbeddedWebAssetMap();
var embeddedContentTypes = new FileExtensionContentTypeProvider();
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

app.UseForwardedHeaders();

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
app.Use(async (context, next) =>
{
    if (await TryServeEmbeddedWebAssetAsync(context, embeddedWebAssets, embeddedContentTypes))
        return;

    await next();
});
if (!string.IsNullOrWhiteSpace(materialWebDir))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(materialWebDir),
        RequestPath = "/material-web",
        ServeUnknownFileTypes = true,
        DefaultContentType = "application/octet-stream"
    });
}
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
    store.MarkBootstrapTokenConsumedByClient(clientId);

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
    store.MarkBootstrapTokenConsumedByClient(client.ClientId);

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
            var json = await ReceiveWebSocketTextMessageAsync(socket, buffer, context.RequestAborted);
            if (json is null)
                break;
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
    store.MarkBootstrapTokenConsumedByClient(client.ClientId);

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

app.MapPost("/api/client/install-script", (ClientInstallScriptRequest req, DataStore store) =>
{
    if (string.IsNullOrWhiteSpace(req.Platform))
        return ApiResponse<ClientInstallScriptResponse>.Fail("Platform is required");

    var scriptType = string.IsNullOrWhiteSpace(req.ScriptType) ? "install" : req.ScriptType.Trim().ToLowerInvariant();
    var platform = req.Platform.Trim().ToLowerInvariant();

    if (platform != "linux" && platform != "windows" && platform != "macos")
        return ApiResponse<ClientInstallScriptResponse>.Fail("Unsupported platform");

    var response = BuildClientCommandResponse(req, store.GetConfig(), platform, scriptType);
    return response is null
        ? ApiResponse<ClientInstallScriptResponse>.Fail("无法生成命令，请先检查更新源配置")
        : ApiResponse<ClientInstallScriptResponse>.Ok(response);
});

// ============================================================
//  API: 一键部署 - 创建 Bootstrap Token
// ============================================================
app.MapPost("/api/bootstrap/create", (HttpContext http, BootstrapTokenCreateRequest req, DataStore store) =>
{
    var ispStr = string.IsNullOrWhiteSpace(req.Isp) ? "Telecom" : req.Isp.Trim();
    if (!Enum.TryParse<IspType>(ispStr, true, out var isp))
        return ApiResponse<BootstrapTokenCreateResponse>.Fail("Invalid ISP");

    store.CleanupBootstrapTokens();

    string clientId;
    string name;

    if (!string.IsNullOrWhiteSpace(req.ClientId))
    {
        var existing = store.GetClient(req.ClientId.Trim());
        if (existing is null)
            return ApiResponse<BootstrapTokenCreateResponse>.Fail("ClientId not found");
        if (!existing.Allowed)
            return ApiResponse<BootstrapTokenCreateResponse>.Fail("Client is not allowed");

        clientId = existing.ClientId;
        // 编辑请求里若带了新的 name/isp，把数据库一起改了
        var newName = string.IsNullOrWhiteSpace(req.Name) ? existing.Name : req.Name.Trim();
        if (!string.Equals(existing.Name, newName, StringComparison.Ordinal) || existing.Isp != isp)
        {
            store.UpdateClientMetadata(clientId, isp, string.IsNullOrWhiteSpace(newName) ? $"{isp}-{clientId[..6]}" : newName);
        }
        name = string.IsNullOrWhiteSpace(newName) ? $"{isp}-{clientId[..6]}" : newName;
    }
    else
    {
        clientId = Guid.NewGuid().ToString("N");
        name = string.IsNullOrWhiteSpace(req.Name)
            ? $"{isp}-{clientId[..6]}"
            : req.Name.Trim();

        var info = new ClientInfo
        {
            ClientId = clientId,
            Isp = isp,
            Name = name,
            RegisteredAt = DateTime.UtcNow,
            LastSeenAt = DateTime.MinValue,
            IsOnline = false,
            Allowed = true,
        };
        store.UpsertClient(info);
    }

    var serverUrl = string.IsNullOrWhiteSpace(req.ServerUrl)
        ? $"{http.Request.Scheme}://{http.Request.Host}"
        : req.ServerUrl.Trim().TrimEnd('/');

    var token = GenerateBootstrapTokenCode(store);

    var record = new BootstrapToken
    {
        Token = token,
        ClientId = clientId,
        Isp = isp,
        Name = name,
        ServerUrl = serverUrl,
        IncludeProxy = req.IncludeProxy,
        DisableAutoUpdate = req.DisableAutoUpdate,
        CreatedAtUtc = DateTime.UtcNow,
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30),
        Consumed = false,
    };
    store.UpsertBootstrapToken(record);

    var publicBase = $"{http.Request.Scheme}://{http.Request.Host}";
    var linuxCmd = $"curl -Ls {publicBase}/i/{token} | bash";
    var windowsCmd = $"irm {publicBase}/i/{token} | iex";

    return ApiResponse<BootstrapTokenCreateResponse>.Ok(new BootstrapTokenCreateResponse
    {
        Token = token,
        ClientId = clientId,
        Isp = isp,
        Name = name,
        ExpiresAtUtc = record.ExpiresAtUtc,
        ServerUrl = serverUrl,
        LinuxCommand = linuxCmd,
        WindowsCommand = windowsCmd,
    });
});

// ============================================================
//  API: 一键部署 - 查询 Token 状态（前端轮询用）
// ============================================================
app.MapGet("/api/bootstrap/{token}/status", (string token, DataStore store) =>
{
    var record = store.GetBootstrapToken(token);
    if (record is null)
        return ApiResponse<BootstrapTokenStatus>.Fail("Token not found");

    var client = store.GetClient(record.ClientId);
    var online = false;
    string? runtimeStatus = null;
    DateTime? lastSeen = null;
    if (client is not null)
    {
        runtimeStatus = client.RuntimeStatus;
        lastSeen = client.LastSeenAt == DateTime.MinValue ? null : client.LastSeenAt;
        online = client.IsOnline && client.LastSeenAt != DateTime.MinValue
                 && (DateTime.UtcNow - client.LastSeenAt) < TimeSpan.FromMinutes(2);
    }

    return ApiResponse<BootstrapTokenStatus>.Ok(new BootstrapTokenStatus
    {
        Token = record.Token,
        ClientId = record.ClientId,
        Online = online,
        Consumed = record.Consumed,
        Expired = !record.Consumed && DateTime.UtcNow > record.ExpiresAtUtc,
        ExpiresAtUtc = record.ExpiresAtUtc,
        LastSeenAtUtc = lastSeen,
        RuntimeStatus = runtimeStatus,
    });
});

// ============================================================
//  /i/{token} - 公开端点，按 User-Agent 返回平台对应的一行命令脚本
// ============================================================
app.MapGet("/i/{token}", (HttpContext http, string token, DataStore store) =>
{
    var record = store.GetBootstrapToken(token);
    if (record is null)
    {
        http.Response.StatusCode = StatusCodes.Status404NotFound;
        return Results.Text("# Bootstrap token not found or already revoked\n", "text/plain", System.Text.Encoding.UTF8);
    }

    if (!record.Consumed && DateTime.UtcNow > record.ExpiresAtUtc)
    {
        http.Response.StatusCode = StatusCodes.Status410Gone;
        return Results.Text("# Bootstrap token expired\n", "text/plain", System.Text.Encoding.UTF8);
    }

    var config = store.GetConfig();
    var ua = http.Request.Headers.UserAgent.ToString() ?? string.Empty;
    var forcePs = http.Request.Query.ContainsKey("ps") || http.Request.Query.ContainsKey("powershell");
    var forceSh = http.Request.Query.ContainsKey("sh") || http.Request.Query.ContainsKey("bash");

    var isWindows = forcePs || (!forceSh && (
        ua.Contains("PowerShell", StringComparison.OrdinalIgnoreCase) ||
        ua.Contains("WindowsPowerShell", StringComparison.OrdinalIgnoreCase) ||
        ua.Contains("Microsoft.PowerShell", StringComparison.OrdinalIgnoreCase) ||
        ua.Contains("WindowsTerminal", StringComparison.OrdinalIgnoreCase) ||
        ua.Contains("Windows NT", StringComparison.OrdinalIgnoreCase)));

    var script = isWindows
        ? BuildBootstrapPowerShellScript(record, config)
        : BuildBootstrapBashScript(record, config);

    // bash 脚本必须使用 LF 换行，避免 Windows 服务端生成的 CRLF 导致 Linux 端 $'\r' 错误
    if (!isWindows)
        script = script.Replace("\r\n", "\n");

    // 简单返回 text/plain；让 ASP.NET 自己加 charset，避免反代解析重复 charset 时返回 502
    http.Response.Headers["Cache-Control"] = "no-store";
    return Results.Text(script, "text/plain", System.Text.Encoding.UTF8);
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

app.MapGet("/api/server/info", () =>
{
    var version = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion;

    if (string.IsNullOrWhiteSpace(version))
    {
        version = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
    }

    return ApiResponse<ServerInfo>.Ok(new ServerInfo
    {
        Version = string.IsNullOrWhiteSpace(version) ? "unknown" : version
    });
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
        MaxTestIpCount = config.MaxTestIpCount,
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
app.MapGet("/api/history", (DataStore store, int? limit, DateTime? from, DateTime? to) =>
{
    if (from.HasValue && to.HasValue)
        return ApiResponse<List<TestHistory>>.Ok(store.GetHistoryByTimeRange(from.Value, to.Value, limit ?? 500));
    return ApiResponse<List<TestHistory>>.Ok(store.GetHistory(limit ?? 100));
});

app.MapGet("/api/history/segments", (DataStore store) =>
{
    return ApiResponse<List<HistoryTimeSegment>>.Ok(store.GetHistoryTimeSegments());
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

app.MapDelete("/api/history/{historyId}", (string historyId, DataStore store) =>
{
    if (string.IsNullOrWhiteSpace(historyId))
    {
        return ApiResponse<string>.Fail("HistoryId is required");
    }

    return store.RemoveHistory(historyId)
        ? ApiResponse<string>.Ok("测速记录已删除")
        : ApiResponse<string>.Fail("测速记录不存在");
});

// ============================================================
//  API: WebUI - 获取所有运营商的IP池聚合数据
// ============================================================
app.MapGet("/api/ippool", (DataStore store) =>
{
    var config = store.GetConfig();
    var result = new Dictionary<string, IpPoolView>();
    
    foreach (var isp in new[] { "Telecom", "Unicom", "Mobile" })
    {
        var manualIps = config.IpSources.TryGetValue(isp, out var source) ? source.ManualIps : [];
        var apiIps = store.GetApiIpPool(isp);
        result[isp] = new IpPoolView
        {
            ManualIps = manualIps.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ApiIps = apiIps.Where(ip => !manualIps.Contains(ip, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }
    
    return ApiResponse<Dictionary<string, IpPoolView>>.Ok(result);
});

// ============================================================
//  API: WebUI - 手动添加IP
// ============================================================
app.MapPost("/api/ippool/add", (IpPoolAddRequest req, DataStore store) =>
{
    store.AddManualIps(req.Isp, req.Ips);
    return ApiResponse<string>.Ok($"Added {req.Ips.Count} IPs to {req.Isp}");
});

// ============================================================
//  API: WebUI - 覆盖当前运营商的手动 IP 池
// ============================================================
app.MapPost("/api/ippool/replace", (IpPoolReplaceRequest req, DataStore store) =>
{
    store.ReplaceManualIpPool(req.Isp, req.Ips);
    return ApiResponse<string>.Ok($"Replaced manual pool for {req.Isp}");
});

app.MapPost("/api/ippool/remove", (IpPoolRemoveRequest req, DataStore store) =>
{
    if (string.IsNullOrWhiteSpace(req.Isp) || string.IsNullOrWhiteSpace(req.Ip) || string.IsNullOrWhiteSpace(req.Source))
    {
        return ApiResponse<string>.Fail("Isp, Ip and Source are required");
    }

    return store.RemovePoolIp(req.Isp, req.Ip, req.Source)
        ? ApiResponse<string>.Ok("IP 已删除")
        : ApiResponse<string>.Fail("IP 不存在");
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
app.MapGet("/api/dns/status", (DnsUpdateService dns, DateTime? from, DateTime? to) =>
{
    return ApiResponse<List<DnsUpdateStatus>>.Ok(dns.GetStatus(from, to));
});

// ============================================================
//  API: WebUI - 手动触发 DNS 更新
// ============================================================
app.MapPost("/api/dns/update", async (DnsUpdateTriggerRequest? req, DnsUpdateService dns) =>
{
    var results = await dns.ManualUpdateAsync(req?.Isp, req?.From, req?.To);
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

static ClientInstallScriptResponse? BuildClientCommandResponse(ClientInstallScriptRequest req, ServerConfig config, string platform, string scriptType)
{
    var normalizedServerUrl = (string.IsNullOrWhiteSpace(req.ServerUrl) ? string.Empty : req.ServerUrl.Trim()).TrimEnd('/');
    var normalizedClientId = string.IsNullOrWhiteSpace(req.ClientId) ? "CHANGE_ME_CLIENT_ID" : req.ClientId.Trim();
    var normalizedIsp = string.IsNullOrWhiteSpace(req.Isp) ? "Telecom" : req.Isp.Trim();
    var normalizedName = string.IsNullOrWhiteSpace(req.Name) ? $"{normalizedIsp}-{normalizedClientId[..Math.Min(8, normalizedClientId.Length)]}" : req.Name.Trim();

    if (scriptType == "manual")
    {
        return new ClientInstallScriptResponse
        {
            Platform = platform,
            ScriptType = scriptType,
            ServiceKind = "manual",
            ScriptFileName = "CfSpeedtest.Client.exe",
            ScriptSource = "manual",
            Script = BuildManualClientCommand(normalizedServerUrl, normalizedClientId, normalizedIsp, normalizedName, req.DisableAutoUpdate),
        };
    }

    if (scriptType == "uninstall")
    {
        return new ClientInstallScriptResponse
        {
            Platform = platform,
            ScriptType = scriptType,
            ServiceKind = platform == "windows" ? "windows-service" : (platform == "macos" ? "launchd" : "auto-detect"),
            ScriptFileName = platform == "windows" ? "remove-cfspeedtest-client.ps1" : "remove-cfspeedtest-client.sh",
            ScriptSource = "generated",
            Script = BuildUninstallCommand(platform),
        };
    }

    var scriptInfo = BuildInstallScriptInfo(config, platform, req.IncludeProxy);
    if (scriptInfo is null)
    {
        return null;
    }
    var info = scriptInfo.Value;

    return new ClientInstallScriptResponse
    {
        Platform = platform,
        ScriptType = scriptType,
        ServiceKind = info.ServiceKind,
        ScriptFileName = info.FileName,
        ScriptSource = info.Source,
        ScriptUrl = info.Url,
        Script = BuildInstallCommand(platform, info.Url, config, normalizedServerUrl, normalizedClientId, normalizedIsp, normalizedName, req.IncludeProxy),
    };
}

static string BuildManualClientCommand(string serverUrl, string clientId, string isp, string name, bool disableAutoUpdate)
{
    var command = $"CfSpeedtest.Client.exe --server {serverUrl} --client-id {clientId} --isp {isp}";
    if (!string.IsNullOrWhiteSpace(name))
    {
        command += $" --name {name}";
    }
    if (disableAutoUpdate)
    {
        command += " --disable-auto-update";
    }
    return command;
}

static string BuildInstallCommand(string platform, string scriptUrl, ServerConfig config, string serverUrl, string clientId, string isp, string name, bool includeProxy)
{
    if (platform == "windows")
    {
        var args = new List<string>
        {
            "-ServerUrl " + PsSingleQuote(serverUrl),
            "-ClientId " + PsSingleQuote(clientId),
            "-Isp " + PsSingleQuote(isp),
            "-ClientName " + PsSingleQuote(name),
            "-Repository " + PsSingleQuote(config.ClientUpdateRepository.Trim()),
            "-ReleaseTag " + PsSingleQuote(config.ClientUpdateReleaseTag.Trim())
        };

        if (includeProxy && !string.IsNullOrWhiteSpace(config.ClientUpdateGhProxyPrefix))
        {
            args.Add("-GhProxyPrefix " + PsSingleQuote(config.ClientUpdateGhProxyPrefix.Trim()));
        }

        return "powershell -NoProfile -ExecutionPolicy Bypass -Command \"& { " +
               "$tmp = Join-Path $env:TEMP " + PsSingleQuote("install-cfspeedtest-client.ps1") + "; " +
               "irm " + PsSingleQuote(scriptUrl) + " -OutFile $tmp; " +
               "& powershell -ExecutionPolicy Bypass -File $tmp " + string.Join(" ", args) +
               " }\"";
    }

    var suffix = new List<string>
    {
        "--server " + ShellQuote(serverUrl),
        "--client-id " + ShellQuote(clientId),
        "--isp " + ShellQuote(isp),
        "--name " + ShellQuote(name),
        "--repository " + ShellQuote(config.ClientUpdateRepository.Trim()),
        "--release-tag " + ShellQuote(config.ClientUpdateReleaseTag.Trim())
    };

    if (includeProxy && !string.IsNullOrWhiteSpace(config.ClientUpdateGhProxyPrefix))
    {
        suffix.Add("--gh-proxy-prefix " + ShellQuote(config.ClientUpdateGhProxyPrefix.Trim()));
    }

    return "wget -qO- \"" + scriptUrl + "\" | sudo bash -s -- " + string.Join(" ", suffix);
}

static string BuildUninstallCommand(string platform)
{
    if (platform == "windows")
    {
        return "powershell -NoProfile -ExecutionPolicy Bypass -Command \"" +
               "$serviceName='CfSpeedtestClient';" +
               "$installDir=Join-Path $env:ProgramFiles 'CfSpeedtestClient';" +
               "$nssmExe=Join-Path $installDir 'nssm\\nssm.exe';" +
               "if (Test-Path $nssmExe) { & $nssmExe stop $serviceName | Out-Null; & $nssmExe remove $serviceName confirm | Out-Null };" +
               "if (Test-Path $installDir) { Remove-Item -Recurse -Force $installDir }" +
               "\"";
    }

    if (platform == "macos")
    {
        return "sudo launchctl bootout system /Library/LaunchDaemons/com.cfspeedtest.client.plist 2>/dev/null; " +
               "sudo rm -f /Library/LaunchDaemons/com.cfspeedtest.client.plist; " +
               "sudo rm -rf /opt/cfspeedtest-client";
    }

    return "sudo sh -c " + ShellQuote(
        "SERVICE_NAME=\"cfspeedtest-client\"; " +
        "if command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]; then " +
            "systemctl stop \"$SERVICE_NAME\" 2>/dev/null || true; " +
            "systemctl disable \"$SERVICE_NAME\" 2>/dev/null || true; " +
            "rm -f \"/etc/systemd/system/${SERVICE_NAME}.service\"; " +
            "systemctl daemon-reload; " +
        "elif [ -f /etc/openwrt_release ] || [ -f /etc/rc.common ]; then " +
            "if [ -x \"/etc/init.d/${SERVICE_NAME}\" ]; then \"/etc/init.d/${SERVICE_NAME}\" stop 2>/dev/null || true; \"/etc/init.d/${SERVICE_NAME}\" disable 2>/dev/null || true; rm -f \"/etc/init.d/${SERVICE_NAME}\"; fi; " +
        "elif command -v rc-service >/dev/null 2>&1 || [ -x /sbin/openrc-run ]; then " +
            "rc-service \"$SERVICE_NAME\" stop 2>/dev/null || true; " +
            "rc-update del \"$SERVICE_NAME\" default 2>/dev/null || true; " +
            "rm -f \"/etc/init.d/${SERVICE_NAME}\"; " +
        "fi; " +
        "rm -rf /opt/cfspeedtest-client"
    );
}

static (string FileName, string Url, string Source, string ServiceKind)? BuildInstallScriptInfo(ServerConfig config, string platform, bool includeProxy)
{
    var sourceType = string.IsNullOrWhiteSpace(config.ClientUpdateSourceType) ? "github" : config.ClientUpdateSourceType.Trim().ToLowerInvariant();
    var fileName = platform == "windows"
        ? "install-cfspeedtest-client-windows.ps1"
        : (platform == "macos" ? "install-cfspeedtest-client-macos.sh" : "install-cfspeedtest-client-linux.sh");
    var serviceKind = platform == "windows" ? "windows-service" : (platform == "macos" ? "launchd" : "auto-detect");

    if (sourceType != "github")
    {
        return null;
    }

    var repository = config.ClientUpdateRepository.Trim();
    var releaseTag = config.ClientUpdateReleaseTag.Trim();
    if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(releaseTag))
    {
        return null;
    }

    var rawUrl = $"https://github.com/{repository}/releases/download/{releaseTag}/{fileName}";
    var url = includeProxy && !string.IsNullOrWhiteSpace(config.ClientUpdateGhProxyPrefix)
        ? CombineProxyUrl(config.ClientUpdateGhProxyPrefix.Trim(), rawUrl)
        : rawUrl;

    return (fileName, url, "GitHub Release", serviceKind);
}

static string ShellQuote(string value)
{
    return "'" + (value ?? string.Empty).Replace("'", "'\"'\"'") + "'";
}

static string PsSingleQuote(string value)
{
    return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
}

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

static async Task<string?> ReceiveWebSocketTextMessageAsync(WebSocket socket, byte[] buffer, CancellationToken cancellationToken)
{
    using var ms = new MemoryStream();
    while (true)
    {
        var result = await socket.ReceiveAsync(buffer, cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close)
            return null;

        ms.Write(buffer, 0, result.Count);
        if (result.EndOfMessage)
            break;
    }

    return System.Text.Encoding.UTF8.GetString(ms.ToArray());
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

    if (value.StartsWith("/i/", StringComparison.OrdinalIgnoreCase))
        return false;

    if (value.StartsWith("/api/bootstrap/", StringComparison.OrdinalIgnoreCase) &&
        value.EndsWith("/status", StringComparison.OrdinalIgnoreCase))
        return false;

    return value.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
}

static Dictionary<string, string> BuildEmbeddedWebAssetMap()
{
    const string Prefix = "wwwroot/";
    var assembly = Assembly.GetExecutingAssembly();
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var resourceName in assembly.GetManifestResourceNames())
    {
        if (!resourceName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            continue;

        var path = "/" + resourceName[Prefix.Length..].Replace('\\', '/');
        result[path] = resourceName;
    }

    return result;
}

static async Task<bool> TryServeEmbeddedWebAssetAsync(HttpContext context, IReadOnlyDictionary<string, string> assets, FileExtensionContentTypeProvider contentTypes)
{
    if (assets.Count == 0)
        return false;
    if (context.Request.Method != HttpMethods.Get && context.Request.Method != HttpMethods.Head)
        return false;
    if (!IsEmbeddedWebAssetCandidate(context.Request.Path))
        return false;

    var path = context.Request.Path.Value ?? "/";
    if (string.IsNullOrWhiteSpace(path) || path == "/")
        path = "/index.html";
    if (path.Contains("..", StringComparison.Ordinal))
        return false;

    if (!assets.TryGetValue(path, out var resourceName) && !Path.HasExtension(path))
    {
        assets.TryGetValue("/index.html", out resourceName);
    }
    if (string.IsNullOrWhiteSpace(resourceName))
        return false;

    var assembly = Assembly.GetExecutingAssembly();
    await using var stream = assembly.GetManifestResourceStream(resourceName);
    if (stream is null)
        return false;

    if (!contentTypes.TryGetContentType(path, out var contentType))
        contentType = "application/octet-stream";
    if (path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
        context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
    context.Response.ContentType = contentType;
    context.Response.ContentLength = stream.Length;
    if (context.Request.Method != HttpMethods.Head)
        await stream.CopyToAsync(context.Response.Body);
    return true;
}

static bool IsEmbeddedWebAssetCandidate(PathString path)
{
    var value = path.Value ?? string.Empty;
    if (value.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("/client-updates/", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("/material-web/", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("/install/client/", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("/i/", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    return true;
}

static string GenerateBootstrapTokenCode(DataStore store)
{
    const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
    var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
    var buffer = new byte[8];
    for (var attempt = 0; attempt < 32; attempt++)
    {
        rng.GetBytes(buffer);
        var sb = new System.Text.StringBuilder(8);
        for (var i = 0; i < 8; i++)
        {
            sb.Append(Alphabet[buffer[i] % Alphabet.Length]);
        }
        var token = sb.ToString();
        if (store.GetBootstrapToken(token) is null) return token;
    }

    // Fallback: use guid suffix if collision space exhausted (shouldn't happen)
    return Guid.NewGuid().ToString("N")[..12];
}

static string BuildBootstrapBashScript(BootstrapToken token, ServerConfig config)
{
    var serverUrl = string.IsNullOrWhiteSpace(token.ServerUrl) ? "" : token.ServerUrl.Trim().TrimEnd('/');
    var clientId = token.ClientId;
    var isp = token.Isp.ToString();
    var name = string.IsNullOrWhiteSpace(token.Name) ? $"{isp}-{clientId[..6]}" : token.Name;
    var repository = config.ClientUpdateRepository.Trim();
    var releaseTag = config.ClientUpdateReleaseTag.Trim();
    var ghProxyPrefix = config.ClientUpdateGhProxyPrefix.Trim();

    if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(releaseTag))
    {
        return "#!/usr/bin/env bash\necho '[CfSpeedtest] 服务端未配置 GitHub 仓库或 Release Tag，无法部署' >&2\nexit 1\n";
    }

    var scriptFile = "install-cfspeedtest-client-linux.sh";
    var rawUrl = $"https://github.com/{repository}/releases/download/{releaseTag}/{scriptFile}";
    var scriptUrl = (token.IncludeProxy && !string.IsNullOrWhiteSpace(ghProxyPrefix))
        ? CombineProxyUrl(ghProxyPrefix, rawUrl)
        : rawUrl;

    var args = new List<string>
    {
        "--server " + ShellQuote(serverUrl),
        "--client-id " + ShellQuote(clientId),
        "--isp " + ShellQuote(isp),
        "--name " + ShellQuote(name),
        "--repository " + ShellQuote(repository),
        "--release-tag " + ShellQuote(releaseTag),
    };

    if (token.IncludeProxy && !string.IsNullOrWhiteSpace(ghProxyPrefix))
    {
        args.Add("--gh-proxy-prefix " + ShellQuote(ghProxyPrefix));
    }

    var sb = new System.Text.StringBuilder();
    sb.AppendLine("#!/usr/bin/env bash");
    sb.AppendLine("# CfSpeedtest one-click bootstrap installer");
    sb.AppendLine("# Token: " + token.Token);
    sb.AppendLine("# Generated: " + DateTime.UtcNow.ToString("u"));
    sb.AppendLine("set -e");
    sb.AppendLine();
    sb.AppendLine("if [ \"${EUID:-$(id -u)}\" -ne 0 ]; then");
    sb.AppendLine("  if command -v sudo >/dev/null 2>&1; then");
    sb.AppendLine("    SUDO=sudo");
    sb.AppendLine("  else");
    sb.AppendLine("    echo '[CfSpeedtest] 需要 root 权限，请使用 sudo 执行' >&2");
    sb.AppendLine("    exit 1");
    sb.AppendLine("  fi");
    sb.AppendLine("else");
    sb.AppendLine("  SUDO=");
    sb.AppendLine("fi");
    sb.AppendLine();
    sb.AppendLine("UNAME_S=\"$(uname -s 2>/dev/null || echo Linux)\"");
    sb.AppendLine($"SCRIPT_URL_LINUX={ShellQuote(scriptUrl)}");
    var macScriptFile = "install-cfspeedtest-client-macos.sh";
    var macRaw = $"https://github.com/{repository}/releases/download/{releaseTag}/{macScriptFile}";
    var macUrl = (token.IncludeProxy && !string.IsNullOrWhiteSpace(ghProxyPrefix))
        ? CombineProxyUrl(ghProxyPrefix, macRaw)
        : macRaw;
    sb.AppendLine($"SCRIPT_URL_MACOS={ShellQuote(macUrl)}");
    sb.AppendLine();
    sb.AppendLine("if [ \"$UNAME_S\" = \"Darwin\" ]; then");
    sb.AppendLine("  SCRIPT_URL=\"$SCRIPT_URL_MACOS\"");
    sb.AppendLine("else");
    sb.AppendLine("  SCRIPT_URL=\"$SCRIPT_URL_LINUX\"");
    sb.AppendLine("fi");
    sb.AppendLine();
    sb.AppendLine("if command -v curl >/dev/null 2>&1; then");
    sb.AppendLine("  FETCH=\"curl -fsSL\"");
    sb.AppendLine("elif command -v wget >/dev/null 2>&1; then");
    sb.AppendLine("  FETCH=\"wget -qO-\"");
    sb.AppendLine("else");
    sb.AppendLine("  echo '[CfSpeedtest] 需要 curl 或 wget' >&2");
    sb.AppendLine("  exit 1");
    sb.AppendLine("fi");
    sb.AppendLine();
    sb.AppendLine("echo \"[CfSpeedtest] 拉取安装脚本: $SCRIPT_URL\"");
    sb.AppendLine("$FETCH \"$SCRIPT_URL\" | $SUDO bash -s -- \\");
    for (var i = 0; i < args.Count; i++)
    {
        var line = "  " + args[i];
        if (i < args.Count - 1) line += " \\";
        sb.AppendLine(line);
    }
    sb.AppendLine();
    return sb.ToString();
}

static string BuildBootstrapPowerShellScript(BootstrapToken token, ServerConfig config)
{
    var serverUrl = string.IsNullOrWhiteSpace(token.ServerUrl) ? "" : token.ServerUrl.Trim().TrimEnd('/');
    var clientId = token.ClientId;
    var isp = token.Isp.ToString();
    var name = string.IsNullOrWhiteSpace(token.Name) ? $"{isp}-{clientId[..6]}" : token.Name;
    var repository = config.ClientUpdateRepository.Trim();
    var releaseTag = config.ClientUpdateReleaseTag.Trim();
    var ghProxyPrefix = config.ClientUpdateGhProxyPrefix.Trim();

    if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(releaseTag))
    {
        return "Write-Error '[CfSpeedtest] 服务端未配置 GitHub 仓库或 Release Tag，无法部署'\nexit 1\n";
    }

    var scriptFile = "install-cfspeedtest-client-windows.ps1";
    var rawUrl = $"https://github.com/{repository}/releases/download/{releaseTag}/{scriptFile}";
    var scriptUrl = (token.IncludeProxy && !string.IsNullOrWhiteSpace(ghProxyPrefix))
        ? CombineProxyUrl(ghProxyPrefix, rawUrl)
        : rawUrl;

    var sb = new System.Text.StringBuilder();
    sb.AppendLine("# CfSpeedtest one-click bootstrap installer");
    sb.AppendLine("# Token: " + token.Token);
    sb.AppendLine("# Generated: " + DateTime.UtcNow.ToString("u"));
    sb.AppendLine("$ErrorActionPreference = 'Stop'");
    sb.AppendLine($"$ScriptUrl = {PsSingleQuote(scriptUrl)}");
    sb.AppendLine($"$ServerUrl = {PsSingleQuote(serverUrl)}");
    sb.AppendLine($"$ClientId  = {PsSingleQuote(clientId)}");
    sb.AppendLine($"$Isp       = {PsSingleQuote(isp)}");
    sb.AppendLine($"$ClientName= {PsSingleQuote(name)}");
    sb.AppendLine($"$Repository= {PsSingleQuote(repository)}");
    sb.AppendLine($"$ReleaseTag= {PsSingleQuote(releaseTag)}");
    if (token.IncludeProxy && !string.IsNullOrWhiteSpace(ghProxyPrefix))
    {
        sb.AppendLine($"$GhProxyPrefix = {PsSingleQuote(ghProxyPrefix)}");
    }
    else
    {
        sb.AppendLine("$GhProxyPrefix = ''");
    }
    sb.AppendLine();
    sb.AppendLine("Write-Host \"[CfSpeedtest] 拉取安装脚本: $ScriptUrl\"");
    sb.AppendLine("$tmp = Join-Path $env:TEMP 'install-cfspeedtest-client.ps1'");
    sb.AppendLine("Invoke-WebRequest -Uri $ScriptUrl -OutFile $tmp -UseBasicParsing");
    sb.AppendLine();
    sb.AppendLine("$psArgs = @(");
    sb.AppendLine("    '-NoProfile','-ExecutionPolicy','Bypass','-File',$tmp,");
    sb.AppendLine("    '-ServerUrl',$ServerUrl,");
    sb.AppendLine("    '-ClientId',$ClientId,");
    sb.AppendLine("    '-Isp',$Isp,");
    sb.AppendLine("    '-ClientName',$ClientName,");
    sb.AppendLine("    '-Repository',$Repository,");
    sb.AppendLine("    '-ReleaseTag',$ReleaseTag");
    sb.AppendLine(")");
    sb.AppendLine("if (-not [string]::IsNullOrWhiteSpace($GhProxyPrefix)) {");
    sb.AppendLine("    $psArgs += @('-GhProxyPrefix',$GhProxyPrefix)");
    sb.AppendLine("}");
    sb.AppendLine();
    sb.AppendLine("& powershell.exe @psArgs");
    sb.AppendLine("exit $LASTEXITCODE");
    return sb.ToString();
}
