namespace CfSpeedtest.Shared;

/// <summary>
/// 运营商类型
/// </summary>
public enum IspType
{
    /// <summary>中国电信</summary>
    Telecom = 0,
    /// <summary>中国联通</summary>
    Unicom = 1,
    /// <summary>中国移动</summary>
    Mobile = 2,
}

/// <summary>
/// 服务端分发给客户端的测速任务
/// </summary>
public class SpeedTestTask
{
    /// <summary>任务ID</summary>
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>待测速的IP列表</summary>
    public List<string> IpAddresses { get; set; } = [];

    /// <summary>测速目标URL模板，{ip} 会被替换为实际IP</summary>
    public string TestUrl { get; set; } = string.Empty;

    /// <summary>HTTPS测速时使用的Host头</summary>
    public string TestHost { get; set; } = string.Empty;

    /// <summary>测速端口</summary>
    public int TestPort { get; set; } = 443;

    /// <summary>下载测速时长(秒)</summary>
    public int DownloadDurationSeconds { get; set; } = 10;

    /// <summary>TCP测试时长(秒)</summary>
    public int TcpTestDurationSeconds { get; set; } = 10;

    /// <summary>返回前N个最优结果</summary>
    public int TopN { get; set; } = 5;

    /// <summary>单轮测速最多测试多少个 IP（达到该数量后停止补拉）</summary>
    public int MaxTestIpCount { get; set; } = 40;

    /// <summary>最低下载速度阈值(KB/s)，低于该值不计入最终上传和DNS候选</summary>
    public double MinDownloadSpeedKBps { get; set; }

    /// <summary>下载测速限速(KB/s)，0 表示不限速</summary>
    public double MaxDownloadSpeedKBps { get; set; }

    /// <summary>客户端下次轮询间隔(分钟)</summary>
    public int ClientIntervalMinutes { get; set; } = 60;

    /// <summary>服务端统一安排的本轮开始时间(UTC)</summary>
    public DateTime ScheduledAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 单个IP的测速结果
/// </summary>
public class IpTestResult
{
    /// <summary>测速IP</summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>下载速度 (KB/s)</summary>
    public double DownloadSpeedKBps { get; set; }

    /// <summary>平均延迟 (ms)</summary>
    public double AvgLatencyMs { get; set; }

    /// <summary>最低延迟 (ms)</summary>
    public double MinLatencyMs { get; set; }

    /// <summary>丢包率 (0-1)</summary>
    public double PacketLossRate { get; set; }

    /// <summary>TCP连接成功次数</summary>
    public int TcpSuccessCount { get; set; }

    /// <summary>TCP连接总次数</summary>
    public int TcpTotalCount { get; set; }

    /// <summary>综合评分(越高越好)</summary>
    public double Score { get; set; }
}

/// <summary>
/// 客户端提交的测速报告
/// </summary>
public class SpeedTestReport
{
    /// <summary>任务ID</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>客户端ID</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>客户端运营商</summary>
    public IspType Isp { get; set; }

    /// <summary>Top N 结果</summary>
    public List<IpTestResult> Results { get; set; } = [];

    /// <summary>测速完成时间</summary>
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 客户端注册请求
/// </summary>
public class ClientRegisterRequest
{
    /// <summary>客户端ID(首次注册可为空)</summary>
    public string? ClientId { get; set; }

    /// <summary>运营商类型</summary>
    public IspType Isp { get; set; }

    /// <summary>客户端备注名</summary>
    public string? Name { get; set; }

    /// <summary>客户端版本号</summary>
    public string? Version { get; set; }

    /// <summary>客户端平台</summary>
    public string? Platform { get; set; }
}

/// <summary>
/// 客户端注册响应
/// </summary>
public class ClientRegisterResponse
{
    public string ClientId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Message { get; set; }
    public int HeartbeatIntervalSeconds { get; set; } = 5;
    public IspType EffectiveIsp { get; set; }
    public string EffectiveName { get; set; } = string.Empty;
    public string EffectiveProxyMode { get; set; } = "direct";
    public string EffectiveProxyUrl { get; set; } = string.Empty;
}

/// <summary>
/// 客户端心跳请求
/// </summary>
public class ClientHeartbeatRequest
{
    public string ClientId { get; set; } = string.Empty;
    public IspType Isp { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Platform { get; set; }
    public string? RuntimeStatus { get; set; }
    public int CurrentTaskTotalIps { get; set; }
    public int CurrentTaskTestedIps { get; set; }
    public DateTime? CurrentTaskStartedAt { get; set; }
    public string? RuntimeLog { get; set; }
}

/// <summary>
/// 客户端心跳响应
/// </summary>
public class ClientHeartbeatResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public int HeartbeatIntervalSeconds { get; set; } = 5;
    public bool ForceFetchTask { get; set; }
    public bool ForceCheckUpdate { get; set; }
    public IspType EffectiveIsp { get; set; }
    public string EffectiveName { get; set; } = string.Empty;
    public string EffectiveProxyMode { get; set; } = "direct";
    public string EffectiveProxyUrl { get; set; } = string.Empty;
}

public class ClientWsMessage
{
    public string Type { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public IspType Isp { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Platform { get; set; }
    public string? RuntimeStatus { get; set; }
    public int CurrentTaskTotalIps { get; set; }
    public int CurrentTaskTestedIps { get; set; }
    public DateTime? CurrentTaskStartedAt { get; set; }
    public string? RuntimeLog { get; set; }
    public int HeartbeatIntervalSeconds { get; set; }
    public bool ForceFetchTask { get; set; }
    public bool ForceCheckUpdate { get; set; }
    public string? Message { get; set; }
    public IspType EffectiveIsp { get; set; }
    public string? EffectiveName { get; set; }
    public string EffectiveProxyMode { get; set; } = "direct";
    public string EffectiveProxyUrl { get; set; } = string.Empty;
}

/// <summary>
/// 服务端预留白名单客户端 ID 的请求
/// </summary>
public class ClientReservationRequest
{
    public string? ClientId { get; set; }
    public string? Isp { get; set; }
    public string? Name { get; set; }
}

/// <summary>
/// 服务端预留白名单客户端 ID 的响应
/// </summary>
public class ClientReservationResponse
{
    public string ClientId { get; set; } = string.Empty;
    public string? Message { get; set; }
}

public class ClientMetadataUpdateRequest
{
    public string Name { get; set; } = string.Empty;
    public string Isp { get; set; } = string.Empty;
}

/// <summary>
/// 客户端更新检查结果
/// </summary>
public class ClientUpdateInfo
{
    public bool Enabled { get; set; }
    public string CurrentVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public bool HasUpdate { get; set; }
    public string? DownloadUrl { get; set; }
    public string? PackageFileName { get; set; }
    public string? Message { get; set; }
}

public class ClientUpdatePackageStatus
{
    public string Platform { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
}

public class ClientUpdateOverview
{
    public bool Enabled { get; set; }
    public string LatestVersion { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string ReleaseTag { get; set; } = string.Empty;
    public string GhProxyPrefix { get; set; } = string.Empty;
    public string LocalDirectory { get; set; } = string.Empty;
    public List<ClientUpdatePackageStatus> Packages { get; set; } = [];
}

public class ClientInstallScriptResponse
{
    public string Platform { get; set; } = string.Empty;
    public string ScriptType { get; set; } = string.Empty;
    public string ServiceKind { get; set; } = string.Empty;
    public string ScriptFileName { get; set; } = string.Empty;
    public string ScriptSource { get; set; } = string.Empty;
    public string ScriptUrl { get; set; } = string.Empty;
    public string Script { get; set; } = string.Empty;
}

public class ClientInstallScriptRequest
{
    public string Platform { get; set; } = string.Empty;
    public string ScriptType { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string Isp { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IncludeProxy { get; set; } = true;
    public bool DisableAutoUpdate { get; set; }
}

public class WebUiAuthConfig
{
    public bool Enabled { get; set; } = true;
    public string Username { get; set; } = "admin";
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public List<WebUiSessionInfo> Sessions { get; set; } = [];
}

public class WebUiSessionInfo
{
    public string Token { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
}

public class WebUiSessionOverview
{
    public string Username { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
}

public class WebUiLoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class WebUiChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewUsername { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public class WebUiAuthStatus
{
    public bool Enabled { get; set; }
    public bool Authenticated { get; set; }
    public string Username { get; set; } = string.Empty;
}

/// <summary>
/// 客户端请求补拉未测过 IP 的请求
/// </summary>
public class AdditionalIpBatchRequest
{
    public string ClientId { get; set; } = string.Empty;
    public IspType Isp { get; set; }
    public List<string> ExcludeIps { get; set; } = [];
}

/// <summary>
/// 服务端返回补拉的新 IP 批次
/// </summary>
public class AdditionalIpBatchResponse
{
    public List<string> IpAddresses { get; set; } = [];
}

/// <summary>
/// 客户端本地持久化状态
/// </summary>
public class ClientLocalState
{
    public string ClientId { get; set; } = string.Empty;
}

/// <summary>
/// 服务端存储的客户端信息
/// </summary>
public class ClientInfo
{
    public string ClientId { get; set; } = string.Empty;
    public IspType Isp { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Platform { get; set; }
    public string? RuntimeStatus { get; set; }
    public int CurrentTaskTotalIps { get; set; }
    public int CurrentTaskTestedIps { get; set; }
    public DateTime? CurrentTaskStartedAt { get; set; }
    public string? RuntimeLog { get; set; }
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public bool IsOnline { get; set; }
    public bool Allowed { get; set; } = true;
}

public enum FetchSourceType
{
    Api = 0,
    Cname = 1
}

public class FetchSource
{
    public FetchSourceType Type { get; set; }
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// 服务端IP池来源配置 (单运营商)
/// </summary>
public class IpSourceConfig
{
    /// <summary>手动输入的IP列表</summary>
    public List<string> ManualIps { get; set; } = [];

    /// <summary>自动拉取源列表</summary>
    public List<FetchSource> FetchSources { get; set; } = [];
}

/// <summary>
/// DoH 解析响应结构
/// </summary>
public class DohResponse
{
    public int Status { get; set; }
    public List<DohAnswer>? Answer { get; set; }
}

public class DohAnswer
{
    public string? name { get; set; }
    public int type { get; set; }
    public int TTL { get; set; }
    public string? data { get; set; }
}

/// <summary>
/// 手动添加IP的请求
/// </summary>
public class IpPoolAddRequest
{
    public string Isp { get; set; } = "Telecom";
    public List<string> Ips { get; set; } = [];
}

/// <summary>
/// 覆盖当前运营商池内容的请求
/// </summary>
public class IpPoolReplaceRequest
{
    public string Isp { get; set; } = "Telecom";
    public List<string> Ips { get; set; } = [];
}

public class IpPoolRemoveRequest
{
    public string Isp { get; set; } = "Telecom";
    public string Ip { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}

public class IpPoolView
{
    public List<string> ManualIps { get; set; } = [];
    public List<string> ApiIps { get; set; } = [];
}

/// <summary>
/// 服务端全局配置
/// </summary>
public class ServerConfig
{
    /// <summary>各运营商的IP池配置</summary>
    public Dictionary<string, IpSourceConfig> IpSources { get; set; } = new()
    {
        ["Telecom"] = new(),
        ["Unicom"] = new(),
        ["Mobile"] = new(),
    };

    /// <summary>API拉取间隔(分钟)</summary>
    public int ApiRefreshIntervalMinutes { get; set; } = 60;

    /// <summary>分发测速任务时是否优先使用手动 IP 池</summary>
    public bool ManualIpPriorityEnabled { get; set; } = true;

    /// <summary>测速URL模板</summary>
    public string TestUrl { get; set; } = "https://{ip}/__down?bytes=104857600";

    /// <summary>HTTPS SNI Host</summary>
    public string TestHost { get; set; } = "speed.cloudflare.com";

    /// <summary>测速端口</summary>
    public int TestPort { get; set; } = 443;

    /// <summary>每个IP下载测速时长(秒)</summary>
    public int DownloadDurationSeconds { get; set; } = 10;

    /// <summary>TCP延迟测试时长(秒)</summary>
    public int TcpTestDurationSeconds { get; set; } = 10;

    /// <summary>每次分发给客户端的IP数量</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>返回前N个最优IP</summary>
    public int TopN { get; set; } = 5;

    /// <summary>单轮测速最多测试多少个 IP（达到该数量后停止补拉）</summary>
    public int MaxTestIpCount { get; set; } = 40;

    /// <summary>客户端轮询间隔(分钟)</summary>
    public int ClientIntervalMinutes { get; set; } = 60;

    /// <summary>测速记录保留天数，0 表示不自动清理</summary>
    public int HistoryRetentionDays { get; set; } = 30;

    /// <summary>客户端心跳间隔(秒)</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 5;

    /// <summary>客户端代理模式：direct / system / custom</summary>
    public string ClientProxyMode { get; set; } = "direct";

    /// <summary>客户端自定义代理地址，仅在 custom 模式生效</summary>
    public string ClientProxyUrl { get; set; } = string.Empty;

    /// <summary>最低下载速度阈值(KB/s)，低于该值的结果不会进入最终TopN</summary>
    public double MinDownloadSpeedKBps { get; set; }

    /// <summary>下载测速限速(KB/s)，0 表示不限速</summary>
    public double MaxDownloadSpeedKBps { get; set; }

    /// <summary>是否只允许白名单中的客户端 ID 连接</summary>
    public bool ClientWhitelistOnly { get; set; }

    /// <summary>是否启用客户端版本检查和更新</summary>
    public bool ClientUpdateEnabled { get; set; }

    /// <summary>客户端更新源类型：github 或 local</summary>
    public string ClientUpdateSourceType { get; set; } = "github";

    /// <summary>服务端提供的最新客户端版本号</summary>
    public string LatestClientVersion { get; set; } = string.Empty;

    /// <summary>GitHub 仓库，例如 greepar/CfSpeedtest</summary>
    public string ClientUpdateRepository { get; set; } = string.Empty;

    /// <summary>GitHub Release Tag，例如 v1.0 或 v1.x</summary>
    public string ClientUpdateReleaseTag { get; set; } = string.Empty;

    /// <summary>可选的 GH Proxy 前缀，例如 https://ghproxy.com/</summary>
    public string ClientUpdateGhProxyPrefix { get; set; } = string.Empty;

    /// <summary>是否启用测速后自动清理IP池（只保留TopN最优IP，其余删除）</summary>
    public bool AutoCleanupEnabled { get; set; }

    /// <summary>华为云 DNS 配置</summary>
    public HuaweiDnsConfig HuaweiDns { get; set; } = new();

    /// <summary>WebUI 登录配置</summary>
    public WebUiAuthConfig WebUiAuth { get; set; } = new();
}

/// <summary>
/// API通用响应
/// </summary>
public class ApiResponse<T>
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public T? Data { get; set; }

    public static ApiResponse<T> Ok(T data, string? msg = null) =>
        new() { Success = true, Data = data, Message = msg };

    public static ApiResponse<T> Fail(string msg) =>
        new() { Success = false, Message = msg };
}

/// <summary>
/// 测速历史记录
/// </summary>
public class TestHistory
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TaskId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public IspType Isp { get; set; }
    public List<IpTestResult> Results { get; set; } = [];
    public DateTime CompletedAt { get; set; }
}

/// <summary>
/// 华为云 DNS 单条记录集配置（每个运营商一条）
/// </summary>
public class HuaweiDnsRecordConfig
{
    /// <summary>Zone ID（域名ID）</summary>
    public string ZoneId { get; set; } = string.Empty;

    /// <summary>RecordSet ID（记录集ID）</summary>
    public string RecordSetId { get; set; } = string.Empty;

    /// <summary>域名（如 ct.example.com.）</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>TTL（秒）</summary>
    public int Ttl { get; set; } = 60;
}

/// <summary>
/// 华为云 DNS 全局配置
/// </summary>
public class HuaweiDnsConfig
{
    /// <summary>是否启用华为云 DNS 自动更新</summary>
    public bool Enabled { get; set; }

    /// <summary>华为云 Access Key（AK）</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>华为云 Secret Key（SK）</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>华为云 DNS API Endpoint（如 https://dns.cn-north-4.myhuaweicloud.com）</summary>
    public string Endpoint { get; set; } = "https://dns.cn-north-4.myhuaweicloud.com";

    /// <summary>DNS 自动更新间隔（分钟），0 表示不自动更新，仅手动触发</summary>
    public int UpdateIntervalMinutes { get; set; } = 30;

    /// <summary>各运营商的记录集配置</summary>
    public Dictionary<string, HuaweiDnsRecordConfig> Records { get; set; } = new()
    {
        ["Telecom"] = new(),
        ["Unicom"] = new(),
        ["Mobile"] = new(),
    };
}

/// <summary>
/// 手动触发 DNS 更新的请求
/// </summary>
public class DnsUpdateTriggerRequest
{
    /// <summary>运营商（Telecom/Unicom/Mobile），为空则更新所有</summary>
    public string? Isp { get; set; }

    /// <summary>可选的时间段起始(UTC)，用于指定使用哪个时间段的测速记录</summary>
    public DateTime? From { get; set; }

    /// <summary>可选的时间段结束(UTC)</summary>
    public DateTime? To { get; set; }
}

/// <summary>
/// 测速记录的小时时间段
/// </summary>
public class HistoryTimeSegment
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
}

/// <summary>
/// DNS 更新状态记录
/// </summary>
public class DnsUpdateStatus
{
    public string Isp { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public List<IpTestResult> Results { get; set; } = [];
    public DateTime? LastUpdatedAt { get; set; }
    public bool Success { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// 单个运营商的统一轮次状态
/// </summary>
public class IspRoundStatus
{
    public string Isp { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public DateTime ScheduledAtUtc { get; set; }
    public DateTime FinalizeAfterUtc { get; set; }
    public int AssignedClients { get; set; }
    public int ReportedClients { get; set; }
    public bool Finalizing { get; set; }
    public bool Finalized { get; set; }
}

/// <summary>
/// WebUI 用的统一轮次概览状态
/// </summary>
public class RoundStatusOverview
{
    public DateTime ServerNowUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CurrentRoundStartUtc { get; set; }
    public DateTime NextRoundStartUtc { get; set; }
    public int ClientIntervalMinutes { get; set; } = 60;
    public List<IspRoundStatus> Isps { get; set; } = [];
}
