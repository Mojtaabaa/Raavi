<script setup lang="ts">
import { ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import Dialog from 'primevue/dialog'
import Button from 'primevue/button'
import Message from 'primevue/message'
import { wikiApi, type RestrictionSubject, type Restrictions } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import PrincipalPicker from './PrincipalPicker.vue'

/** View restrictions (inherited by child pages) and edit restrictions (this page only), Confluence semantics. */
const props = defineProps<{ contentId: string; visible: boolean }>()
const emit = defineEmits<{ 'update:visible': [v: boolean]; saved: [] }>()
const { t, te } = useI18n()
const toast = useToast()

const data = ref<Restrictions | null>(null)
const view = ref<RestrictionSubject[]>([])
const edit = ref<RestrictionSubject[]>([])
const saving = ref(false)

watch(
  () => props.visible,
  async (open) => {
    if (!open) return
    try {
      data.value = await wikiApi.restrictions(props.contentId)
      view.value = [...data.value.view]
      edit.value = [...data.value.edit]
    } catch (e) {
      toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
    }
  },
  { immediate: true },
)

function listOf(kind: 'view' | 'edit') {
  return kind === 'view' ? view : edit
}

function add(kind: 'view' | 'edit', s: { subjectType: string; subjectId: string | null; name: string }) {
  const list = listOf(kind)
  if (!s.subjectId || (s.subjectType !== 'User' && s.subjectType !== 'Group')) return
  if (list.value.some((x) => x.subjectId === s.subjectId)) return
  list.value = [...list.value, { subjectType: s.subjectType, subjectId: s.subjectId, name: s.name }]
}

function remove(kind: 'view' | 'edit', s: RestrictionSubject) {
  const list = listOf(kind)
  list.value = list.value.filter((x) => x !== s)
}

async function save() {
  saving.value = true
  try {
    await wikiApi.saveRestrictions(props.contentId, view.value, edit.value)
    emit('saved')
    emit('update:visible', false)
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog :visible="visible" modal :header="t('wiki.restrictions.title')" :style="{ inlineSize: 'min(40rem, 95vw)' }" @update:visible="(v) => emit('update:visible', v)">
    <div v-if="data" class="grid">
      <Message v-if="data.inheritedFrom.length" severity="info" :closable="false">
        {{ t('wiki.restrictions.inherited', { pages: data.inheritedFrom.map((p) => p.title).join('، ') }) }}
      </Message>

      <section v-for="kind in (['view', 'edit'] as const)" :key="kind" class="block">
        <h3>{{ t(`wiki.restrictions.${kind}Title`) }}</h3>
        <p class="muted">{{ t(`wiki.restrictions.${kind}Help`) }}</p>
        <ul class="chips" :aria-label="t(`wiki.restrictions.${kind}Title`)">
          <li v-if="(kind === 'view' ? view : edit).length === 0" class="muted">{{ t('wiki.restrictions.none') }}</li>
          <li v-for="s in kind === 'view' ? view : edit" :key="s.subjectId" class="chip">
            <i :class="s.subjectType === 'Group' ? 'pi pi-users' : 'pi pi-user'" aria-hidden="true" />
            <span>{{ s.name ?? s.subjectId }}</span>
            <Button
              text
              rounded
              size="small"
              icon="pi pi-times"
              :aria-label="t('common.delete')"
              @click="remove(kind, s)"
            />
          </li>
        </ul>
        <label class="sr-only" :for="`pick-${kind}`">{{ t('wiki.permissions.searchPrincipal') }}</label>
        <PrincipalPicker :input-id="`pick-${kind}`" @pick="(s) => add(kind, s)" />
      </section>
    </div>
    <template #footer>
      <Button text :label="t('common.cancel')" @click="emit('update:visible', false)" />
      <Button :label="t('common.save')" :loading="saving" data-testid="save-restrictions" @click="save" />
    </template>
  </Dialog>
</template>

<style scoped>
.grid { display: grid; gap: var(--raavi-space-4); }
.block h3 { margin: 0 0 var(--raavi-space-1); font-size: 1rem; }
.muted { color: var(--raavi-text-muted); font-size: 0.9rem; margin: 0 0 var(--raavi-space-2); }
.chips { list-style: none; padding: 0; margin: 0 0 var(--raavi-space-2); display: flex; flex-wrap: wrap; gap: var(--raavi-space-2); }
.chip { display: inline-flex; align-items: center; gap: var(--raavi-space-1); padding-inline: var(--raavi-space-2); border: 1px solid var(--raavi-border); border-radius: 999px; }
.sr-only { position: absolute; inline-size: 1px; block-size: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
</style>
