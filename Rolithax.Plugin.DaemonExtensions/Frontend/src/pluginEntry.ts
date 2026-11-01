import './style.css';

import PairingPage from './views/PairingPage.vue';

const loadedVersion = import.meta.url.match(/\/plugins\/[^/]+\/([^/]+)\//)?.[1];

/**
 * MSLX WebPanel 插件入口约定：
 * 宿主 pluginManager.ts 动态 import 本文件（由插件 DLL 内嵌资源 Frontend/dist 提供），
 * 读取导出的 pluginConfig 并注册路由 / UI 扩展。
 * 依赖（vue / tdesign-vue-next / mslx-request 等）已外部化到宿主 window 全局，构建产物为单文件 ESM。
 */
export const pluginConfig = {
  // 与后端 IPlugin.Id、package.json.name 完全一致，作为 Android 客户端扩展能力的统一标识。
  name: 'mslx-plugin-rolithax',
  version: loadedVersion ?? '1.0.0',

  routes: [
    {
      path: '/mslx-plugin-rolithax',
      name: 'mslx-plugin-rolithax-center',
      component: 'HOST_LAYOUT',
      meta: { title: 'Rolithax 中心', icon: 'app', roleCode: ['admin', 'user'] },
      children: [{
        path: '',
        name: 'mslx-plugin-rolithax-home',
        component: PairingPage,
        meta: { title: 'Rolithax 中心', hidden: true, roleCode: ['admin', 'user'] },
      }],
    },
  ],
};
