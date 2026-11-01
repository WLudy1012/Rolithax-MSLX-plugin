<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue';
import { CopyIcon, QrcodeIcon, RefreshIcon } from 'tdesign-icons-vue-next';
import { MessagePlugin } from 'tdesign-vue-next';
import QrcodeVue from 'qrcode.vue';

import { getInstanceOptions, postPairCode, savePairingConfig } from '../../api/pairing';
import type { PairCodeModel, PairingConfigModel } from '../../api/model/pairing';

const props = defineProps<{ config: PairingConfigModel }>();
const isAdmin = computed(() => props.config.canEditAddress);

const form = reactive({
  scope: isAdmin.value ? ('full' as 'full' | 'limited') : ('limited' as const),
  resources: [] as string[],
  deviceTtlDays: 30,
  publicUrl: props.config.publicUrl,
});
const savedPublicUrl = ref(props.config.publicUrl);
const savingAddress = ref(false);
const addressDirty = computed(() => form.publicUrl.trim() !== savedPublicUrl.value);

const instanceOptions = ref<{ label: string; value: string }[]>([]);
const loadingInstances = ref(false);

async function loadInstances() {
  try {
    loadingInstances.value = true;
    instanceOptions.value = await getInstanceOptions();
  } catch (e: any) {
    MessagePlugin.error('加载实例列表失败: ' + (e?.message || e));
  } finally {
    loadingInstances.value = false;
  }
}

async function handleSaveAddress() {
  if (!isAdmin.value || savingAddress.value) return;
  const value = form.publicUrl.trim();
  if (!value) {
    MessagePlugin.warning('请输入 Daemon 对外地址');
    return;
  }
  try {
    savingAddress.value = true;
    const data = await savePairingConfig(value);
    form.publicUrl = data.publicUrl;
    savedPublicUrl.value = data.publicUrl;
    MessagePlugin.success('Daemon 地址已保存');
  } catch (e: any) {
    MessagePlugin.error('保存 Daemon 地址失败: ' + (e?.message || e));
  } finally {
    savingAddress.value = false;
  }
}

const generating = ref(false);
const pairCode = ref<PairCodeModel | null>(null);
const remaining = ref(0);
let timer: ReturnType<typeof setInterval> | null = null;

async function handleGenerate() {
  if (generating.value) return;
  if (isAdmin.value && form.scope === 'limited' && form.resources.length === 0) {
    MessagePlugin.warning('受限范围请至少选择一个实例资源');
    return;
  }
  try {
    generating.value = true;
    const data = await postPairCode({
      scope: isAdmin.value ? form.scope : 'limited',
      resources: isAdmin.value && form.scope === 'limited' ? form.resources : [],
      deviceTtlDays: form.deviceTtlDays,
    });
    pairCode.value = data;
    startCountdown(data?.expiresInSeconds || 120);
  } catch (e: any) {
    MessagePlugin.error('生成配对码失败: ' + (e?.message || e));
  } finally {
    generating.value = false;
  }
}

function startCountdown(seconds: number) {
  stopCountdown();
  remaining.value = Math.max(0, seconds);
  timer = setInterval(() => {
    remaining.value = Math.max(0, remaining.value - 1);
    if (remaining.value === 0) stopCountdown();
  }, 1000);
}

function stopCountdown() {
  if (timer) clearInterval(timer);
  timer = null;
}

const expired = computed(() => pairCode.value !== null && remaining.value === 0);

async function copyPayload() {
  if (!pairCode.value) return;
  try {
    await navigator.clipboard.writeText(pairCode.value.payload);
    MessagePlugin.success('二维码原文已复制');
  } catch {
    MessagePlugin.error('复制失败，请手动选择');
  }
}

onMounted(loadInstances);
onUnmounted(stopCountdown);
</script>

<template>
  <div class="pairing-card design-card relative flex flex-col rounded-2xl border border-[var(--td-component-border)] shadow-sm p-5 sm:p-6 gap-5 min-w-0">
    <div class="flex flex-col gap-2">
      <div class="flex items-center gap-3">
        <span class="w-1.5 h-5 bg-[var(--color-primary)] rounded-full"></span>
        <h3 class="rolithax-title text-base font-bold m-0">生成配对二维码</h3>
      </div>
      <p class="rolithax-muted text-sm m-0">
        生成一次性二维码，在另一台设备的 Rolithax Launcher「连接」页扫描即可接入。
      </p>
    </div>

    <div class="flex flex-col gap-2">
      <label class="rolithax-title text-xs font-bold">Daemon 对外地址</label>
      <div class="flex flex-col md:flex-row gap-2">
        <t-input v-model="form.publicUrl" :disabled="!isAdmin" clearable class="flex-1" />
        <t-button v-if="isAdmin" theme="primary" :loading="savingAddress" :disabled="!addressDirty" @click="handleSaveAddress">
          保存地址
        </t-button>
      </div>
      <span class="rolithax-placeholder text-xs">
        {{ isAdmin ? '管理员修改后会持久化，所有新二维码使用该地址。' : '地址由管理员维护，当前账号只能使用已保存地址。' }}
      </span>
    </div>

    <div v-if="!isAdmin" class="rolithax-muted text-sm">
      当前二维码只会授予你的 Daemon 权限，无法扩大到其他用户或实例。
      <span v-if="instanceOptions.length">当前可访问实例：{{ instanceOptions.map((item) => item.label).join('、') }}</span>
    </div>

    <div class="pairing-layout">
      <div class="flex-1 min-w-0 flex flex-col gap-4">
        <div v-if="isAdmin" class="flex flex-col gap-2">
          <label class="rolithax-title text-xs font-bold">授权范围</label>
          <t-radio-group v-model="form.scope" variant="default-filled">
            <t-radio-button value="full">完整权限（admin）</t-radio-button>
            <t-radio-button value="limited">受限（指定实例）</t-radio-button>
          </t-radio-group>
        </div>

        <div v-if="isAdmin && form.scope === 'limited'" class="flex flex-col gap-2">
          <label class="rolithax-title text-xs font-bold">授予实例</label>
          <t-select
            v-model="form.resources"
            :options="instanceOptions"
            :loading="loadingInstances"
            multiple
            clearable
            :min-collapsed-num="3"
            placeholder="选择该设备可管理的实例"
          />
        </div>

        <div class="flex flex-col gap-2">
          <label class="rolithax-title text-xs font-bold">设备有效期（天，1–365）</label>
          <t-input-number v-model="form.deviceTtlDays" :min="1" :max="365" theme="column" class="w-full" />
        </div>

        <t-button theme="primary" :loading="generating" class="self-start" @click="handleGenerate">
          <template #icon><qrcode-icon /></template>
          生成配对二维码
        </t-button>
      </div>

      <div class="rolithax-qr-col shrink-0 flex flex-col items-center gap-3">
        <div class="rolithax-qr-box rounded-2xl border border-[var(--td-component-border)] items-center justify-center relative overflow-hidden">
          <template v-if="pairCode">
            <QrcodeVue :value="pairCode.payload" :size="220" :level="'M'" :margin="1" />
            <div v-if="expired" class="rolithax-qr-mask flex flex-col items-center justify-center gap-2 text-sm font-bold">
              <span>二维码已过期</span>
              <t-button size="small" variant="base" @click="handleGenerate">重新生成</t-button>
            </div>
          </template>
          <span v-else class="rolithax-placeholder text-sm px-6 text-center">生成后在此显示二维码</span>
        </div>

        <div v-if="pairCode" class="flex flex-col items-center gap-2 text-center">
          <div class="rolithax-title text-sm font-mono tracking-widest">配对码 {{ pairCode.code }}</div>
          <div class="text-xs" :class="expired ? 'rolithax-expired' : 'rolithax-muted'">
            {{ expired ? '已过期，请重新生成' : `剩余 ${remaining} 秒 · 单次有效 · ${pairCode.scope === 'full' ? '完整权限' : '受限'}` }}
          </div>
          <div class="flex items-center gap-2">
            <t-button size="small" variant="text" @click="copyPayload">
              <template #icon><copy-icon /></template>
              复制原文
            </t-button>
            <t-button size="small" variant="text" @click="handleGenerate">
              <template #icon><refresh-icon /></template>
              刷新
            </t-button>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
@unocss;

.pairing-card {
  container-type: inline-size;
}

.pairing-layout {
  display: flex;
  flex-direction: column;
  gap: 24px;
}

.rolithax-qr-col {
  width: 100%;
}

.rolithax-qr-box {
  display: flex;
  width: 240px;
  height: 240px;
  background: #fff;
}

.rolithax-qr-mask {
  position: absolute;
  inset: 0;
  background: rgba(0, 0, 0, 0.7);
  color: #fff;
}

.rolithax-expired {
  color: var(--td-error-color);
}

@container (min-width: 650px) {
  .pairing-layout {
    flex-direction: row;
  }

  .rolithax-qr-col {
    width: 260px;
  }
}
</style>
