<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import AutoComplete from 'primevue/autocomplete'
import { wikiApi, type DirectoryEntry } from '@/core/api/wiki'

/** Searches users and groups of the current workspace (server-side, tenant-scoped) and emits the chosen principal. */
const props = defineProps<{ inputId: string; allowAllMembers?: boolean; groupsOnly?: boolean }>()
const emit = defineEmits<{ pick: [subject: { subjectType: 'User' | 'Group' | 'AllMembers'; subjectId: string | null; name: string }] }>()
const { t } = useI18n()

interface Option {
  kind: 'User' | 'Group' | 'AllMembers'
  id: string | null
  label: string
  detail: string | null
}
const query = ref<Option | string | null>(null)
const suggestions = ref<Option[]>([])

async function search(e: { query: string }) {
  const all: Option[] = props.allowAllMembers ? [{ kind: 'AllMembers', id: null, label: t('wiki.permissions.allMembers'), detail: null }] : []
  let found: DirectoryEntry[] = []
  try {
    found = e.query.trim() ? await wikiApi.directory(e.query.trim()) : []
  } catch {
    found = []
  }
  suggestions.value = [
    ...all,
    ...found.filter((d) => !props.groupsOnly || d.kind === 'Group').map((d) => ({ kind: d.kind, id: d.id, label: d.displayName, detail: d.detail })),
  ]
}

function select(e: { value: Option }) {
  emit('pick', { subjectType: e.value.kind, subjectId: e.value.id, name: e.value.label })
  query.value = null
}
</script>

<template>
  <AutoComplete
    v-model="query"
    :input-id="inputId"
    :suggestions="suggestions"
    option-label="label"
    :placeholder="t('wiki.permissions.searchPrincipal')"
    :min-length="1"
    :delay="250"
    complete-on-focus
    fluid
    @complete="search"
    @option-select="select"
  >
    <template #option="{ option }">
      <span class="opt">
        <i :class="option.kind === 'Group' ? 'pi pi-users' : option.kind === 'AllMembers' ? 'pi pi-globe' : 'pi pi-user'" aria-hidden="true" />
        <span>{{ option.label }}</span>
        <small v-if="option.detail" class="muted">{{ option.detail }}</small>
      </span>
    </template>
  </AutoComplete>
</template>

<style scoped>
.opt { display: inline-flex; align-items: center; gap: var(--raavi-space-2); }
.muted { color: var(--raavi-text-muted); }
</style>
