import request from 'mslx-request';
import { putPluginData } from './request';

export type Feature = 'pairing' | 'serverIcon' | 'resourceInstall';
export type FeatureState = Record<Feature, boolean>;

const api = '/api/plugin/mslx-plugin-rolithax/features';

function featureState(data: Partial<FeatureState> | null | undefined): FeatureState {
  if (typeof data?.pairing !== 'boolean' ||
      typeof data.serverIcon !== 'boolean' ||
      typeof data.resourceInstall !== 'boolean') {
    throw new Error('扩展 API 状态无效');
  }
  return {
    pairing: data.pairing,
    serverIcon: data.serverIcon,
    resourceInstall: data.resourceInstall,
  };
}

export async function getFeatures(): Promise<FeatureState> {
  return featureState(await request.get({ url: api }));
}

export async function setFeature(key: Feature, enabled: boolean): Promise<FeatureState> {
  return featureState(await putPluginData<FeatureState>(`${api}/${key}`, { enabled }));
}
