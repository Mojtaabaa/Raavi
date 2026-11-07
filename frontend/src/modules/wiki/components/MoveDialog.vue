<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import Dialog from 'primevue/dialog'
import Button from 'primevue/button'
import Select from 'primevue/select'
import Checkbox from 'primevue/checkbox'
import { wikiApi, type Page, type SpaceSummary, type TreeNode } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import PageTree from './PageTree.vue'

/** Moves a page (with its subtree) under another parent and/or into another space. */
const props = defineProps<{ page: Page; etag: string | null; visible: boolean }>()
const emit = defineEmits<{ 'update:visible': [v: boolean]; moved: [page: Page, etag: string | null] }>()
const { t, te } = useI18n()
const toast = useToast()

const spaces = ref<SpaceSummary[]>([])
const spaceKey = ref(props.page.spaceKey)
const parent = ref<TreeNode | null>(null)
const topLevel = ref(false)
const saving = ref(false)

watch(
  () => props.visible,
  async (open) => {
    if (!open) return
    spaceKey.value = props.page.spaceKey
    parent.value = null
    topLevel.value = false
    spaces.value = await wikiApi.spaces().catch(() => [])
  },
  { immediate: true },
)
watch(spaceKey, () => (parent.value = null))

const canSave = computed(() => topLevel.value || parent.value !== null)

async function save() {
  saving.value = true
  try {
    const r = await wikiApi.move(props.page.id, props.etag, {
      spaceKey: spaceKey.value === props.page.spaceKey ? null : spaceKey.value,
      parentId: topLevel.value ? null : (parent.value?.id ?? null),
      position: null,
    })
    emit('moved', r.value, r.etag)
    emit('update:visible', false)
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog :visible="visible" modal :header="t('wiki.move.title', { title: page.title })" :style="{ inlineSize: 'min(36rem, 95vw)' }" @update:visible="(v) => emit('update:visible', v)">
    <div class="grid">
      <label for="move-space">{{ t('wiki.move.space') }}</label>
      <Select v-model="spaceKey" input-id="move-space" :options="spaces" option-label="name" option-value="key" filter />
      <div class="row">
        <Checkbox v-model="topLevel" input-id="move-top" binary />
        <label for="move-top">{{ t('wiki.move.topLevel') }}</label>
      </div>
      <template v-if="!topLevel">
        <span>{{ t('wiki.move.parent') }}</span>
        <div class="tree-box">
          <PageTree :space-key="spaceKey" :selected-id="parent?.id" :disabled-ids="[page.id]" @select="(n) => (parent = n)" />
        </div>
      </template>
    </div>
    <template #footer>
      <Button text :label="t('common.cancel')" @click="emit('update:visible', false)" />
      <Button :label="t('wiki.move.submit')" :disabled="!canSave" :loading="saving" @click="save" />
    </template>
  </Dialog>
</template>

<style scoped>
.grid { display: grid; gap: var(--raavi-space-2); }
.row { display: flex; align-items: center; gap: var(--raavi-space-2); }
.tree-box { max-block-size: 18rem; overflow: auto; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); padding: var(--raavi-space-2); }
</style>
