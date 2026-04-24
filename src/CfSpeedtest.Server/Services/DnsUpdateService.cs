using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using CfSpeedtest.Shared;

namespace CfSpeedtest.Server.Services;

/// <summary>
/// DNS更新服务 - 支持华为云 DNS API
/// 根据测速结果自动更新各运营商对应域名的A记录
/// </summary>
public class DnsUpdateService
{
    private readonly ILogger<DnsUpdateService> _logger;
    private readonly DataStore _store;
    private readonly IHttpClientFactory _httpFactory;

    // 各运营商最后一次更新状态
    private readonly Dictionary<string, DnsUpdateStatus> _lastStatus = new();

    public DnsUpdateService(ILogger<DnsUpdateService> logger, DataStore store, IHttpClientFactory httpFactory)
    {
        _logger = logger;
        _store = store;
        _httpFactory = httpFactory;
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
            Enum.TryParse<IspType>(isp, out var ispEnum);
            var aggregatedResults = AggregateLatestResultsForIsp(ispEnum, config.TopN);

            if (_lastStatus.TryGetValue(isp, out var status))
            {
                if (string.IsNullOrWhiteSpace(status.Domain) && hwConfig.Records.TryGetValue(isp, out var statusRec))
                {
                    status.Domain = statusRec.Domain;
                }

                if ((status.Results is null || status.Results.Count == 0) && aggregatedResults.Count > 0)
                {
                    status.Results = aggregatedResults;
                    if (string.IsNullOrWhiteSpace(status.Message))
                    {
                        status.Message = "当前展示最近一次可聚合的测速候选结果";
                    }
                }

                result.Add(status);
            }
            else
            {
                var s = new DnsUpdateStatus
                {
                    Isp = isp,
                    Message = aggregatedResults.Count > 0
                        ? "尚未执行过更新，当前展示最近一次可聚合的测速候选结果"
                        : "尚未执行过更新"
                };
                // 填充域名信息
                if (hwConfig.Records.TryGetValue(isp, out var rec))
                    s.Domain = rec.Domain;
                s.Results = aggregatedResults;
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
                        s.Message = (s.Message ?? "") + $" | 下次满足轮次自动更新窗口: {remaining.TotalMinutes:F0} 分钟后";
                    else
                        s.Message = (s.Message ?? "") + " | 已到自动更新窗口，等待下一轮收口";
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

        var aggregatedResults = SelectDnsCandidates(bestResults, config.TopN, config.MinDownloadSpeedKBps);
        var usingFallback = aggregatedResults.Count > 0 && aggregatedResults.All(r => r.DownloadSpeedKBps < config.MinDownloadSpeedKBps);

        if (aggregatedResults.Count == 0)
        {
            aggregatedResults = AggregateLatestResultsForIsp(isp, config.TopN);
            usingFallback = aggregatedResults.Count > 0 && aggregatedResults.All(r => r.DownloadSpeedKBps < config.MinDownloadSpeedKBps);
        }

        if (!hwConfig.Enabled)
        {
            UpdatePreviewStatus(ispKey, aggregatedResults, hwConfig, false,
                usingFallback
                    ? $"华为云 DNS 未启用，当前仅展示本次兜底结果（未达到最低下载速度 {config.MinDownloadSpeedKBps:F1} KB/s）"
                    : "华为云 DNS 未启用，当前仅展示本次汇总 TopN");
            _logger.LogDebug("Huawei DNS update is disabled, skipping for {Isp}", isp);
            return;
        }

        if (hwConfig.UpdateIntervalMinutes > 0)
        {
            if (_lastStatus.TryGetValue(ispKey, out var lastStatus)
                && lastStatus.LastUpdatedAt.HasValue
                && DateTime.UtcNow - lastStatus.LastUpdatedAt.Value < TimeSpan.FromMinutes(hwConfig.UpdateIntervalMinutes))
            {
                var remaining = lastStatus.LastUpdatedAt.Value.AddMinutes(hwConfig.UpdateIntervalMinutes) - DateTime.UtcNow;
                UpdatePreviewStatus(ispKey, aggregatedResults, hwConfig, false,
                    usingFallback
                        ? $"已汇总本次兜底结果（未达到最低下载速度 {config.MinDownloadSpeedKBps:F1} KB/s），等待按轮次自动更新（剩余约 {Math.Max(1, Math.Ceiling(remaining.TotalMinutes))} 分钟）"
                        : $"已汇总本次 TopN，等待按轮次自动更新（剩余约 {Math.Max(1, Math.Ceiling(remaining.TotalMinutes))} 分钟）");
                _logger.LogDebug("DNS interval not reached yet for {Isp}, skipping round-triggered update", isp);
                return;
            }

            UpdatePreviewStatus(ispKey, aggregatedResults, hwConfig, false,
                usingFallback
                    ? $"已汇总本次兜底结果（未达到最低下载速度 {config.MinDownloadSpeedKBps:F1} KB/s），本轮收口触发自动更新（{hwConfig.UpdateIntervalMinutes} 分钟间隔）"
                    : $"已汇总本次 TopN，本轮收口触发自动更新（{hwConfig.UpdateIntervalMinutes} 分钟间隔）");
        }

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
        try
        {
            ValidateSigningConfig(hwConfig);

            var client = _httpFactory.CreateClient();
            var url = $"{hwConfig.Endpoint.TrimEnd('/')}/v2/zones?limit=1";
            using var request = CreateSignedRequest(HttpMethod.Get, url, body: null, hwConfig);

            _logger.LogInformation("Testing Huawei DNS signed auth against {Url}", url);
            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Huawei DNS signed auth test failed (HTTP {(int)response.StatusCode}): {body}");
            }

            _logger.LogInformation("Huawei DNS signed auth test succeeded: {Body}", body);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Huawei DNS signed auth test failed");
            return false;
        }
    }

    public async Task<(bool Success, string Message)> TestRecordConfigAsync(string ispKey)
    {
        try
        {
            var config = _store.GetConfig();
            var hwConfig = config.HuaweiDns;
            ValidateSigningConfig(hwConfig);

            if (!hwConfig.Records.TryGetValue(ispKey, out var recordConfig))
                return (false, $"未找到 {ispKey} 的记录集配置");
            if (string.IsNullOrWhiteSpace(recordConfig.ZoneId) || string.IsNullOrWhiteSpace(recordConfig.RecordSetId))
                return (false, $"{ispKey} 未配置 Zone ID 或 RecordSet ID");

            var client = _httpFactory.CreateClient();
            var url = $"{hwConfig.Endpoint.TrimEnd('/')}/v2.1/zones/{recordConfig.ZoneId}/recordsets/{recordConfig.RecordSetId}";
            using var request = CreateSignedRequest(HttpMethod.Get, url, body: null, hwConfig);

            _logger.LogInformation("Testing Huawei DNS record config for {Isp}: {Url}", ispKey, url);
            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return (false, $"记录集测试失败 (HTTP {(int)response.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var apiName = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
            var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? string.Empty : string.Empty;
            var ttl = root.TryGetProperty("ttl", out var ttlEl) && ttlEl.ValueKind == JsonValueKind.Number ? ttlEl.GetInt32() : 0;

            if (!string.IsNullOrWhiteSpace(recordConfig.Domain))
            {
                var expected = recordConfig.Domain.EndsWith('.') ? recordConfig.Domain : recordConfig.Domain + ".";
                if (!string.Equals(apiName, expected, StringComparison.OrdinalIgnoreCase))
                    return (false, $"记录集存在，但域名不匹配。当前配置: {expected}，华为云返回: {apiName}");
            }

            return (true, $"记录集可访问，类型 {type}，TTL {ttl}，域名 {apiName}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Huawei DNS record config test failed for {Isp}", ispKey);
            return (false, ex.Message);
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
        var config = _store.GetConfig();

        var ispClients = clients.Where(c => c.Isp == isp).Select(c => c.ClientId).ToHashSet();

        var latestPerClient = history
            .Where(h => h.Isp == isp && ispClients.Contains(h.ClientId))
            .GroupBy(h => h.ClientId)
            .Select(g => g.OrderByDescending(h => h.CompletedAt).First())
            .ToList();

        var allResults = latestPerClient
            .SelectMany(h => h.Results)
            .ToList();

        var uniqueResults = SelectDnsCandidates(allResults, topN, config.MinDownloadSpeedKBps);

        _logger.LogInformation("Aggregated {Count} unique IPs for {Isp} from {ClientCount} clients",
            uniqueResults.Count, isp, latestPerClient.Count);

        return uniqueResults;
    }

    private static List<IpTestResult> SelectDnsCandidates(IEnumerable<IpTestResult> results, int topN, double minDownloadSpeedKBps)
    {
        var orderedUniqueResults = results
            .OrderByDescending(r => r.Score)
            .GroupBy(r => r.IpAddress)
            .Select(g => g.First())
            .ToList();

        var qualifiedResults = orderedUniqueResults
            .Where(r => r.DownloadSpeedKBps >= minDownloadSpeedKBps)
            .Take(topN)
            .ToList();

        if (qualifiedResults.Count > 0)
        {
            return qualifiedResults;
        }

        return orderedUniqueResults.Take(1).ToList();
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
            using var request = CreateSignedRequest(HttpMethod.Put, url, json, hwConfig);

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

    private static void ValidateSigningConfig(HuaweiDnsConfig hwConfig)
    {
        if (string.IsNullOrWhiteSpace(hwConfig.AccessKey) || string.IsNullOrWhiteSpace(hwConfig.SecretKey))
            throw new InvalidOperationException("未配置华为云 AK/SK");
        if (string.IsNullOrWhiteSpace(hwConfig.Endpoint))
            throw new InvalidOperationException("未配置华为云 DNS Endpoint");
    }

    private static HttpRequestMessage CreateSignedRequest(HttpMethod method, string url, string? body, HuaweiDnsConfig hwConfig)
    {
        ValidateSigningConfig(hwConfig);

        var uri = new Uri(url);
        var request = new HttpRequestMessage(method, uri);
        var requestBody = body ?? string.Empty;

        if (!string.IsNullOrEmpty(body))
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = uri.Authority,
            ["x-sdk-date"] = timestamp,
        };

        if (request.Content is not null)
        {
            headers["content-type"] = "application/json; charset=utf-8";
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8"
            };
        }

        request.Headers.Host = uri.Authority;
        request.Headers.TryAddWithoutValidation("X-Sdk-Date", timestamp);

        var signedHeaders = string.Join(";", headers.Keys);
        var canonicalRequest = string.Join("\n",
            method.Method.ToUpperInvariant(),
            BuildCanonicalUri(uri.AbsolutePath),
            BuildCanonicalQueryString(uri.Query),
            BuildCanonicalHeaders(headers),
            signedHeaders,
            Sha256Hex(requestBody));

        var stringToSign = $"SDK-HMAC-SHA256\n{timestamp}\n{Sha256Hex(canonicalRequest)}";
        var signature = HmacSha256Hex(hwConfig.SecretKey.Trim(), stringToSign);
        var authorization = $"SDK-HMAC-SHA256 Access={hwConfig.AccessKey.Trim()}, SignedHeaders={signedHeaders}, Signature={signature}";
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        return request;
    }

    private static string BuildCanonicalUri(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath))
            return "/";

        var segments = absolutePath.Split('/', StringSplitOptions.None)
            .Select(EscapeCanonicalComponent);
        var result = string.Join('/', segments);
        if (!result.StartsWith('/'))
            result = "/" + result;
        if (!result.EndsWith('/'))
            result += "/";
        return result;
    }

    private static string BuildCanonicalQueryString(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query == "?")
            return string.Empty;

        var pairs = new List<(string Key, string Value)>();
        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = segment.IndexOf('=');
            var rawKey = idx >= 0 ? segment[..idx] : segment;
            var rawValue = idx >= 0 ? segment[(idx + 1)..] : string.Empty;
            var key = EscapeCanonicalComponent(Uri.UnescapeDataString(rawKey.Replace("+", "%20")));
            var value = EscapeCanonicalComponent(Uri.UnescapeDataString(rawValue.Replace("+", "%20")));
            pairs.Add((key, value));
        }

        return string.Join("&", pairs
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}"));
    }

    private static string BuildCanonicalHeaders(SortedDictionary<string, string> headers)
    {
        return string.Join("\n", headers.Select(h => $"{h.Key}:{NormalizeHeaderValue(h.Value)}")) + "\n";
    }

    private static string NormalizeHeaderValue(string value)
    {
        return string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string EscapeCanonicalComponent(string value)
    {
        return Uri.EscapeDataString(value)
            .Replace("%7E", "~")
            .Replace("+", "%20")
            .Replace("*", "%2A");
    }

    private static string Sha256Hex(string value)
    {
        return ConvertToHex(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string HmacSha256Hex(string secret, string value)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return ConvertToHex(hmac.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }

    private static string ConvertToHex(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
