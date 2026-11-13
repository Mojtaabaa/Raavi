<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Select from 'primevue/select'
import AutoComplete from 'primevue/autocomplete'
import Tag from 'primevue/tag'
import { wikiApi, type ContentListItem, type KnowledgeRelationship } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'

const props = defineProps<{ contentId: string; canEdit: boolean }>()
const { t, te } = useI18n()
const toast = useToast()

const rows = ref<KnowledgeRelationship[]>([])
const loading = ref(true)
const open = ref(false)
const saving = ref(false)
const relationshipType = ref('RelatedTo')
const targetSuggestions = ref<ContentListItem[]>([])
const selectedTarget = ref<ContentListItem | null>(null)

function labelType(v: string) {
  const key = `wiki.relationships.types.${v}`
  return te(key) ? t(key) : v
}

const types = ['RelatedTo', 'References', 'DependsOn', 'Documents', 'Implements', 'Supersedes']
const typeOptions = types.map((value) => ({ value, label: labelType(value) }))

async function load() {
  loading.value = true
  try {
    rows.value = await wikiApi.relationships(props.contentId)
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
    rows.value = []
  } finally {
    loading.value = false
  }
}

watch(() => props.contentId, load)
onMounted(load)

async function suggest(e: { query: string }) {
  const q = e.query.trim()
  if (q.length < 2) {
    targetSuggestions.value = []
    return
  }
  try {
    const found = await wikiApi.quickFind(q)
    targetSuggestions.value = found.content.filter((c) => c.id !== props.contentId)
  } catch {
    targetSuggestions.value = []
  }
}

async function create() {
  if (!selectedTarget.value) return
  saving.value = true
  try {
    await wikiApi.createRelationship(props.contentId, {
      relationshipType: relationshipType.value,
      targetContentId: selectedTarget.value.id,
    })
    open.value = false
    selectedTarget.value = null
    await load()
    toast.add({ severity: 'success', summary: t('wiki.relationships.created'), life: 2500 })
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
  } finally {
    saving.value = false
  }
}

async function remove(r: KnowledgeRelationship) {
  try {
    await wikiApi.removeRelationship(props.contentId, r.id)
    await load()
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
  }
}
</script>

<template>
  <section class="rel" :aria-label="t('wiki.relationships.title')" data-testid="relationships-panel">
    <header class="head">
      <h2>{{ t('wiki.relationships.title') }}</h2>
      <Button v-if="canEdit" size="small" text icon="pi pi-plus" :label="t('wiki.relationships.add')" data-testid="relationship-add" @click="open = true" />
    </header>
    <p v-if="!loading && rows.length === 0" class="muted">{{ t('wiki.relationships.empty') }}</p>
    <ul v-else class="list">
      <li v-for="r in rows" :key="r.id" class="item">
        <Tag severity="secondary" :value="labelType(r.relationshipType)" />
        <span class="dir muted">{{ t(`wiki.relationships.direction.${r.direction}`) }}</span>
        <RouterLink
          v-if="r.targetKind === 'Content' && r.targetContentId && r.spaceKey"
          :to="{ name: 'page', params: { key: r.spaceKey, id: r.targetContentId, slug: r.slug || undefined } }"
          dir="auto"
        >{{ r.title }}</RouterLink>
        <span v-else dir="auto">{{ r.title ?? r.targetExternalKey }}</span>
        <Tag v-if="r.origin !== 'Manual'" severity="warn" :value="t(`wiki.relationships.origin.${r.origin}`)" />
        <Button
          v-if="r.canRemove"
          text
          rounded
          size="small"
          icon="pi pi-times"
          :aria-label="t('wiki.relationships.remove')"
          @click="remove(r)"
        />
      </li>
    </ul>

    <Dialog v-model:visible="open" modal :header="t('wiki.relationships.add')" :style="{ inlineSize: 'min(28rem, 95vw)' }">
      <div class="form">
        <label for="rel-type">{{ t('wiki.relationships.type') }}</label>
        <Select id="rel-type" v-model="relationshipType" :options="typeOptions" option-label="label" option-value="value" data-testid="rel-type" />
        <label for="rel-target">{{ t('wiki.relationships.target') }}</label>
        <AutoComplete
          id="rel-target"
          v-model="selectedTarget"
          :suggestions="targetSuggestions"
          option-label="title"
          :placeholder="t('wiki.relationships.targetHint')"
          force-selection
          fluid
          data-testid="rel-target"
          @complete="suggest"
        >
          <template #option="{ option }">
            <span dir="auto">{{ option.title }}</span>
            <small class="muted"> · {{ option.spaceName }}</small>
          </template>
        </AutoComplete>
      </div>
      <template #footer>
        <Button :label="t('common.cancel')" text @click="open = false" />
        <Button :label="t('wiki.relationships.add')" :loading="saving" :disabled="!selectedTarget" data-testid="rel-save" @click="create" />
      </template>
    </Dialog>
  </section>
</template>

<style scoped>
.rel { display: grid; gap: var(--raavi-space-2); border-block-start: 1px solid var(--raavi-border); padding-block-start: var(--raavi-space-3); }
.head { display: flex; align-items: center; justify-content: space-between; gap: var(--raavi-space-2); }
.head h2 { margin: 0; font-size: 1rem; }
.list { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-2); }
.item { display: flex; flex-wrap: wrap; align-items: center; gap: var(--raavi-space-2); }
.muted { color: var(--raavi-text-muted); }
.form { display: grid; gap: var(--raavi-space-2); }
</style>
