// 对齐 CfSpeedtest.Shared/Models.cs 的 TypeScript 类型定义。
// 注意：服务端 Isp 字段以数字枚举序列化（Telecom=0, Unicom=1, Mobile=2）。

export type IspValue = 0 | 1 | 2;
export type IspKey = "Telecom" | "Unicom" | "Mobile";

export interface ApiResponse<T> {
  success: boolean;
  message?: string | null;
  data?: T | null;
}

export interface IpTestResult {
  ipAddress: string;
  downloadSpeedKBps: number;
  avgLatencyMs: number;
  minLatencyMs: number;
  packetLossRate: number;
  tcpSuccessCount: number;
  tcpTotalCount: number;
  score: number;
}

export interface ClientInfo {
  clientId: string;
  isp: IspValue;
  name?: string | null;
  version?: string | null;
  platform?: string | null;
  runtimeStatus?: string | null;
  currentTaskTotalIps: number;
  currentTaskTestedIps: number;
  currentTaskStartedAt?: string | null;
  runtimeLog?: string | null;
  registeredAt: string;
  lastSeenAt: string;
  isOnline: boolean;
  allowed: boolean;
}

export interface TestHistory {
  id: string;
  taskId: string;
  clientId: string;
  isp: IspValue;
  results: IpTestResult[];
  completedAt: string;
}

export interface HistoryTimeSegment {
  from: string;
  to: string;
  label: string;
  count: number;
}

export interface IpPoolView {
  manualIps: string[];
  apiIps: string[];
}

export type IpPoolMap = Record<string, IpPoolView>;

export interface IspRoundStatus {
  isp: string;
  taskId: string;
  scheduledAtUtc: string;
  finalizeAfterUtc: string;
  assignedClients: number;
  reportedClients: number;
  finalizing: boolean;
  finalized: boolean;
}

export interface RoundStatusOverview {
  serverNowUtc: string;
  currentRoundStartUtc?: string | null;
  nextRoundStartUtc: string;
  clientIntervalMinutes: number;
  isps: IspRoundStatus[];
}

export interface DnsUpdateStatus {
  isp: string;
  domain: string;
  results: IpTestResult[];
  lastUpdatedAt?: string | null;
  success: boolean;
  message?: string | null;
}

export interface WebUiAuthStatus {
  enabled: boolean;
  authenticated: boolean;
  username: string;
}

export interface ServerInfo {
  name: string;
  version: string;
}

export interface WebUiSessionOverview {
  username: string;
  userAgent: string;
  ipAddress: string;
  createdAtUtc: string;
  expiresAtUtc: string;
  lastSeenAtUtc: string;
}

export interface FetchSource {
  type: 0 | 1; // Api=0, Cname=1
  value: string;
}

export interface IpSourceConfig {
  manualIps: string[];
  fetchSources: FetchSource[];
}

export interface HuaweiDnsRecordConfig {
  zoneId: string;
  recordSetId: string;
  domain: string;
  ttl: number;
}

export interface HuaweiDnsConfig {
  enabled: boolean;
  accessKey: string;
  secretKey: string;
  endpoint: string;
  updateIntervalMinutes: number;
  records: Record<string, HuaweiDnsRecordConfig>;
}

export interface WebUiAuthConfig {
  enabled: boolean;
  username: string;
  passwordHash: string;
  passwordSalt: string;
  sessions: unknown[];
}

export interface ServerConfig {
  ipSources: Record<string, IpSourceConfig>;
  apiRefreshIntervalMinutes: number;
  manualIpPriorityEnabled: boolean;
  testUrl: string;
  testHost: string;
  testPort: number;
  downloadDurationSeconds: number;
  tcpTestDurationSeconds: number;
  batchSize: number;
  topN: number;
  maxTestIpCount: number;
  clientIntervalMinutes: number;
  historyRetentionDays: number;
  heartbeatIntervalSeconds: number;
  clientProxyMode: string;
  clientProxyUrl: string;
  minDownloadSpeedKBps: number;
  maxDownloadSpeedKBps: number;
  clientWhitelistOnly: boolean;
  clientUpdateEnabled: boolean;
  clientUpdateSourceType: string;
  latestClientVersion: string;
  clientUpdateRepository: string;
  clientUpdateReleaseTag: string;
  clientUpdateGhProxyPrefix: string;
  autoCleanupEnabled: boolean;
  huaweiDns: HuaweiDnsConfig;
  webUiAuth: WebUiAuthConfig;
}

export interface BootstrapTokenCreateResponse {
  token: string;
  clientId: string;
  isp: IspValue;
  name: string;
  expiresAtUtc: string;
  serverUrl: string;
  linuxCommand: string;
  windowsCommand: string;
}

export interface BootstrapTokenStatus {
  token: string;
  clientId: string;
  online: boolean;
  consumed: boolean;
  expired: boolean;
  expiresAtUtc: string;
  lastSeenAtUtc?: string | null;
  runtimeStatus?: string | null;
}
