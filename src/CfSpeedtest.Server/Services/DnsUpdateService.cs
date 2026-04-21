using System.Text;
using System.Text.Json;
using CfSpeedtest.Shared;

namespace CfSpeedtest.Server.Services;

/// <summary>
/// DNS更新服务 - 支持华为云 DNS API
/// 根据测速结果自动更新各运营商对应域名的A记录
/// 同时作为后台定时服务运行
/// </summary>
public class DnsUpdateService : IHostedService, IDisposable
{
    private readonly ILogger<DnsUpdateService> _logger;
    private readonly DataStore _store;
    private readonly IHttpClientFactory _httpFactory;

    // 缓存 IAM Token
    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    // 各运营商最后一次更新状态
    private readonly Dictionary<string, DnsUpdateStatus> _lastStatus = new();

    // 后台定时器
    private Timer? _timer;
    private int _running; // 0=idle, 1=running (防止重入)

    public DnsUpdateService(ILogger<DnsUpdateService> logger, DataStore store, IHttpClientFactory httpFactory)
    {
        _logger = logger;
        _store = store;
        _httpFactory = httpFactory;
    }

    // ==================== IHostedService ====================

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("DnsUpdateService background service starting");
        // 启动时延迟 30 秒再执行第一次，给系统时间初始化
        _timer = new Timer(OnTimerTick, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("DnsUpdateService background service stopping");
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }

    /// <summary>
    /// 定时器回调 - 每 30 秒检查一次是否到了该更新的时间
    /// 实际更新间隔由配置 UpdateIntervalMinutes 控制
    /// </summary>
    private async void OnTimerTick(object? state)
    {
        // 防止重入
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return;

        try
        {
            var config = _store.GetConfig();
            var hwConfig = config.HuaweiDns;

            if (!hwConfig.Enabled || hwConfig.UpdateIntervalMinutes <= 0)
                return;

            var interval = TimeSpan.FromMinutes(hwConfig.UpdateIntervalMinutes);

            foreach (var ispKey in new[] { "Telecom", "Unicom", "Mobile" })
            {
                // 检查该运营商是否配置了记录集
                if (!hwConfig.Records.TryGetValue(ispKey, out var rec)
                    || string.IsNullOrEmpty(rec.ZoneId)
                    || string.IsNullOrEmpty(rec.RecordSetId))
                    continue;

                // 检查是否到了更新时间
                if (_lastStatus.TryGetValue(ispKey, out var lastStatus)
                    && lastStatus.LastUpdatedAt.HasValue
                    && DateTime.UtcNow - lastStatus.LastUpdatedAt.Value < interval)
                    continue;

                // 到时间了，执行更新
                if (!Enum.TryParse<IspType>(ispKey, out var ispEnum))
                    continue;

                _logger.LogInformation("Auto DNS update triggered for {Isp} (interval: {Interval} min)",
                    ispKey, hwConfig.UpdateIntervalMinutes);

                var aggregatedResults = AggregateLatestResultsForIsp(ispEnum, config.TopN);
                if (aggregatedResults.Count == 0)
                {
                    _logger.LogDebug("No IPs available for auto DNS update of {Isp}, skipping", ispKey);
                    continue;
                }

                await UpdateRecordSetForIspAsync(ispKey, aggregatedResults, hwConfig);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in DNS auto-update timer");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    // ==================== Public API ====================

    /// <summary>
    /// 获取各运营商的DNS更新状态
    /// </summary>
    public List<DnsUpdateStatus> GetStatus()
    {
        var config = _store.GetConfig();
        var hwConfig = config.HuaweiDns;
        var result = new List<DnsUpdateStatus>();

        foreach (var isp in new[] { "Telecom", "Unicom", "Mobile" })
        {
            if (_lastStatus.TryGetValue(isp, out var status))
            {
                result.Add(status);
            }
            else
            {
                var s = new DnsUpdateStatus { Isp = isp, Message = "尚未执行过更新" };
                // 填充域名信息
                if (hwConfig.Records.TryGetValue(isp, out var rec))
                    s.Domain = rec.Domain;
                result.Add(s);
            }
        }

        // 附加下次更新时间信息
        if (hwConfig.Enabled && hwConfig.UpdateIntervalMinutes > 0)
        {
            foreach (var s in result)
            {
                if (s.LastUpdatedAt.HasValue)
                {
                    var next = s.LastUpdatedAt.Value.AddMinutes(hwConfig.UpdateIntervalMinutes);
                    var remaining = next - DateTime.UtcNow;
                    if (remaining.TotalSeconds > 0)
                        s.Message = (s.Message ?? "") + $" | 下次自动更新: {remaining.TotalMinutes:F0} 分钟后";
                    else
                        s.Message = (s.Message ?? "") + " | 即将自动更新";
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 根据测速结果更新DNS记录（客户端报告时自动触发）
    /// </summary>
    public async Task UpdateDnsAsync(IspType isp, List<IpTestResult> bestResults)
    {
        var config = _store.GetConfig();
        var hwConfig = config.HuaweiDns;
        var ispKey = isp.ToString();

        var aggregatedResults = bestResults
            .OrderByDescending(r => r.Score)
            .GroupBy(r => r.IpAddress)
            .Select(g => g.First())
            .Take(config.TopN)
            .ToList();

        if (aggregatedResults.Count == 0)
        {
            aggregatedResults = AggregateLatestResultsForIsp(isp, config.TopN);
        }

        if (!hwConfig.Enabled)
        {
            UpdatePreviewStatus(ispKey, aggregatedResults, hwConfig, false, "华为云 DNS 未启用，当前仅展示本次汇总 TopN");
            _logger.LogDebug("Huawei DNS update is disabled, skipping for {Isp}", isp);
            return;
        }

        // 如果配置了定时更新，则不在报告时立即触发（交给定时器）
        if (hwConfig.UpdateIntervalMinutes > 0)
        {
            UpdatePreviewStatus(ispKey, aggregatedResults, hwConfig, false,
                $"已汇总本次 TopN，等待定时更新（{hwConfig.UpdateIntervalMinutes} 分钟间隔）");
            _logger.LogDebug("DNS auto-update interval is set ({Interval} min), skipping immediate update for {Isp}",
                hwConfig.UpdateIntervalMinutes, isp);
            return;
        }

        // UpdateIntervalMinutes == 0 表示每次报告都立即更新
        if (aggregatedResults.Count == 0)
        {
            UpdatePreviewStatus(ispKey, aggregatedResults, hwConfig, false, "没有可用的测速结果");
            _logger.LogWarning("No IPs to update for ISP {Isp}", isp);
            return;
        }

        await UpdateRecordSetForIspAsync(ispKey, aggregatedResults, hwConfig);
    }

    /// <summary>
    /// 手动触发 DNS 更新（从 WebUI 调用）
    /// </summary>
    public async Task<List<DnsUpdateStatus>> ManualUpdateAsync(string? ispFilter)
    {
        var config = _store.GetConfig();
        var hwConfig = config.HuaweiDns;

        if (!hwConfig.Enabled)
        {
            return [new DnsUpdateStatus { Message = "华为云 DNS 未启用", Success = false }];
        }

        var isps = string.IsNullOrEmpty(ispFilter)
            ? new[] { "Telecom", "Unicom", "Mobile" }
            : new[] { ispFilter };

        var results = new List<DnsUpdateStatus>();

        foreach (var ispKey in isps)
        {
            if (!Enum.TryParse<IspType>(ispKey, out var ispEnum))
            {
                results.Add(new DnsUpdateStatus { Isp = ispKey, Success = false, Message = "无效的运营商" });
                continue;
            }

            var aggregatedResults = AggregateLatestResultsForIsp(ispEnum, config.TopN);
            if (aggregatedResults.Count == 0)
            {
                var status = new DnsUpdateStatus
                {
                    Isp = ispKey,
                    Success = false,
                    Message = "没有可用的测速结果"
                };
                _lastStatus[ispKey] = status;
                results.Add(status);
                continue;
            }

            var result = await UpdateRecordSetForIspAsync(ispKey, aggregatedResults, hwConfig);
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// 测试 IAM 认证是否成功
    /// </summary>
    public async Task<bool> TestAuthAsync()
    {
        var config = _store.GetConfig();
        var hwConfig = config.HuaweiDns;
        
        // 强制不使用缓存，以确保确实能连通
        var oldToken = _cachedToken;
        _cachedToken = null;
        try
        {
            await GetIamTokenAsync(hwConfig);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Huawei IAM auth test failed");
            return false;
        }
        finally
        {
            // 如果旧的有并且还在有效期内，恢复它（虽然实际上失败了可能配置已经改了，但无妨）
            // 简单点，我们就不管恢复了，下次真正更新时会重新获取
        }
    }

    // ==================== Internal ====================

    /// <summary>
    /// 聚合某运营商所有客户端的最新测速结果，取最优的 topN 个结果（含详细数据）
    /// 逻辑：取每个客户端最新一次报告的所有结果，合并后按 score 降序排列，去重IP，取前 topN
    /// </summary>
    public List<IpTestResult> AggregateLatestResultsForIsp(IspType isp, int topN)
    {
        var history = _store.GetHistory(500);
        var clients = _store.GetClients();

        var ispClients = clients.Where(c => c.Isp == isp).Select(c => c.ClientId).ToHashSet();

        var latestPerClient = history
            .Where(h => h.Isp == isp && ispClients.Contains(h.ClientId))
            .GroupBy(h => h.ClientId)
            .Select(g => g.OrderByDescending(h => h.CompletedAt).First())
            .ToList();

        var allResults = latestPerClient
            .SelectMany(h => h.Results)
            .OrderByDescending(r => r.Score)
            .ToList();

        var uniqueResults = new List<IpTestResult>();
        var seen = new HashSet<string>();
        foreach (var r in allResults)
        {
            if (seen.Add(r.IpAddress))
            {
                uniqueResults.Add(r);
                if (uniqueResults.Count >= topN)
                    break;
            }
        }

        _logger.LogInformation("Aggregated {Count} unique IPs for {Isp} from {ClientCount} clients",
            uniqueResults.Count, isp, latestPerClient.Count);

        return uniqueResults;
    }

    /// <summary>
    /// 调用华为云 DNS API 更新指定运营商的记录集
    /// PUT /v2.1/zones/{zone_id}/recordsets/{recordset_id}
    /// </summary>
    private async Task<DnsUpdateStatus> UpdateRecordSetForIspAsync(
        string ispKey, List<IpTestResult> testResults, HuaweiDnsConfig hwConfig)
    {
        var ips = testResults.Select(r => r.IpAddress).ToList();
        var status = new DnsUpdateStatus
        {
            Isp = ispKey,
            Results = testResults,
            LastUpdatedAt = DateTime.UtcNow,
        };

        if (hwConfig.Records.TryGetValue(ispKey, out var previewRecordConfig))
        {
            status.Domain = previewRecordConfig.Domain;
        }

        status.Success = false;
        status.Message = "已汇总本次 TopN，正在尝试更新 DNS";
        _lastStatus[ispKey] = status;

        try
        {
            if (!hwConfig.Records.TryGetValue(ispKey, out var recordConfig)
                || string.IsNullOrEmpty(recordConfig.ZoneId)
                || string.IsNullOrEmpty(recordConfig.RecordSetId))
            {
                status.Success = false;
                status.Message = $"{ispKey} 未配置 ZoneId 或 RecordSetId";
                _lastStatus[ispKey] = status;
                return status;
            }

            status.Domain = recordConfig.Domain;

            var token = await GetIamTokenAsync(hwConfig);

            var client = _httpFactory.CreateClient();
            var url = $"{hwConfig.Endpoint.TrimEnd('/')}/v2.1/zones/{recordConfig.ZoneId}/recordsets/{recordConfig.RecordSetId}";

            var requestBody = new
            {
                name = recordConfig.Domain.EndsWith('.') ? recordConfig.Domain : recordConfig.Domain + ".",
                type = "A",
                ttl = recordConfig.Ttl > 0 ? recordConfig.Ttl : 60,
                records = ips,
            };

            var json = JsonSerializer.Serialize(requestBody);
            var request = new HttpRequestMessage(HttpMethod.Put, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Auth-Token", token);

            _logger.LogInformation("Updating Huawei DNS for {Isp}: {Url} with IPs: {Ips}",
                ispKey, url, string.Join(", ", ips));

            var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                status.Success = true;
                status.Message = $"更新成功 (HTTP {(int)response.StatusCode})";
                _logger.LogInformation("Huawei DNS update succeeded for {Isp}: {Response}",
                    ispKey, responseBody);
            }
            else
            {
                status.Success = false;
                status.Message = $"API 返回错误 (HTTP {(int)response.StatusCode}): {responseBody}";
                _logger.LogError("Huawei DNS update failed for {Isp}: HTTP {Code} - {Body}",
                    ispKey, (int)response.StatusCode, responseBody);
            }
        }
        catch (Exception ex)
        {
            status.Success = false;
            status.Message = $"异常: {ex.Message}";
            _logger.LogError(ex, "Huawei DNS update exception for {Isp}", ispKey);
        }

        _lastStatus[ispKey] = status;
        return status;
    }

    private void UpdatePreviewStatus(
        string ispKey,
        List<IpTestResult> testResults,
        HuaweiDnsConfig hwConfig,
        bool success,
        string message)
    {
        var status = new DnsUpdateStatus
        {
            Isp = ispKey,
            Results = testResults,
            LastUpdatedAt = DateTime.UtcNow,
            Success = success,
            Message = message,
        };

        if (hwConfig.Records.TryGetValue(ispKey, out var recordConfig))
        {
            status.Domain = recordConfig.Domain;
        }

        _lastStatus[ispKey] = status;
    }

    /// <summary>
    /// 获取华为云 IAM Token（带缓存）
    /// POST {iamEndpoint}/v3/auth/tokens
    /// </summary>
    private async Task<string> GetIamTokenAsync(HuaweiDnsConfig hwConfig)
    {
        await _tokenLock.WaitAsync();
        try
        {
            if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
            {
                return _cachedToken;
            }

            var client = _httpFactory.CreateClient();
            var url = $"{hwConfig.IamEndpoint.TrimEnd('/')}/v3/auth/tokens";

            var requestBody = new
            {
                auth = new
                {
                    identity = new
                    {
                        methods = new[] { "password" },
                        password = new
                        {
                            user = new
                            {
                                name = hwConfig.IamUser,
                                password = hwConfig.IamPassword,
                                domain = new { name = hwConfig.IamDomainName }
                            }
                        }
                    },
                    scope = new
                    {
                        project = new { id = hwConfig.ProjectId }
                    }
                }
            };

            var json = JsonSerializer.Serialize(requestBody);
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            _logger.LogInformation("Requesting IAM token from {Url}", url);
            var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"IAM token request failed (HTTP {(int)response.StatusCode}): {errorBody}");
            }

            if (!response.Headers.TryGetValues("X-Subject-Token", out var tokenValues))
            {
                throw new InvalidOperationException("IAM response missing X-Subject-Token header");
            }

            _cachedToken = tokenValues.First();
            _tokenExpiry = DateTime.UtcNow.AddHours(24);

            _logger.LogInformation("IAM token obtained successfully, expires at {Expiry}", _tokenExpiry);
            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }
}
