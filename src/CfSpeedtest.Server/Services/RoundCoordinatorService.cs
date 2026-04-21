using CfSpeedtest.Shared;

namespace CfSpeedtest.Server.Services;

/// <summary>
/// 统一轮次协调服务。
/// 服务端按固定时间片组织各运营商客户端在同一轮开始测速，并在本轮结束后统一收口。
/// </summary>
public class RoundCoordinatorService : BackgroundService
{
    private sealed class RoundState
    {
        public string IspKey { get; init; } = string.Empty;
        public IspType Isp { get; init; }
        public string TaskId { get; init; } = string.Empty;
        public DateTime StartAtUtc { get; init; }
        public DateTime FinalizeAfterUtc { get; init; }
        public HashSet<string> AssignedClients { get; } = [];
        public HashSet<string> ReportedClients { get; } = [];
        public bool Finalizing { get; set; }
        public bool Finalized { get; set; }
    }

    private readonly DataStore _store;
    private readonly DnsUpdateService _dns;
    private readonly IpPoolService _ipPool;
    private readonly ILogger<RoundCoordinatorService> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, RoundState> _rounds = new();

    public RoundCoordinatorService(
        DataStore store,
        DnsUpdateService dns,
        IpPoolService ipPool,
        ILogger<RoundCoordinatorService> logger)
    {
        _store = store;
        _dns = dns;
        _ipPool = ipPool;
        _logger = logger;
    }

    public (string TaskId, DateTime ScheduledAtUtc) RegisterClient(IspType isp, string clientId)
    {
        var config = _store.GetConfig();
        var startAtUtc = GetNextRoundStartUtc(DateTime.UtcNow, config.ClientIntervalMinutes);
        var ispKey = isp.ToString();

        lock (_lock)
        {
            if (!_rounds.TryGetValue(ispKey, out var state) || state.StartAtUtc != startAtUtc)
            {
                state = new RoundState
                {
                    Isp = isp,
                    IspKey = ispKey,
                    TaskId = $"{ispKey}-{startAtUtc:yyyyMMddHHmmss}",
                    StartAtUtc = startAtUtc,
                    FinalizeAfterUtc = startAtUtc.Add(GetFinalizeGracePeriod(config)),
                };
                _rounds[ispKey] = state;
            }

            state.AssignedClients.Add(clientId);
            return (state.TaskId, state.StartAtUtc);
        }
    }

    public async Task<string> HandleReportAsync(SpeedTestReport report)
    {
        RoundState? finalizeState = null;
        int assigned = 0;
        int reported = 0;

        lock (_lock)
        {
            if (_rounds.TryGetValue(report.Isp.ToString(), out var state)
                && state.TaskId == report.TaskId)
            {
                state.ReportedClients.Add(report.ClientId);
                assigned = state.AssignedClients.Count;
                reported = state.ReportedClients.Count;

                if (!state.Finalized && !state.Finalizing && assigned > 0 && reported >= assigned)
                {
                    state.Finalizing = true;
                    finalizeState = state;
                }
            }
        }

        if (finalizeState is not null)
        {
            var summary = await FinalizeRoundAsync(finalizeState);
            return $"Report received, {summary}";
        }

        if (assigned > 0)
        {
            return $"Report received, waiting for this round to finish ({reported}/{assigned} clients reported)";
        }

        return "Report received";
    }

    public RoundStatusOverview GetStatusOverview()
    {
        var config = _store.GetConfig();
        var nowUtc = DateTime.UtcNow;
        var nextRoundStartUtc = GetNextRoundStartUtc(nowUtc, config.ClientIntervalMinutes);
        var statuses = new List<IspRoundStatus>();

        lock (_lock)
        {
            foreach (var ispKey in new[] { "Telecom", "Unicom", "Mobile" })
            {
                if (_rounds.TryGetValue(ispKey, out var state))
                {
                    statuses.Add(new IspRoundStatus
                    {
                        Isp = ispKey,
                        TaskId = state.TaskId,
                        ScheduledAtUtc = state.StartAtUtc,
                        FinalizeAfterUtc = state.FinalizeAfterUtc,
                        AssignedClients = state.AssignedClients.Count,
                        ReportedClients = state.ReportedClients.Count,
                        Finalizing = state.Finalizing,
                        Finalized = state.Finalized,
                    });
                }
                else
                {
                    statuses.Add(new IspRoundStatus
                    {
                        Isp = ispKey,
                        TaskId = $"{ispKey}-{nextRoundStartUtc:yyyyMMddHHmmss}",
                        ScheduledAtUtc = nextRoundStartUtc,
                        FinalizeAfterUtc = nextRoundStartUtc.Add(GetFinalizeGracePeriod(config)),
                        AssignedClients = 0,
                        ReportedClients = 0,
                    });
                }
            }
        }

        return new RoundStatusOverview
        {
            ServerNowUtc = nowUtc,
            NextRoundStartUtc = nextRoundStartUtc,
            ClientIntervalMinutes = config.ClientIntervalMinutes,
            Isps = statuses,
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            RoundState[] expiredStates;
            lock (_lock)
            {
                expiredStates = _rounds.Values
                    .Where(r => !r.Finalized && !r.Finalizing && DateTime.UtcNow >= r.FinalizeAfterUtc)
                    .ToArray();

                foreach (var state in expiredStates)
                {
                    state.Finalizing = true;
                }
            }

            foreach (var state in expiredStates)
            {
                try
                {
                    await FinalizeRoundAsync(state);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to finalize expired round {TaskId}", state.TaskId);
                    lock (_lock)
                    {
                        state.Finalizing = false;
                    }
                }
            }
        }
    }

    private async Task<string> FinalizeRoundAsync(RoundState state)
    {
        var config = _store.GetConfig();
        var roundReports = _store.GetHistory(500)
            .Where(h => h.Isp == state.Isp && h.TaskId == state.TaskId)
            .ToList();

        var topResults = roundReports
            .SelectMany(h => h.Results)
            .OrderByDescending(r => r.Score)
            .GroupBy(r => r.IpAddress)
            .Select(g => g.First())
            .Take(config.TopN)
            .ToList();

        int removed = 0;
        if (config.AutoCleanupEnabled && topResults.Count > 0)
        {
            var keepIps = topResults.Select(r => r.IpAddress).ToHashSet();
            var poolIps = _store.GetConfig().IpSources.TryGetValue(state.IspKey, out var source)
                ? source.ManualIps.Concat(_store.GetApiIpPool(state.IspKey)).Distinct().ToList()
                : _store.GetApiIpPool(state.IspKey);
            var removeIps = poolIps.Where(ip => !keepIps.Contains(ip)).ToList();
            if (removeIps.Count > 0)
            {
                removed = _store.RemoveIpsFromPool(state.IspKey, removeIps);
            }

            await _ipPool.RefreshFromApiAsync(state.IspKey);
        }

        await _dns.UpdateDnsAsync(state.Isp, topResults);

        lock (_lock)
        {
            state.Finalized = true;
        }

        _logger.LogInformation(
            "Round finalized for {Isp}: task={TaskId}, assigned={Assigned}, reported={Reported}, top={TopCount}, removed={Removed}",
            state.IspKey,
            state.TaskId,
            state.AssignedClients.Count,
            state.ReportedClients.Count,
            topResults.Count,
            removed);

        return $"round finalized: kept top {topResults.Count}, removed {removed} IPs, source refresh triggered";
    }

    private static DateTime GetNextRoundStartUtc(DateTime nowUtc, int intervalMinutes)
    {
        var safeIntervalMinutes = Math.Max(1, intervalMinutes);
        var intervalTicks = TimeSpan.FromMinutes(safeIntervalMinutes).Ticks;
        var nextTicks = ((nowUtc.Ticks + intervalTicks - 1) / intervalTicks) * intervalTicks;
        return new DateTime(nextTicks, DateTimeKind.Utc);
    }

    private static TimeSpan GetFinalizeGracePeriod(ServerConfig config)
    {
        var perIpSeconds = Math.Max(1, config.TcpTestDurationSeconds) + Math.Max(1, config.DownloadDurationSeconds);
        var estimatedBatchSeconds = Math.Max(1, config.BatchSize) * perIpSeconds;
        return TimeSpan.FromSeconds(estimatedBatchSeconds + 60);
    }
}
