<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { MessagePlugin } from 'tdesign-vue-next';
import { getFeatures, setFeature, type Feature, type FeatureState } from '../../api/features';

const features = ref<FeatureState | null>(null);
const emit = defineEmits<{ changed: [] }>();
const loading = ref(false);
const updating = ref<Feature | null>(null);
const error = ref('');
const entries: { key: Feature; title: string; description: string }[] = [
  { key: 'pairing', title: '设备配对', description: '通过一次性配对码连接 Rolithax Launcher。' },
  { key: 'serverIcon', title: '实例图标', description: '向客户端提供服务端图标。' },
  { key: 'resourceInstall', title: '资源安装', description: '允许客户端创建远端资源安装任务。' },
];
async function load() {
  loading.value = true;
  error.value = '';
  try {
    features.value = await getFeatures();
  } catch (e: any) {
    error.value = e?.message || '获取扩展 API 状态失败';
  } finally {
    loading.value = false;
  }
}

async function change(key: Feature, enabled: boolean) {
  if (updating.value) return;
  updating.value = key;
  try {
    features.value = await setFeature(key, enabled);
    MessagePlugin.success('扩展 API 状态已更新');
    emit('changed');
  } catch (e: any) {
    MessagePlugin.error(e?.message || '更新扩展 API 状态失败');
    await load();
  } finally {
    updating.value = null;
  }
}
onMounted(load);
</script>

<template>
  <section class="design-card relative flex flex-col rounded-2xl border border-[var(--td-component-border)] shadow-sm p-5 sm:p-6 gap-5">
    <div>
      <div class="flex items-center gap-3">
        <span class="w-1.5 h-5 bg-[var(--color-primary)] rounded-full"></span>
        <h3 class="rolithax-title text-base font-bold m-0">扩展 API</h3>
      </div>
      <p class="rolithax-muted text-sm mt-2 mb-0">管理此 Daemon 提供的 Rolithax 扩展能力。</p>
    </div>
    <p v-if="loading" class="rolithax-muted text-sm m-0">正在读取扩展状态…</p>
    <div v-else-if="error" class="flex items-center gap-3">
      <span class="text-[var(--td-error-color)] text-sm">{{ error }}</span>
      <t-button variant="text" @click="load">重试</t-button>
    </div>
    <div v-else-if="features" class="feature-list flex flex-col">
      <div v-for="entry in entries" :key="entry.key" class="feature-row flex items-center justify-between gap-4">
        <div class="min-w-0">
          <div class="rolithax-title font-medium">{{ entry.title }}</div>
          <div class="rolithax-muted text-sm mt-1">{{ entry.description }}</div>
        </div>
        <t-switch :value="features[entry.key]" :loading="updating === entry.key" :disabled="updating !== null" @change="(value: boolean) => change(entry.key, value)" />
      </div>
    </div>
  </section>
</template>

<style scoped>
@unocss;

.feature-row {
  padding: 16px 0;
  border-top: 1px solid var(--td-component-border);
}

.feature-row:last-child {
  padding-bottom: 0;
}
</style>
