import type { ApiResponse } from "./types";

// 401 时的全局回调（由 LoginGate 注册），用于弹出登录框。
let onUnauthorized: (() => void) | null = null;
export function setUnauthorizedHandler(fn: () => void) {
  onUnauthorized = fn;
}

export class ApiError extends Error {
  status: number;
  constructor(message: string, status: number) {
    super(message);
    this.status = status;
  }
}

interface RequestOptions {
  method?: string;
  body?: unknown;
  query?: Record<string, string | number | boolean | undefined | null>;
  /** 401 时是否触发全局登录框（轮询类请求可关掉） */
  silent401?: boolean;
}

function buildUrl(path: string, query?: RequestOptions["query"]): string {
  if (!query) return path;
  const qs = new URLSearchParams();
  for (const [k, v] of Object.entries(query)) {
    if (v !== undefined && v !== null && v !== "") qs.append(k, String(v));
  }
  const s = qs.toString();
  return s ? `${path}?${s}` : path;
}

async function request<T>(path: string, opts: RequestOptions = {}): Promise<T> {
  const { method = "GET", body, query, silent401 } = opts;
  const init: RequestInit = {
    method,
    headers: {},
    credentials: "same-origin",
  };
  if (body !== undefined) {
    (init.headers as Record<string, string>)["Content-Type"] =
      "application/json";
    init.body = JSON.stringify(body);
  }

  let res: Response;
  try {
    res = await fetch(buildUrl(path, query), init);
  } catch {
    throw new ApiError("网络请求失败，请检查服务端是否在线", 0);
  }

  if (res.status === 401) {
    if (!silent401 && onUnauthorized) onUnauthorized();
    throw new ApiError("未登录或登录已过期", 401);
  }

  let json: ApiResponse<T> | null = null;
  const text = await res.text();
  if (text) {
    try {
      json = toCamel(JSON.parse(text)) as ApiResponse<T>;
    } catch {
      throw new ApiError(`服务端返回了非 JSON 响应 (${res.status})`, res.status);
    }
  }

  if (json && typeof json.success === "boolean") {
    if (!json.success) {
      throw new ApiError(json.message || "请求失败", res.status);
    }
    return json.data as T;
  }

  if (!res.ok) {
    throw new ApiError(`请求失败 (${res.status})`, res.status);
  }
  return (json as unknown as T) ?? (undefined as T);
}

function toCamel(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(toCamel);
  if (!value || typeof value !== "object") return value;
  const out: Record<string, unknown> = {};
  for (const [key, val] of Object.entries(value)) {
    const next = key ? key[0].toLowerCase() + key.slice(1) : key;
    out[next] = toCamel(val);
  }
  return out;
}

export const api = {
  get: <T>(path: string, query?: RequestOptions["query"], silent401?: boolean) =>
    request<T>(path, { query, silent401 }),
  post: <T>(path: string, body?: unknown, query?: RequestOptions["query"]) =>
    request<T>(path, { method: "POST", body, query }),
  del: <T>(path: string, query?: RequestOptions["query"]) =>
    request<T>(path, { method: "DELETE", query }),
  raw: request,
};
