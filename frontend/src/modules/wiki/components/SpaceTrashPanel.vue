<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import { useConfirm } from 'primevue/useconfirm'
import Button from 'primevue/button'
import { wikiApi, type TrashItem } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'
import { useSpaceContext } from '../spaceContext'

/** Space trash: restore deleted pages (with their children) or delete them permanently. */
const { t, te } = useI18n()
const toast = useToast()
const confirm = useConfirm()
const locale = useLocaleStore()
const ctx = useSpaceContext()
const items = ref<TrashItem[]>([])
const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })

async function load() {
  if (!ctx.space.value?.rights.canView) return
  items.value = await wikiApi.trashList(ctx.space.value.key).catch(() => [])
}
onMounted(load)

async function restore(item: TrashItem) {
  try {
    await wikiApi.restoreFromTrash(item.id)
    ctx.reloadTree()
    await load()
  } catch (e) {
    fail(e)
  }
}

function purge(item: TrashItem) {
  confirm.require({
    header: t('common.confirmTitle'),
    message: t('wiki.trash.confirmPurge', { title: item.title, n: item.descendants }),
    acceptLabel: t('wiki.trash.purge'),
    rejectLabel: t('common.cancel'),
    acceptClass: 'p-button-danger',
    accept: async () => {
      try {
        await wikiApi.purge(item.id)
        await load()
      } catch (e) {
        fail(e)
      }
    },
  })
}
</script>

<template>
  <div class="trash">
    <p class="muted">{{ t('wiki.trash.help') }}</p>
    <p v-if="items.length === 0" class="muted">{{ t('wiki.trash.empty') }}</p>
    <ul>
      <li v-for="item in items" :key="item.id">
        <div>
          <i :class="item.contentType === 'folder' ? 'pi pi-folder' : 'pi pi-file'" aria-hidden="true" />
          <span dir="auto"> {{ item.title }}</span>
          <small class="muted">
            · {{ t('wiki.trash.deleted', { name: item.deletedByName ?? t('wiki.unknownUser'), time: formatDate(item.deletedAt, locale.locale, { dateStyle: 'medium', timeStyle: 'short' }) }) }}
            <template v-if="item.descendants"> · {{ t('wiki.trash.children', { n: item.descendants }) }}</template>
          </small>
        </div>
        <div class="actions">
          <Button size="small" text :label="t('wiki.trash.restore')" icon="pi pi-replay" data-testid="restore-trash" @click="restore(item)" />
          <Button size="small" text severity="danger" :label="t('wiki.trash.purge')" icon="pi pi-times" @click="purge(item)" />
        </div>
      </li>
    </ul>
  </div>
</template>

<style scoped>
.trash { display: grid; gap: var(--raavi-space-2); }
ul { list-style: none; padding: 0; margin: 0; display: grid; gap: var(--raavi-space-2); }
li { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: var(--raavi-space-2); border-block-end: 1px solid var(--raavi-border); padding-block-end: var(--raavi-space-2); }
.muted { color: var(--raavi-text-muted); margin: 0; }
.actions { display: flex; gap: var(--raavi-space-1); }
</style>
