using CfSpeedtest.Shared;
using System.Net;

namespace CfSpeedtest.Server.Services;

/// <summary>
/// IP池管理服务 - 支持手动输入和API拉取
/// </summary>
public class IpPoolService : BackgroundService
{
    private readonly DataStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<IpPoolService> _logger;

    public IpPoolService(DataStore store, IHttpClientFactory httpClientFactory, ILogger<IpPoolService> logger)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动时先清空 API 自动拉取池，再重新拉取，避免沿用上次缓存结果
        _store.ClearApiIpPool();

        // 启动时立即拉取一次
        await RefreshFromApiAsync();

        while (!stoppingToken.IsCancellationRequested)
        {
            var config = _store.GetConfig();
            var interval = config.ApiRefreshIntervalMinutes;
            if (interval <= 0) interval = 60;

            await Task.Delay(TimeSpan.FromMinutes(interval), stoppingToken);
            await RefreshFromApiAsync();
        }
    }

    public async Task RefreshFromApiAsync(string? specificIsp = null)
    {
        var config = _store.GetConfig();
        
        var ispsToRefresh = specificIsp != null 
            ? new[] { specificIsp } 
            : ["Telecom", "Unicom", "Mobile"];

        foreach (var isp in ispsToRefresh)
        {
            if (!config.IpSources.TryGetValue(isp, out var sourceConfig))
            {
                continue;
            }

            if (sourceConfig.FetchSources == null || sourceConfig.FetchSources.Count == 0)
            {
                continue;
            }

            try
            {
                var ips = new List<string>();

                foreach (var source in sourceConfig.FetchSources)
                {
                    if (string.IsNullOrWhiteSpace(source.Value)) continue;

                    var fetched = await FetchIpsFromSourceAsync(source);
                    ips.AddRange(fetched);
                }

                if (ips.Count > 0)
                {
                    if (config.AutoCleanupEnabled)
                    {
                        var manualIps = sourceConfig.ManualIps ?? [];
                        var existingApiIps = _store.GetApiIpPool(isp);
                        var existingAll = manualIps.Concat(existingApiIps).ToHashSet();
                        // Keep at least two full batches so clients can request a second batch of different IPs.
                        var targetPoolSize = Math.Max(config.BatchSize * 2, config.TopN);
                        var needCount = Math.Max(0, targetPoolSize - existingAll.Count);
                        var refillIps = ips.Where(ip => !existingAll.Contains(ip)).Take(needCount).ToList();

                        if (refillIps.Count > 0)
                        {
                            _store.MergeApiIps(isp, refillIps);
                        }

                        _logger.LogInformation(
                            "Fetched {FetchedCount} IPs for {Isp}; auto-cleanup enabled, refilled {AddedCount} IPs to target pool size {TargetPoolSize}",
                            ips.Count,
                            isp,
                            refillIps.Count,
                            targetPoolSize);
                    }
                    else
                    {
                        _store.MergeApiIps(isp, ips);
                        _logger.LogInformation("Fetched {Count} IPs for {Isp} from API & DoH", ips.Count, isp);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch IPs for {Isp}", isp);
            }
        }
    }

    public async Task<List<string>> FetchIpsFromSourceAsync(FetchSource source)
    {
        var ips = new List<string>();
        try
        {
            if (source.Type == FetchSourceType.Api)
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(30);
                var response = await client.GetStringAsync(source.Value);

                IEnumerable<string> apiIps = response
                    .Split(['\n', '\r', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                    .Select(NormalizeIpCandidate)
                    .Where(ip => !string.IsNullOrEmpty(ip))
                    .Select(ip => ip!);
                
                ips.AddRange(apiIps);
            }
            else if (source.Type == FetchSourceType.Cname)
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/dns-json"));
                
                var dohUrl = $"https://doh.pub/dns-query?name={Uri.EscapeDataString(source.Value.Trim())}&type=A";
                var responseStream = await client.GetStreamAsync(dohUrl);
                
                var dohResp = await System.Text.Json.JsonSerializer.DeserializeAsync(
                    responseStream, 
                    AppJsonContext.Default.DohResponse);
                
                if (dohResp?.Status == 0 && dohResp.Answer != null)
                {
                    IEnumerable<string> dohIps = dohResp.Answer
                        .Where(a => a.type == 1 && !string.IsNullOrEmpty(a.data))
                        .Select(a => NormalizeIpCandidate(a.data!))
                        .Where(ip => !string.IsNullOrEmpty(ip))
                        .Select(ip => ip!);
                    ips.AddRange(dohIps);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch from source {Type}: {Value}", source.Type, source.Value);
        }
        return [.. ips.Distinct()];
    }

    private static string? NormalizeIpCandidate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // 支持类似 162.159.36.18#备注 的格式，只保留 # 前面的 IP
        var candidate = raw.Split('#', 2)[0].Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        if (!IPAddress.TryParse(candidate, out var parsed))
        {
            return null;
        }

        return parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? parsed.ToString()
            : null;
    }

    /// <summary>
    /// 获取特定运营商的分批IP列表给客户端
    /// </summary>
    public List<string> GetBatch(string isp)
    {
        return GetBatch(isp, []);
    }

    public List<string> GetBatch(string isp, IEnumerable<string> excludeIps)
    {
        var config = _store.GetConfig();
        var manualIps = config.IpSources.TryGetValue(isp, out var source) ? source.ManualIps : [];
        var apiIps = _store.GetApiIpPool(isp);
        var excludeSet = excludeIps.Select(ip => ip.Trim()).Where(ip => !string.IsNullOrWhiteSpace(ip)).ToHashSet();
        
        var allIps = manualIps.Concat(apiIps).Distinct().Where(ip => !excludeSet.Contains(ip)).ToList();

        if (allIps.Count == 0) return [];

        var batchSize = Math.Min(config.BatchSize, allIps.Count);

        // 随机打乱后取batch
        var rng = Random.Shared;
        for (int i = allIps.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (allIps[i], allIps[j]) = (allIps[j], allIps[i]);
        }

        return allIps.Take(batchSize).ToList();
    }
}
