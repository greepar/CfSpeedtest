import type { IspKey, IspValue } from "./types";

// 数字枚举 <-> 运营商
export const ISP_KEYS: IspKey[] = ["Telecom", "Unicom", "Mobile"];

export const ISP_LABEL: Record<IspKey, string> = {
  Telecom: "电信",
  Unicom: "联通",
  Mobile: "移动",
};

export function ispLabel(isp: IspValue | string): string {
  if (typeof isp === "number") return ISP_LABEL[ISP_KEYS[isp]] ?? String(isp);
  const key = isp as IspKey;
  return ISP_LABEL[key] ?? isp;
}

export function ispKey(isp: IspValue): IspKey {
  return ISP_KEYS[isp] ?? "Telecom";
}

export function ispValue(key: IspKey | string): IspValue {
  const i = ISP_KEYS.indexOf(key as IspKey);
  return (i >= 0 ? i : 0) as IspValue;
}

// 运营商徽章配色（语义 token）
export const ISP_BADGE: Record<IspKey, "info" | "success" | "warning"> = {
  Telecom: "info",
  Unicom: "success",
  Mobile: "warning",
};

export function ispBadgeTone(isp: IspValue): "info" | "success" | "warning" {
  return ISP_BADGE[ispKey(isp)];
}
