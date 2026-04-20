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

            // 从手动 IP 列表移除
            if (_config.IpSources.TryGetValue(isp, out var source))
            {
                var before = source.ManualIps.Count;
                source.ManualIps = source.ManualIps.Where(ip => !removeSet.Contains(ip)).ToList();
                removed += before - source.ManualIps.Count;
            }

            if (removed > 0)
            {
                PersistFile("ippool.json", _apiIpPools);
                PersistFile("config.json", _config);
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
        // 只保留最近500条
        PersistFile("history.json", _history.OrderByDescending(h => h.CompletedAt).Take(500).ToList());
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
