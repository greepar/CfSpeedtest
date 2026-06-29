using CfSpeedtest.Shared;

namespace CfSpeedtest.Server.Services;

/// <summary>
/// 统一轮次协调服务。
/// 服务端按固定时间片组织各运营商客户端在同一轮开始测速，并在本轮结束后统一收口。
/// </summary>
public class RoundCoordinatorService : BackgroundService
{
    private enum RoundPhase
    {
        Initial,
        CrossTest,
    }

    public sealed record RoundTaskDispatch(
        string TaskId,
        DateTime ScheduledAtUtc,
        bool IsImmediateDispatch,
        bool IsCrossTest,
        List<string>? IpAddresses = null);

    private sealed class RoundState
    {
        public string IspKey { get; init; } = string.Empty;
        public IspType Isp { get; init; }
        public string TaskId { get; init; } = string.Empty;
        public string CrossTaskId => $"{TaskId}-cross";
        public DateTime StartAtUtc { get; init; }
        public DateTime? CrossStartAtUtc { get; set; }
        public DateTime FinalizeAfterUtc { get; set; }
        public RoundPhase Phase { get; set; } = RoundPhase.Initial;
        public HashSet<string> AssignedClients { get; } = [];
        public HashSet<string> ReportedClients { get; } = [];
        public HashSet<string> PendingTriggerClients { get; } = [];
        public Dictionary<string, HashSet<string>> InitialResultIpsByClient { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> CrossTestIps { get; set; } = [];
        public HashSet<string> CrossAssignedClients { get; } = [];
        public HashSet<string> CrossReportedClients { get; } = [];
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

    public RoundTaskDispatch RegisterClient(IspType isp, string clientId)
    {
        var config = _store.GetConfig();
        var ispKey = isp.ToString();

        lock (_lock)
        {
            if (_rounds.TryGetValue(ispKey, out var crossState)
                && !crossState.Finalized
                && crossState.Phase == RoundPhase.CrossTest
                && DateTime.UtcNow <= crossState.FinalizeAfterUtc)
            {
                crossState.CrossAssignedClients.Add(clientId);
                var crossImmediateDispatch = crossState.PendingTriggerClients.Contains(clientId);
                return new RoundTaskDispatch(
                    crossState.CrossTaskId,
                    crossState.CrossStartAtUtc ?? DateTime.UtcNow,
                    crossImmediateDispatch,
                    IsCrossTest: true,
                    GetCrossTestIpsForClient(crossState, clientId));
            }

            var startAtUtc = _rounds.TryGetValue(ispKey, out var existingState)
                && !existingState.Finalized
                && existingState.Phase == RoundPhase.Initial
                && DateTime.UtcNow <= existingState.FinalizeAfterUtc
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
            return new RoundTaskDispatch(state.TaskId, state.StartAtUtc, isImmediateDispatch, IsCrossTest: false);
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
                && state.PendingTriggerClients.Contains(clientId))
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
        RoundState? completionState = null;
        int assigned = 0;
        int reported = 0;
        var phaseName = "round";

        lock (_lock)
        {
            if (_rounds.TryGetValue(report.Isp.ToString(), out var state)
                && state.Phase == RoundPhase.Initial
                && state.TaskId == report.TaskId)
            {
                state.ReportedClients.Add(report.ClientId);
                state.InitialResultIpsByClient[report.ClientId] = report.Results
                    .Select(r => r.IpAddress)
                    .Where(ip => !string.IsNullOrWhiteSpace(ip))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                assigned = CountInitialTargetClients(state);
                reported = state.ReportedClients.Count;
                phaseName = "initial round";

                if (!state.Finalized && !state.Finalizing && assigned > 0 && reported >= assigned)
                {
                    state.Finalizing = true;
                    completionState = state;
                }
            }
            else if (_rounds.TryGetValue(report.Isp.ToString(), out state)
                && state.Phase == RoundPhase.CrossTest
                && state.CrossTaskId == report.TaskId)
            {
                state.CrossAssignedClients.Add(report.ClientId);
                state.CrossReportedClients.Add(report.ClientId);
                assigned = CountCrossTargetClients(state);
                reported = state.CrossReportedClients.Count;
                phaseName = "cross-test round";

                if (!state.Finalized && !state.Finalizing && assigned > 0 && reported >= assigned)
                {
                    state.Finalizing = true;
                    completionState = state;
                }
            }
        }

        if (completionState is not null)
        {
            var summary = await CompleteRoundAsync(completionState);
            return $"Report received, {summary}";
        }

        if (assigned > 0)
        {
            return $"Report received, waiting for this {phaseName} to finish ({reported}/{assigned} clients reported)";
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
                    var isCrossTest = state.Phase == RoundPhase.CrossTest;
                    var scheduledAtUtc = isCrossTest ? state.CrossStartAtUtc ?? state.StartAtUtc : state.StartAtUtc;
                    var totalTargetClients = isCrossTest ? CountCrossTargetClients(state) : CountInitialTargetClients(state);
                    var reportedClients = isCrossTest ? state.CrossReportedClients.Count : state.ReportedClients.Count;
                    if (!state.Finalized && nowUtc <= state.FinalizeAfterUtc)
                    {
                        currentRoundStartUtc = currentRoundStartUtc.HasValue
                            ? Min(currentRoundStartUtc.Value, scheduledAtUtc)
                            : scheduledAtUtc;
                    }

                    statuses.Add(new IspRoundStatus
                    {
                        Isp = ispKey,
                        TaskId = isCrossTest ? state.CrossTaskId : state.TaskId,
                        Phase = isCrossTest ? "cross" : "initial",
                        ScheduledAtUtc = scheduledAtUtc,
                        FinalizeAfterUtc = state.FinalizeAfterUtc,
                        AssignedClients = totalTargetClients,
                        ReportedClients = reportedClients,
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
                    await CompleteRoundAsync(state);
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

    private async Task<string> CompleteRoundAsync(RoundState state)
    {
        if (state.Phase == RoundPhase.Initial)
        {
            var crossTestSummary = TryStartCrossTest(state);
            if (crossTestSummary is not null)
            {
                return crossTestSummary;
            }
        }

        return await FinalizeRoundAsync(state);
    }

    private async Task<string> FinalizeRoundAsync(RoundState state)
    {
        var config = _store.GetConfig();
        var initialReports = GetRoundReports(state.Isp, state.TaskId);
        var crossReports = state.Phase == RoundPhase.CrossTest
            ? GetRoundReports(state.Isp, state.CrossTaskId)
            : [];
        var useCrossReports = crossReports.Count > 0;
        var sourceReports = useCrossReports
            ? initialReports.Concat(crossReports).ToList()
            : initialReports;

        if (CountInitialTargetClients(state) == 0
            && CountCrossTargetClients(state) == 0
            && sourceReports.Count == 0)
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

        var candidateSet = state.CrossTestIps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allRoundResults = useCrossReports
            ? BuildAggregatedResults(sourceReports
                .SelectMany(h => h.Results)
                .Where(r => candidateSet.Count == 0 || candidateSet.Contains(r.IpAddress)))
            : sourceReports
                .SelectMany(h => h.Results)
                .OrderByDescending(r => r.Score)
                .GroupBy(r => r.IpAddress, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

        var topResults = allRoundResults
            .Where(r => r.DownloadSpeedKBps >= config.MinDownloadSpeedKBps)
            .OrderByDescending(r => r.Score)
            .Take(config.TopN)
            .ToList();

        int removed = 0;
        if (config.AutoCleanupEnabled && topResults.Count > 0)
        {
            var keepIps = topResults.Select(r => r.IpAddress).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var poolIps = _store.GetConfig().IpSources.TryGetValue(state.IspKey, out var source)
                ? source.ManualIps.Concat(_store.GetApiIpPool(state.IspKey)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
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
            "Round finalized for {Isp}: task={TaskId}, phase={Phase}, assigned={Assigned}, reported={Reported}, crossReports={CrossReports}, top={TopCount}, removed={Removed}",
            state.IspKey,
            useCrossReports ? state.CrossTaskId : state.TaskId,
            state.Phase,
            state.Phase == RoundPhase.CrossTest ? CountCrossTargetClients(state) : CountInitialTargetClients(state),
            state.Phase == RoundPhase.CrossTest ? state.CrossReportedClients.Count : state.ReportedClients.Count,
            crossReports.Count,
            topResults.Count,
            removed);

        return useCrossReports
            ? $"cross-test round finalized: kept top {topResults.Count}, removed {removed} IPs, source refresh triggered"
            : $"round finalized: kept top {topResults.Count}, removed {removed} IPs, source refresh triggered";
    }

    private string? TryStartCrossTest(RoundState state)
    {
        var config = _store.GetConfig();
        if (!config.CrossTestEnabled)
        {
            return null;
        }

        var initialReports = GetRoundReports(state.Isp, state.TaskId);
        var targetClientIds = initialReports
            .Select(h => h.ClientId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targetClientIds.Count < 2)
        {
            return null;
        }

        var candidateIps = BuildCrossTestIps(initialReports, config);
        if (candidateIps.Count == 0)
        {
            return null;
        }

        var ownIpsByClient = initialReports
            .GroupBy(h => h.ClientId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(h => h.Results)
                    .Select(r => r.IpAddress)
                    .Where(ip => !string.IsNullOrWhiteSpace(ip))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        var crossStartAtUtc = DateTime.UtcNow;

        lock (_lock)
        {
            if (state.Finalized || state.Phase != RoundPhase.Initial)
            {
                return null;
            }

            state.Phase = RoundPhase.CrossTest;
            state.CrossStartAtUtc = crossStartAtUtc;
            state.CrossTestIps = candidateIps;
            state.CrossAssignedClients.Clear();
            state.CrossReportedClients.Clear();
            state.PendingTriggerClients.Clear();
            state.InitialResultIpsByClient.Clear();

            foreach (var clientId in targetClientIds)
            {
                state.CrossAssignedClients.Add(clientId);
                state.PendingTriggerClients.Add(clientId);
            }

            foreach (var (clientId, ips) in ownIpsByClient)
            {
                state.InitialResultIpsByClient[clientId] = ips;
            }

            state.FinalizeAfterUtc = crossStartAtUtc.Add(GetCrossFinalizeGracePeriod(config, candidateIps.Count));
            state.Finalizing = false;
        }

        _logger.LogInformation(
            "Cross-test started for {Isp}: task={TaskId}, crossTask={CrossTaskId}, clients={ClientCount}, candidates={CandidateCount}",
            state.IspKey,
            state.TaskId,
            state.CrossTaskId,
            targetClientIds.Count,
            candidateIps.Count);

        return $"cross-test started: {candidateIps.Count} candidate IPs assigned to {targetClientIds.Count} clients";
    }

    private List<TestHistory> GetRoundReports(IspType isp, string taskId)
    {
        return _store.GetHistory(500)
            .Where(h => h.Isp == isp && h.TaskId == taskId)
            .ToList();
    }

    private static List<string> BuildCrossTestIps(List<TestHistory> initialReports, ServerConfig config)
    {
        var allResults = initialReports
            .SelectMany(h => h.Results)
            .Where(r => !string.IsNullOrWhiteSpace(r.IpAddress))
            .ToList();
        var qualifiedResults = allResults
            .Where(r => r.DownloadSpeedKBps >= config.MinDownloadSpeedKBps)
            .ToList();
        var sourceResults = qualifiedResults.Count > 0 ? qualifiedResults : allResults;

        return sourceResults
            .OrderByDescending(r => r.Score)
            .GroupBy(r => r.IpAddress, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First().IpAddress)
            .Take(GetCrossTestCandidateCount(config))
            .ToList();
    }

    private static List<IpTestResult> BuildAggregatedResults(IEnumerable<IpTestResult> results)
    {
        return results
            .Where(r => !string.IsNullOrWhiteSpace(r.IpAddress))
            .GroupBy(r => r.IpAddress, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var items = g.ToList();
                return new IpTestResult
                {
                    IpAddress = g.Key,
                    DownloadSpeedKBps = items.Average(r => r.DownloadSpeedKBps),
                    AvgLatencyMs = items.Average(r => r.AvgLatencyMs),
                    MinLatencyMs = items.Min(r => r.MinLatencyMs),
                    PacketLossRate = items.Average(r => r.PacketLossRate),
                    TcpSuccessCount = items.Sum(r => r.TcpSuccessCount),
                    TcpTotalCount = items.Sum(r => r.TcpTotalCount),
                    Score = items.Average(r => r.Score),
                };
            })
            .OrderByDescending(r => r.Score)
            .ToList();
    }

    private static int CountInitialTargetClients(RoundState state)
    {
        return state.AssignedClients
            .Concat(state.PendingTriggerClients)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static int CountCrossTargetClients(RoundState state)
    {
        return state.CrossAssignedClients.Count;
    }

    private static List<string> GetCrossTestIpsForClient(RoundState state, string clientId)
    {
        if (state.CrossTestIps.Count == 0)
        {
            return [];
        }

        if (state.InitialResultIpsByClient.TryGetValue(clientId, out var ownIps))
        {
            var crossIps = state.CrossTestIps
                .Where(ip => !ownIps.Contains(ip))
                .ToList();
            if (crossIps.Count > 0)
            {
                return crossIps;
            }
        }

        return [.. state.CrossTestIps];
    }

    private static int GetCrossTestCandidateCount(ServerConfig config)
    {
        var topN = Math.Max(1, config.TopN);
        return config.CrossTestCandidateCount > 0
            ? Math.Max(topN, config.CrossTestCandidateCount)
            : topN * 2;
    }

    private static TimeSpan GetCrossFinalizeGracePeriod(ServerConfig config, int candidateCount)
    {
        var perIpSeconds = Math.Max(1, config.TcpTestDurationSeconds) + Math.Max(1, config.DownloadDurationSeconds);
        var estimatedSeconds = Math.Max(1, candidateCount) * perIpSeconds;
        return TimeSpan.FromSeconds(estimatedSeconds + Math.Max(60, config.HeartbeatIntervalSeconds * 2));
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
        var estimatedBatchSeconds = Math.Max(1, Math.Max(config.BatchSize, config.MaxTestIpCount)) * perIpSeconds;
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

        if (state.Phase == RoundPhase.CrossTest)
        {
            return state;
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
