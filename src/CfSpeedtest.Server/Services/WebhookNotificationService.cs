using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CfSpeedtest.Shared;

namespace CfSpeedtest.Server.Services;

public sealed class WebhookNotificationService(
    DataStore store,
    IHttpClientFactory httpClientFactory,
    ILogger<WebhookNotificationService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, ClientNotificationState> _clientStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<WebhookNotification> _queue = Channel.CreateUnbounded<WebhookNotification>();

    public void ClientSeen(ClientInfo client)
    {
        while (true)
        {
            if (_clientStates.TryAdd(client.ClientId, new ClientNotificationState(true, null)))
            {
                QueueClientEvent(client, true);
                return;
            }

            if (!_clientStates.TryGetValue(client.ClientId, out var state))
                continue;
            if (state.IsOnline && state.OfflineDetectedAtUtc is null)
                return;

            var recoveredAfterNotification = !state.IsOnline;
            if (_clientStates.TryUpdate(client.ClientId, new ClientNotificationState(true, null), state))
            {
                if (recoveredAfterNotification)
                    QueueClientEvent(client, true);
                return;
            }
        }
    }

    public async Task<string> SendTestAsync(CancellationToken cancellationToken = default)
    {
        var notification = new WebhookNotification
        {
            EventType = "webhook.test",
            OccurredAtUtc = DateTime.UtcNow,
            ClientId = "test-client",
            ClientName = "Webhook 测试",
            Message = "CfSpeedtest Webhook 配置测试成功"
        };

        return await SendAsync(notification, ignoreEnabled: true, cancellationToken);
    }

    public static bool TryValidateTemplate(string template, out string error)
    {
        try
        {
            var rendered = RenderTemplate(template, new WebhookNotification
            {
                EventType = "webhook.test",
                OccurredAtUtc = DateTime.UtcNow,
                ClientId = "test-client",
                ClientName = "Webhook 测试",
                Isp = IspType.Telecom,
                LastSeenAtUtc = DateTime.UtcNow,
                Version = "1.0.0",
                Platform = "linux-x64",
                Message = "CfSpeedtest Webhook 配置测试"
            });
            using var document = JsonDocument.Parse(rendered);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            error = $"Webhook JSON 模板无效：{ex.Message}";
            return false;
        }
    }

    public static bool TryNormalizeHeaders(List<WebhookHeader>? headers, out List<WebhookHeader> normalized, out string error)
    {
        normalized = [];
        foreach (var header in headers ?? [])
        {
            var name = header.Name.Trim();
            var value = header.Value.Trim();
            if (name.Length == 0 && value.Length == 0)
                continue;
            if (name.Length == 0 || value.Length == 0)
            {
                error = "自定义请求头的名称和值都必须填写";
                return false;
            }
            if (value.Contains('\r') || value.Contains('\n'))
            {
                error = $"请求头 {name} 的值不能包含换行符";
                return false;
            }

            using var request = new HttpRequestMessage();
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                error = $"请求头名称无效或不受支持：{name}";
                return false;
            }

            normalized.Add(new WebhookHeader { Name = name, Value = value });
        }

        error = string.Empty;
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        InitializeStates();
        await Task.WhenAll(MonitorClientsAsync(stoppingToken), ProcessQueueAsync(stoppingToken));
    }

    private void InitializeStates()
    {
        var config = store.GetConfig();
        var now = DateTime.UtcNow;
        foreach (var client in store.GetClients())
        {
            _clientStates[client.ClientId] = new ClientNotificationState(IsOnline(client, config, now), null);
        }
    }

    private async Task MonitorClientsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var config = store.GetConfig();
            var now = DateTime.UtcNow;
            var clients = store.GetClients();
            var clientIds = new HashSet<string>(clients.Select(c => c.ClientId), StringComparer.OrdinalIgnoreCase);

            foreach (var client in clients)
            {
                if (IsOnline(client, config, now))
                    continue;

                while (true)
                {
                    if (!_clientStates.TryGetValue(client.ClientId, out var state))
                    {
                        _clientStates.TryAdd(client.ClientId, new ClientNotificationState(false, null));
                        break;
                    }
                    if (!state.IsOnline)
                        break;

                    if (state.OfflineDetectedAtUtc is null)
                    {
                        if (_clientStates.TryUpdate(client.ClientId, state with { OfflineDetectedAtUtc = now }, state))
                            break;
                        continue;
                    }

                    var delay = TimeSpan.FromSeconds(Math.Clamp(config.Webhook.OfflineNotificationDelaySeconds, 0, 86400));
                    if (now - state.OfflineDetectedAtUtc.Value < delay)
                        break;
                    if (!config.Webhook.Enabled || !config.Webhook.NotifyClientOffline)
                        break;

                    if (_clientStates.TryUpdate(client.ClientId, new ClientNotificationState(false, null), state))
                    {
                        client.IsOnline = false;
                        QueueClientEvent(client, false);
                        break;
                    }
                }
            }

            foreach (var clientId in _clientStates.Keys)
            {
                if (!clientIds.Contains(clientId))
                    _clientStates.TryRemove(clientId, out _);
            }
        }
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        await foreach (var notification in _queue.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await SendAsync(notification, ignoreEnabled: false, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send webhook event {EventType} for client {ClientId}",
                    notification.EventType, notification.ClientId);
            }
        }
    }

    private void QueueClientEvent(ClientInfo client, bool online)
    {
        var config = store.GetConfig().Webhook;
        if (!config.Enabled || (online ? !config.NotifyClientOnline : !config.NotifyClientOffline))
            return;

        var displayName = string.IsNullOrWhiteSpace(client.Name) ? client.ClientId : client.Name;
        _queue.Writer.TryWrite(new WebhookNotification
        {
            EventType = online ? "client.online" : "client.offline",
            OccurredAtUtc = DateTime.UtcNow,
            ClientId = client.ClientId,
            ClientName = client.Name,
            Isp = client.Isp,
            LastSeenAtUtc = client.LastSeenAt == DateTime.MinValue ? null : client.LastSeenAt.ToUniversalTime(),
            Version = client.Version,
            Platform = client.Platform,
            Message = $"客户端 {displayName} 已{(online ? "上线" : "离线")}"
        });
    }

    private async Task<string> SendAsync(WebhookNotification notification, bool ignoreEnabled, CancellationToken cancellationToken)
    {
        var config = store.GetConfig().Webhook;
        if (!ignoreEnabled && !config.Enabled)
            return "Webhook 未启用";
        if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Webhook URL 必须是有效的 HTTP 或 HTTPS 地址");

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        var json = RenderTemplate(config.BodyTemplate, notification);
        using var document = JsonDocument.Parse(json);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CfSpeedtest", "1.0"));
        foreach (var header in config.Headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Name, header.Value))
                throw new InvalidOperationException($"无法添加自定义请求头：{header.Name}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await httpClientFactory.CreateClient().SendAsync(request, timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Webhook 返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        logger.LogInformation("Sent webhook event {EventType} for client {ClientId}", notification.EventType, notification.ClientId);
        return $"Webhook 发送成功（HTTP {(int)response.StatusCode}）";
    }

    private static bool IsOnline(ClientInfo client, ServerConfig config, DateTime now)
    {
        var onlineWindow = TimeSpan.FromSeconds(Math.Max(30, config.HeartbeatIntervalSeconds * 3));
        return client.LastSeenAt != DateTime.MinValue &&
               now - client.LastSeenAt.ToUniversalTime() <= onlineWindow;
    }

    private static string RenderTemplate(string template, WebhookNotification notification)
    {
        if (string.IsNullOrWhiteSpace(template))
            throw new InvalidOperationException("Webhook JSON 模板不能为空");

        var online = notification.EventType == "client.online";
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{{eventType}}"] = EscapeJsonString(notification.EventType),
            ["{{occurredAtUtc}}"] = EscapeJsonString(notification.OccurredAtUtc.ToString("O")),
            ["{{clientId}}"] = EscapeJsonString(notification.ClientId),
            ["{{clientName}}"] = EscapeJsonString(notification.ClientName ?? string.Empty),
            ["{{isp}}"] = ((int)notification.Isp).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["{{ispName}}"] = EscapeJsonString(notification.Isp.ToString()),
            ["{{online}}"] = online ? "true" : "false",
            ["{{lastSeenAtUtc}}"] = EscapeJsonString(notification.LastSeenAtUtc?.ToString("O") ?? string.Empty),
            ["{{version}}"] = EscapeJsonString(notification.Version ?? string.Empty),
            ["{{platform}}"] = EscapeJsonString(notification.Platform ?? string.Empty),
            ["{{message}}"] = EscapeJsonString(notification.Message)
        };

        var rendered = template;
        foreach (var (placeholder, value) in values)
            rendered = rendered.Replace(placeholder, value, StringComparison.Ordinal);

        return rendered;
    }

    private static string EscapeJsonString(string value)
    {
        var serialized = JsonSerializer.Serialize(value, AppJsonContext.Default.String);
        return serialized[1..^1];
    }

    private sealed record ClientNotificationState(bool IsOnline, DateTime? OfflineDetectedAtUtc);
}
