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
        public HashSet<string> PendingTriggerClients { get; } = [];
        public bool Finalizing { get; set; }
        public bool Finalized { get; set; }
    }

    private readonly DataStore _store;
    private readonly DnsUpdateService _dns;
    private readonly IpPoolService _ipPool;
    private readonly ILogger<RoundCoordinatorService> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, RoundState> _rounds = new();
    private readonly Dictionary<string, DateTime> _latestFinalizedStartAtUtc = new();
    private readonly HashSet<string> _manualUpdateClients = [];

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

    public (string TaskId, DateTime ScheduledAtUtc, bool IsImmediateDispatch) RegisterClient(IspType isp, string clientId)
    {
        var config = _store.GetConfig();
        var ispKey = isp.ToString();

        lock (_lock)
        {
            var startAtUtc = _rounds.TryGetValue(ispKey, out var existingState) && !existingState.Finalized && DateTime.UtcNow <= existingState.FinalizeAfterUtc
                ? existingState.StartAtUtc
                : GetNextRoundStartUtc(DateTime.UtcNow, config.ClientIntervalMinutes);

            if (_latestFinalizedStartAtUtc.TryGetValue(ispKey, out var latestFinalizedAt) && latestFinalizedAt >= startAtUtc)
            {
                startAtUtc = GetNextRoundStartUtc(latestFinalizedAt.AddSeconds(1), config.ClientIntervalMinutes);
            }

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
            var isImmediateDispatch = state.PendingTriggerClients.Contains(clientId);
            return (state.TaskId, state.StartAtUtc, isImmediateDispatch);
        }
    }

    public void MarkTriggerTaskDispatched(string clientId, IspType isp)
    {
        lock (_lock)
        {
            if (_rounds.TryGetValue(isp.ToString(), out var state))
            {
                state.PendingTriggerClients.Remove(clientId);
            }
        }
    }

    public List<string> TriggerImmediateRound(string? ispFilter)
    {
        var config = _store.GetConfig();
        var nowUtc = DateTime.UtcNow;
        var activeThreshold = nowUtc.AddMinutes(-5);
        var clients = _store.GetClients();
        var targetClientIds = new List<string>();

        lock (_lock)
        {
            var isps = string.IsNullOrWhiteSpace(ispFilter)
                ? new[] { "Telecom", "Unicom", "Mobile" }
                : new[] { ispFilter };

            foreach (var isp in isps)
            {
                var startAtUtc = nowUtc.AddSeconds(Math.Max(5, config.HeartbeatIntervalSeconds));

                if (!Enum.TryParse<IspType>(isp, out var ispEnum))
                    continue;

                var state = new RoundState
                {
                    Isp = ispEnum,
                    IspKey = isp,
                    TaskId = $"{isp}-{startAtUtc:yyyyMMddHHmmss}",
                    StartAtUtc = startAtUtc,
                    FinalizeAfterUtc = startAtUtc.Add(GetFinalizeGracePeriod(config)),
                };

                foreach (var client in clients.Where(c => c.Isp == ispEnum && c.Allowed && c.LastSeenAt >= activeThreshold))
                {
                    state.PendingTriggerClients.Add(client.ClientId);
                    targetClientIds.Add(client.ClientId);
                }

                _rounds[isp] = state;
            }
        }

        return targetClientIds;
    }

    public bool TriggerImmediateRoundForClient(string clientId, out IspType isp)
    {
        var config = _store.GetConfig();
        var nowUtc = DateTime.UtcNow;
        var client = _store.GetClient(clientId);
        isp = default;
        if (client is null || !client.Allowed)
            return false;

        isp = client.Isp;

        lock (_lock)
        {
            var ispKey = client.Isp.ToString();
            var startAtUtc = nowUtc.AddSeconds(Math.Max(5, config.HeartbeatIntervalSeconds));
            if (_latestFinalizedStartAtUtc.TryGetValue(ispKey, out var latestFinalizedAt) && latestFinalizedAt >= startAtUtc)
            {
                return false;
            }
            var state = new RoundState
            {
                Isp = client.Isp,
                IspKey = ispKey,
                TaskId = $"{ispKey}-{startAtUtc:yyyyMMddHHmmss}",
                StartAtUtc = startAtUtc,
                FinalizeAfterUtc = startAtUtc.Add(GetFinalizeGracePeriod(config)),
            };
            state.PendingTriggerClients.Add(clientId);
            _rounds[ispKey] = state;
            return true;
        }
    }

    public bool ConsumeImmediateTrigger(string clientId, IspType isp)
    {
        lock (_lock)
        {
            var state = EnsureActiveRoundStateLocked(isp, clientId, allowCreateForScheduledRound: true);
            if (state is not null
                && !state.Finalized
                && state.PendingTriggerClients.Remove(clientId))
            {
                return true;
            }

            return false;
        }
    }

    public void TriggerClientUpdate(string clientId)
    {
        lock (_lock)
        {
            _manualUpdateClients.Add(clientId);
        }
    }

    public bool ConsumeClientUpdateTrigger(string clientId)
    {
        lock (_lock)
        {
            return _manualUpdateClients.Remove(clientId);
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
        DateTime? currentRoundStartUtc = null;
        var statuses = new List<IspRoundStatus>();

        lock (_lock)
        {
            foreach (var ispKey in new[] { "Telecom", "Unicom", "Mobile" })
            {
                if (_rounds.TryGetValue(ispKey, out var state))
                {
                    var totalTargetClients = state.AssignedClients.Count + state.PendingTriggerClients.Count;
                    if (!state.Finalized && nowUtc <= state.FinalizeAfterUtc)
                    {
                        currentRoundStartUtc = currentRoundStartUtc.HasValue
                            ? Min(currentRoundStartUtc.Value, state.StartAtUtc)
                            : state.StartAtUtc;
                    }

                    statuses.Add(new IspRoundStatus
                    {
                        Isp = ispKey,
                        TaskId = state.TaskId,
                        ScheduledAtUtc = state.StartAtUtc,
                        FinalizeAfterUtc = state.FinalizeAfterUtc,
                        AssignedClients = totalTargetClients,
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

        if (currentRoundStartUtc.HasValue)
        {
            nextRoundStartUtc = GetNextRoundStartUtc(currentRoundStartUtc.Value.AddSeconds(1), config.ClientIntervalMinutes);
        }

        return new RoundStatusOverview
        {
            ServerNowUtc = nowUtc,
            CurrentRoundStartUtc = currentRoundStartUtc,
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

        if (state.AssignedClients.Count == 0 && state.PendingTriggerClients.Count == 0 && roundReports.Count == 0)
        {
            lock (_lock)
            {
                state.Finalized = true;
                _latestFinalizedStartAtUtc[state.IspKey] = state.StartAtUtc;
            }

            _logger.LogInformation(
                "Skipping empty round finalization for {Isp}: task={TaskId}",
                state.IspKey,
                state.TaskId);

            return "empty round skipped";
        }

        var allRoundResults = roundReports
            .SelectMany(h => h.Results)
            .OrderByDescending(r => r.Score)
            .GroupBy(r => r.IpAddress)
            .Select(g => g.First())
            .ToList();

        var qualifiedResults = allRoundResults
            .Where(r => r.DownloadSpeedKBps >= config.MinDownloadSpeedKBps)
            .Take(config.TopN)
            .ToList();

        var topResults = qualifiedResults;

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
            _latestFinalizedStartAtUtc[state.IspKey] = state.StartAtUtc;
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

    private static DateTime GetCurrentRoundStartUtc(DateTime nowUtc, int intervalMinutes)
    {
        var safeIntervalMinutes = Math.Max(1, intervalMinutes);
        var intervalTicks = TimeSpan.FromMinutes(safeIntervalMinutes).Ticks;
        var currentTicks = (nowUtc.Ticks / intervalTicks) * intervalTicks;
        return new DateTime(currentTicks, DateTimeKind.Utc);
    }

    private static TimeSpan GetFinalizeGracePeriod(ServerConfig config)
    {
        var perIpSeconds = Math.Max(1, config.TcpTestDurationSeconds) + Math.Max(1, config.DownloadDurationSeconds);
        var estimatedBatchSeconds = Math.Max(1, config.BatchSize) * perIpSeconds;
        return TimeSpan.FromSeconds(estimatedBatchSeconds + 60);
    }

    private RoundState? EnsureActiveRoundStateLocked(IspType isp, string clientId, bool allowCreateForScheduledRound)
    {
        var config = _store.GetConfig();
        var nowUtc = DateTime.UtcNow;
        var ispKey = isp.ToString();

        if (!_rounds.TryGetValue(ispKey, out var state) || state.Finalized || nowUtc > state.FinalizeAfterUtc)
        {
            if (!allowCreateForScheduledRound)
                return null;

            var currentStartUtc = GetCurrentRoundStartUtc(nowUtc, config.ClientIntervalMinutes);
            if (_latestFinalizedStartAtUtc.TryGetValue(ispKey, out var latestFinalizedAt) && latestFinalizedAt >= currentStartUtc)
            {
                return null;
            }
            state = new RoundState
            {
                Isp = isp,
                IspKey = ispKey,
                TaskId = $"{ispKey}-{currentStartUtc:yyyyMMddHHmmss}",
                StartAtUtc = currentStartUtc,
                FinalizeAfterUtc = currentStartUtc.Add(GetFinalizeGracePeriod(config)),
            };
            _rounds[ispKey] = state;
        }

        if (!state.Finalized
            && nowUtc >= state.StartAtUtc
            && nowUtc <= state.FinalizeAfterUtc
            && !state.AssignedClients.Contains(clientId)
            && !state.ReportedClients.Contains(clientId)
            && !state.PendingTriggerClients.Contains(clientId))
        {
            state.PendingTriggerClients.Add(clientId);
        }

        return state;
    }

    private static DateTime Min(DateTime left, DateTime right) => left <= right ? left : right;
}
