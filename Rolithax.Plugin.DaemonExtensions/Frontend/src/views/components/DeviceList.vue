<script setup lang="ts">
import { h, onMounted, ref } from 'vue';
import { RefreshIcon } from 'tdesign-icons-vue-next';
import { MessagePlugin, Tag } from 'tdesign-vue-next';

import { getPairedDevices, revokePairDevice } from '../../api/pairing';
import type { PairedDeviceModel } from '../../api/model/pairing';

const loading = ref(false);
const devices = ref<PairedDeviceModel[]>([]);

async function getList() {
  try {
    loading.value = true;
    devices.value = (await getPairedDevices()) || [];
  } catch (e: any) {
    MessagePlugin.error('获取设备列表失败: ' + (e?.message || e));
  } finally {
    loading.value = false;
  }
}

const statusMeta: Record<string, { text: string; theme: string }> = {
  active: { text: '生效中', theme: 'success' },
  expired: { text: '已过期', theme: 'warning' },
  revoked: { text: '已撤销', theme: 'default' },
  replaced: { text: '已替换', theme: 'default' },
  'issuer-removed': { text: '签发者已删除', theme: 'default' },
  'issuer-revoked': { text: '签发设备已失效', theme: 'default' },
  'user-removed': { text: '用户已被删除', theme: 'default' },
  'plugin-unloaded': { text: '插件已卸载', theme: 'default' },
};

const isLive = (status: string) => status === 'active' || status === 'expired';

function renderStatus(status: string) {
  const meta = statusMeta[status] || { text: status, theme: 'primary' };
  return h(Tag, { size: 'small', theme: meta.theme as any, variant: 'light' }, () => meta.text);
}

const columns = [
  { colKey: 'name', title: '设备名称', ellipsis: true },
  { colKey: 'apiKeyPrefix', title: 'API Key', width: 130 },
  { colKey: 'fingerprintPrefix', title: '设备指纹', width: 150, ellipsis: true },
  { colKey: 'role', title: '角色', width: 90, cell: (_h: any, { row }: any) => (row.role === 'admin' ? '完整' : '受限') },
  {
    colKey: 'resources',
    title: '实例',
    width: 80,
    cell: (_h: any, { row }: any) => (row.role === 'admin' ? '全部' : `${(row.resources || []).length}`),
  },
  { colKey: 'status', title: '状态', width: 100, cell: (_h: any, { row }: any) => renderStatus(row.status) },
  { colKey: 'expiresAt', title: '到期时间', width: 170 },
  { colKey: 'op', title: '操作', width: 90, fixed: 'right' as const },
];

const revoking = ref('');
async function handleRevoke(row: PairedDeviceModel) {
  try {
    revoking.value = row.deviceId;
    await revokePairDevice(row.deviceId);
    MessagePlugin.success('已撤销，该设备凭据立即失效');
    await getList();
  } catch (e: any) {
    MessagePlugin.error('撤销失败: ' + (e?.message || e));
  } finally {
    revoking.value = '';
  }
}

defineExpose({ getList });
onMounted(getList);
</script>

<template>
  <div class="design-card relative flex flex-col rounded-2xl border border-[var(--td-component-border)] shadow-sm p-5 sm:p-6 gap-5 min-w-0">
    <div class="flex items-center justify-between gap-4 flex-wrap">
      <div class="flex flex-col gap-2">
        <div class="flex items-center gap-3">
          <span class="w-1.5 h-5 bg-[var(--color-primary)] rounded-full"></span>
          <h3 class="rolithax-title text-base font-bold m-0">已配对设备</h3>
        </div>
        <p class="rolithax-muted text-sm m-0">撤销将删除对应配对用户，其 API Key 立即失效且不可恢复。</p>
      </div>
      <t-button variant="dashed" :loading="loading" @click="getList">
        <template #icon><refresh-icon /></template>
        刷新
      </t-button>
    </div>

    <div class="overflow-x-auto w-full">
    <t-table
      row-key="deviceId"
      :data="devices"
      :columns="columns"
      :loading="loading"
      size="medium"
      hover
      empty="暂无已配对设备"
    >
      <template #op="{ row }">
        <t-popconfirm
          :content="`确认撤销设备「${row.name}」？该操作不可恢复。`"
          theme="danger"
          placement="left"
          @confirm="handleRevoke(row)"
        >
          <t-button size="small" theme="danger" variant="text" :loading="revoking === row.deviceId" :disabled="!isLive(row.status)">
            撤销
          </t-button>
        </t-popconfirm>
      </template>
    </t-table>
    </div>
  </div>
</template>

<style scoped>
@unocss;
</style>
