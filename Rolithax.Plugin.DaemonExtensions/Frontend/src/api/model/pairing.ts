// 扫码配对相关类型
// 字段与后端 PairingService.CreateCode / ListDevices 返回结构对齐（camelCase）。

/** 生成配对码请求体 */
export interface CreatePairCodeParams {
  /** 授权范围：full 创建 admin 级配对用户；limited 创建受限 user */
  scope: 'full' | 'limited';
  /** limited 模式下授予的实例资源，形如 ["server:1", "server:2"] */
  resources?: string[];
  /** 配对设备有效期（天），1–365，默认 30 */
  deviceTtlDays?: number;
}

export interface PairingConfigModel {
  publicUrl: string;
  canEditAddress: boolean;
  canManageDevices: boolean;
}

/** 生成配对码返回数据（二维码原文在 payload） */
export interface PairCodeModel {
  code: string;
  expiresAt: string;
  expiresInSeconds: number;
  scope: string;
  deviceTtlDays: number;
  /** 二维码内容：mslxp1: + Base64Url(JSON)，前端仅原样渲染，不解析/不验签 */
  payload: string;
  url: string;
}

/** 已配对设备记录（脱敏） */
export interface PairedDeviceModel {
  deviceId: string;
  name: string;
  fingerprintPrefix: string;
  role: string;
  resources: string[];
  apiKeyPrefix: string;
  createdAt: string;
  expiresAt: string;
  /** active / expired / revoked / replaced / issuer-removed / issuer-revoked / user-removed / plugin-unloaded */
  status: string;
}
