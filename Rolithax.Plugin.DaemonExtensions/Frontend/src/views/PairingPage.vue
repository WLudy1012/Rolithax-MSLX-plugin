<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { QrcodeIcon, UsergroupIcon } from 'tdesign-icons-vue-next';
import request from 'mslx-request';

import { getPairingConfig } from '../api/pairing';
import type { PairingConfigModel } from '../api/model/pairing';
import GeneratePairCode from './components/GeneratePairCode.vue';
import DeviceList from './components/DeviceList.vue';
import FeatureSettings from './components/FeatureSettings.vue';

const activeTab = ref('generate');
const config = ref<PairingConfigModel | null>(null);
const loading = ref(true);
const loadError = ref('');
const pairingEnabled = ref<boolean | null>(null);
const canManageDevices = computed(() => config.value?.canManageDevices === true);
const isAdmin = computed(() => {
  const stores = (window as any).MSLX_Stores;
  const userStore = stores?.getUserStore?.() || stores?.useUserStore?.();
  return userStore?.isAdmin === true || config.value?.canEditAddress === true;
});

async function loadConfig() {
  loading.value = true;
  loadError.value = '';
  try {
    config.value = await getPairingConfig();
    if (!canManageDevices.value) activeTab.value = 'generate';
  } catch (e: any) {
    loadError.value = e?.message || String(e);
  } finally {
    loading.value = false;
  }
}

async function loadCenter() {
  loading.value = true;
  loadError.value = '';
  try {
    const state = await request.get({ url: '/api/plugin/mslx-plugin-rolithax/features' });
    if (typeof state?.pairing !== 'boolean') throw new Error('扩展状态无效');
    pairingEnabled.value = state.pairing;
    if (state.pairing) await loadConfig();
    else config.value = null;
  } catch (e: any) {
    pairingEnabled.value = null;
    loadError.value = e?.message || '读取扩展状态失败';
  } finally {
    loading.value = false;
  }
}

onMounted(loadCenter);
</script>

<template>
  <div class="rolithax-page flex flex-col gap-5">
    <div class="design-card relative flex flex-col rounded-2xl border border-[var(--td-component-border)] shadow-sm p-5 sm:p-6 gap-5">
      <div class="rolithax-header flex flex-col sm:flex-row sm:items-center justify-between gap-4">
        <div class="flex items-center gap-3">
          <img src="https://cdnjson.com/images/2026/10/10/ic_launcher_foreground.png" alt="Rolithax" class="w-10 h-10 object-contain" />
          <div>
            <h2 class="rolithax-title text-lg font-bold m-0">Rolithax 中心</h2>
            <p class="rolithax-muted text-sm mt-1 mb-0">连接设备并管理远端扩展能力</p>
          </div>
        </div>

      <t-radio-group v-if="canManageDevices" v-model="activeTab" variant="default-filled" class="self-start xl:self-auto">
        <t-radio-button value="generate">
          <span class="flex items-center gap-1"><qrcode-icon />生成配对码</span>
        </t-radio-button>
        <t-radio-button value="devices">
          <span class="flex items-center gap-1"><usergroup-icon />已配对设备</span>
        </t-radio-button>
      </t-radio-group>
      </div>
    </div>

    <div class="rolithax-content" :class="{ 'rolithax-content-wide': activeTab === 'devices' }">
    <main class="rolithax-main">
    <div v-if="loading" class="design-card relative flex flex-col rounded-2xl border border-[var(--td-component-border)] shadow-sm p-5 sm:p-6 gap-5 rolithax-muted">正在加载配对配置…</div>
    <div v-else-if="loadError" class="design-card relative flex flex-col rounded-2xl border border-[var(--td-component-border)] shadow-sm p-5 sm:p-6 gap-5 rolithax-error">加载失败：{{ loadError }} <t-button variant="text" @click="loadCenter">重试</t-button></div>
    <div v-else-if="pairingEnabled === false" class="design-card relative flex flex-col rounded-2xl border border-[var(--td-component-border)] shadow-sm p-5 sm:p-6 gap-5 rolithax-muted">设备配对功能已关闭。</div>
    <template v-else-if="config">
      <transition name="rolithax-fade" mode="out-in">
        <keep-alive>
          <generate-pair-code v-if="activeTab === 'generate'" :config="config" />
          <device-list v-else-if="canManageDevices" />
        </keep-alive>
      </transition>
    </template>
    </main>
    <feature-settings v-if="isAdmin" @changed="loadCenter" />
    </div>
  </div>
</template>

<style scoped>
@unocss;

.rolithax-page {
  color: var(--td-text-color-primary);
  padding-bottom: 8px;
  min-width: 0;
}

.rolithax-content {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 20px;
  align-items: start;
}

.rolithax-main {
  min-width: 0;
}

@media (min-width: 1100px) {
  .rolithax-content:not(.rolithax-content-wide) {
    grid-template-columns: minmax(0, 1fr) minmax(280px, 340px);
  }
}

.rolithax-error {
  color: var(--td-error-color);
}

.rolithax-fade-enter-active,
.rolithax-fade-leave-active {
  transition:
    opacity 0.2s ease,
    transform 0.2s ease;
}
.rolithax-fade-enter-from,
.rolithax-fade-leave-to {
  opacity: 0;
  transform: translateY(10px);
}
</style>
