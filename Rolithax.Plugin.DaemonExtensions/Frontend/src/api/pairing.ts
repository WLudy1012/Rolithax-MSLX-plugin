import request from 'mslx-request';
import { putPluginData } from './request';

import type {
  CreatePairCodeParams,
  PairCodeModel,
  PairedDeviceModel,
  PairingConfigModel,
} from './model/pairing';

// 鉴权头与响应解包由宿主 request 拦截器处理。
const pairingApi = '/api/plugin/mslx-plugin-rolithax/pair';

export async function getPairingConfig(): Promise<PairingConfigModel> {
  return await request.get({ url: `${pairingApi}/config` });
}

export async function savePairingConfig(publicUrl: string): Promise<PairingConfigModel> {
  return putPluginData(`${pairingApi}/config`, { publicUrl });
}

/** 生成一次性配对码（TTL 120s，仅服务端内存保存） */
export async function postPairCode(params: CreatePairCodeParams): Promise<PairCodeModel> {
  return await request.post({
    url: `${pairingApi}/codes`,
    data: params,
  });
}

/** 已配对设备列表（脱敏） */
export async function getPairedDevices(): Promise<PairedDeviceModel[]> {
  return await request.get({
    url: `${pairingApi}/devices`,
  });
}

/** 撤销设备（删除对应配对用户，API Key 立即失效，不可逆） */
export async function revokePairDevice(deviceId: string) {
  return await request.post({
    url: `${pairingApi}/devices/${encodeURIComponent(deviceId)}/revoke`,
  });
}

/** 实例资源多选器数据源：复用 Daemon 既有实例列表接口，映射为后端要求的 server:xx 资源标识 */
export async function getInstanceOptions(): Promise<{ label: string; value: string }[]> {
  const list = await request.get({ url: '/api/instance/list' });
  return (list || []).map((i: any) => ({ label: `${i.name} (#${i.id})`, value: `server:${i.id}` }));
}
