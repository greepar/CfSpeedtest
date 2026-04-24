using System.Collections.Concurrent;
using System.Text.Json;
using CfSpeedtest.Shared;

namespace CfSpeedtest.Server.Services;

/// <summary>
/// 数据存储服务 - 使用JSON文件持久化
/// </summary>
public class DataStore
{
    private readonly string _dataDir;
    private readonly ILogger<DataStore> _logger;
    private readonly Lock _lock = new();

    private ServerConfig _config = new();
    private readonly ConcurrentDictionary<string, ClientInfo> _clients = new();
    private readonly ConcurrentBag<TestHistory> _history = [];
    private Dictionary<string, List<string>> _apiIpPools = new()
    {
        ["Telecom"] = [],
        ["Unicom"] = [],
        ["Mobile"] = []
    };

    public DataStore(ILogger<DataStore> logger, IConfiguration configuration)
    {
        _logger = logger;
        _dataDir = configuration.GetValue<string>("DataDir") ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(_dataDir);
        Load();
    }

    // ===== Config =====
    public ServerConfig GetConfig()
    {
        lock (_lock) return _config;
    }

    public void SaveConfig(ServerConfig config)
    {
        lock (_lock)
        {
            _config = config;
            PersistFile("config.json", config);
        }
    }

    // ===== IP Pool =====
    public List<string> GetApiIpPool(string isp)
    {
        lock (_lock) return _apiIpPools.TryGetValue(isp, out var pool) ? [.. pool] : [];
    }

    public void MergeApiIps(string isp, IEnumerable<string> ips)
    {
        lock (_lock)
        {
            if (!_apiIpPools.ContainsKey(isp)) _apiIpPools[isp] = [];
            
            var set = new HashSet<string>(_apiIpPools[isp]);
            foreach (var ip in ips)
            {
                var trimmed = ip.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    set.Add(trimmed);
            }
            _apiIpPools[isp] = [.. set];
            PersistFile("ippool.json", _apiIpPools);
        }
    }

    /// <summary>
    /// 清空 API 自动拉取得到的 IP 池，不影响手动维护的 IP。
    /// </summary>
    public void ClearApiIpPool(string? isp = null)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(isp))
            {
                foreach (var key in _apiIpPools.Keys.ToList())
                {
                    _apiIpPools[key] = [];
                }
            }
            else
            {
                _apiIpPools[isp] = [];
            }

            PersistFile("ippool.json", _apiIpPools);
        }
    }

    public void ReplaceIpPool(string isp, IEnumerable<string> ips)
    {
        lock (_lock)
        {
            var normalized = ips
                .Select(ip => ip.Trim())
                .Where(ip => !string.IsNullOrEmpty(ip))
                .Distinct()
                .ToList();

            _apiIpPools[isp] = normalized;

            if (!_config.IpSources.ContainsKey(isp))
            {
                _config.IpSources[isp] = new();
            }

            _config.IpSources[isp].ManualIps = [];

            PersistFile("ippool.json", _apiIpPools);
            PersistFile("config.json", _config);
        }
    }

    public void AddManualIps(string isp, IEnumerable<string> ips)
    {
        lock (_lock)
        {
            if (!_config.IpSources.ContainsKey(isp))
            {
                _config.IpSources[isp] = new();
            }

            var existing = new HashSet<string>(_config.IpSources[isp].ManualIps, StringComparer.OrdinalIgnoreCase);
            foreach (var ip in ips.Select(ip => ip.Trim()).Where(ip => !string.IsNullOrWhiteSpace(ip)))
            {
                if (existing.Add(ip))
                {
                    _config.IpSources[isp].ManualIps.Add(ip);
                }
            }

            PersistFile("config.json", _config);
        }
    }

    public void ReplaceManualIpPool(string isp, IEnumerable<string> ips)
    {
        lock (_lock)
        {
            if (!_config.IpSources.ContainsKey(isp))
            {
                _config.IpSources[isp] = new();
            }

            _config.IpSources[isp].ManualIps = ips
                .Select(ip => ip.Trim())
                .Where(ip => !string.IsNullOrWhiteSpace(ip))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            PersistFile("config.json", _config);
        }
    }

    public bool RemovePoolIp(string isp, string ip, string source)
    {
        lock (_lock)
        {
            var removed = false;

            if (string.Equals(source, "manual", StringComparison.OrdinalIgnoreCase))
            {
                if (_config.IpSources.TryGetValue(isp, out var sourceConfig))
                {
                    var before = sourceConfig.ManualIps.Count;
                    sourceConfig.ManualIps = sourceConfig.ManualIps
                        .Where(x => !string.Equals(x, ip, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    removed = sourceConfig.ManualIps.Count != before;
                    if (removed)
                    {
                        PersistFile("config.json", _config);
                    }
                }

                return removed;
            }

            if (_apiIpPools.TryGetValue(isp, out var apiPool))
            {
                var filtered = apiPool
                    .Where(x => !string.Equals(x, ip, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                removed = filtered.Count != apiPool.Count;
                if (removed)
                {
                    _apiIpPools[isp] = filtered;
                    PersistFile("ippool.json", _apiIpPools);
                }
            }

            return removed;
        }
    }

    /// <summary>
    /// 从指定运营商的IP池中移除一批IP
    /// </summary>
    public int RemoveIpsFromPool(string isp, IEnumerable<string> ipsToRemove)
    {
        lock (_lock)
        {
            var removeSet = new HashSet<string>(ipsToRemove);
            int removed = 0;

            // 从 API 池移除
            if (_apiIpPools.TryGetValue(isp, out var apiPool))
            {
                var before = apiPool.Count;
                _apiIpPools[isp] = apiPool.Where(ip => !removeSet.Contains(ip)).ToList();
                removed += before - _apiIpPools[isp].Count;
            }

            if (removed > 0)
            {
                PersistFile("ippool.json", _apiIpPools);
            }

            return removed;
        }
    }

    // ===== Clients =====
    public List<ClientInfo> GetClients() => [.. _clients.Values];

    public ClientInfo? GetClient(string clientId) =>
        _clients.TryGetValue(clientId, out var c) ? c : null;

    public void UpsertClient(ClientInfo client)
    {
        _clients[client.ClientId] = client;
        PersistFile("clients.json", _clients.Values.ToList());
    }

    public bool RemoveClient(string clientId)
    {
        var removed = _clients.TryRemove(clientId, out _);
        if (removed)
        {
            PersistFile("clients.json", _clients.Values.ToList());
        }
        return removed;
    }

    public bool SetClientAllowed(string clientId, bool allowed)
    {
        if (!_clients.TryGetValue(clientId, out var client))
        {
            return false;
        }

        client.Allowed = allowed;
        if (!allowed)
        {
            client.IsOnline = false;
        }

        _clients[clientId] = client;
        PersistFile("clients.json", _clients.Values.ToList());
        return true;
    }

    public bool UpdateClientMetadata(string clientId, IspType isp, string name)
    {
        if (!_clients.TryGetValue(clientId, out var client))
        {
            return false;
        }

        client.Isp = isp;
        client.Name = name;
        _clients[clientId] = client;
        PersistFile("clients.json", _clients.Values.ToList());
        return true;
    }

    // ===== History =====
    public List<TestHistory> GetHistory(int limit = 100)
    {
        return [.. _history.OrderByDescending(h => h.CompletedAt).Take(limit)];
    }

    public List<TestHistory> GetHistoryByIsp(IspType isp, int limit = 50)
    {
        return [.. _history.Where(h => h.Isp == isp).OrderByDescending(h => h.CompletedAt).Take(limit)];
    }

    public void AddHistory(TestHistory history)
    {
        _history.Add(history);
        ApplyHistoryRetention();
    }

    public int ApplyHistoryRetention()
    {
        var retentionDays = _config.HistoryRetentionDays;
        var before = _history.Count;

        IEnumerable<TestHistory> retained = _history;
        if (retentionDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            retained = retained.Where(h => h.CompletedAt >= cutoff);
        }

        var kept = retained
            .OrderByDescending(h => h.CompletedAt)
            .Take(500)
            .ToList();

        _history.Clear();
        foreach (var item in kept.OrderBy(h => h.CompletedAt))
        {
            _history.Add(item);
        }

        PersistFile("history.json", kept);
        return before - _history.Count;
    }

    public int ClearHistory()
    {
        var count = _history.Count;
        _history.Clear();
        PersistFile("history.json", new List<TestHistory>());
        return count;
    }

    public bool RemoveHistory(string historyId)
    {
        lock (_lock)
        {
            var kept = _history
                .Where(h => !string.Equals(h.Id, historyId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(h => h.CompletedAt)
                .ToList();

            if (kept.Count == _history.Count)
            {
                return false;
            }

            _history.Clear();
            foreach (var item in kept)
            {
                _history.Add(item);
            }

            PersistFile("history.json", kept);
            return true;
        }
    }

    // ===== Persistence =====
    private void PersistFile<T>(string filename, T data)
    {
        try
        {
            var path = Path.Combine(_dataDir, filename);
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist {File}", filename);
        }
    }

    private void Load()
    {
        try
        {
            LoadFile("config.json", ref _config);

            var clients = new List<ClientInfo>();
            if (LoadFile("clients.json", ref clients))
            {
                foreach (var c in clients)
                    _clients[c.ClientId] = c;
            }

            var history = new List<TestHistory>();
            if (LoadFile("history.json", ref history))
            {
                foreach (var h in history)
                    _history.Add(h);
            }

            var apiPools = new Dictionary<string, List<string>>();
            if (LoadFile("ippool.json", ref apiPools))
            {
                _apiIpPools = apiPools;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load data");
        }
    }

    private bool LoadFile<T>(string filename, ref T target) where T : new()
    {
        var path = Path.Combine(_dataDir, filename);
        if (!File.Exists(path)) return false;
        try
        {
            var json = File.ReadAllText(path);
            var result = JsonSerializer.Deserialize<T>(json);
            if (result is not null)
            {
                target = result;
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load {File}", filename);
        }
        return false;
    }
}
