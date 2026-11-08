<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { NodeViewWrapper, nodeViewProps } from '@tiptap/vue-3'
import { useI18n } from 'vue-i18n'
import { connectorsApi, type IssueInfo } from '@/core/api/integrations'

/**
 * Issue / pull request link (GitHub, GitLab, Jira). The document stores only the connector id and the reference; title, status and link are looked up
 * when the page is shown (through the workspace's connector, with the viewer's own session), so nothing from the tracker is ever saved in the page.
 * Answers are remembered for a minute per connector and reference to avoid one request per occurrence.
 */
const props = defineProps(nodeViewProps)
const { t } = useI18n()

interface Cached {
  at: number
  value: Promise<IssueInfo>
}
const CACHE_MS = 60_000
const cache: Map<string, Cached> = ((globalThis as { __raaviIssueCache?: Map<string, Cached> }).__raaviIssueCache ??= new Map())

const info = ref<IssueInfo | null>(null)
const state = ref<'idle' | 'loading' | 'ready' | 'error'>('idle')
const editing = ref(false)
const trackers = ref<{ id: string; name: string }[]>([])
const draftConnector = ref('')
const draftReference = ref('')

async function load() {
  const connector = props.node.attrs.connector as string | null
  const reference = String(props.node.attrs.reference ?? '')
  info.value = null
  if (!connector || reference.trim() === '') {
    state.value = 'idle'
    return
  }
  state.value = 'loading'
  const key = `${connector}|${reference}`
  let hit = cache.get(key)
  if (!hit || Date.now() - hit.at > CACHE_MS) {
    hit = { at: Date.now(), value: connectorsApi.lookup(connector, reference) }
    cache.set(key, hit)
  }
  try {
    info.value = await hit.value
    state.value = 'ready'
  } catch {
    cache.delete(key)
    state.value = 'error'
  }
}

async function startEditing() {
  if (!props.editor.isEditable) return
  draftConnector.value = (props.node.attrs.connector as string | null) ?? ''
  draftReference.value = String(props.node.attrs.reference ?? '')
  editing.value = true
  try {
    trackers.value = await connectorsApi.available()
    if (!draftConnector.value) draftConnector.value = trackers.value[0]?.id ?? ''
  } catch {
    trackers.value = []
  }
}

function apply() {
  props.updateAttributes({ connector: draftConnector.value || null, reference: draftReference.value.trim() })
  editing.value = false
}

onMounted(() => {
  void load()
  if (props.editor.isEditable && !props.node.attrs.reference) void startEditing()
})
watch(() => [props.node.attrs.connector, props.node.attrs.reference], () => void load())
</script>

<template>
  <NodeViewWrapper as="span" class="issue-wrap">
    <span class="raavi-issue" contenteditable="false" :data-state="state" data-testid="issue" @dblclick="startEditing">
      <template v-if="state === 'ready' && info">
        <a :href="info.url" target="_blank" rel="noopener noreferrer" :title="t('editor.issue.open')" data-testid="issue-link">
          <span class="ref" dir="ltr">{{ info.reference }}</span> <span dir="auto">{{ info.title }}</span>
        </a>
        <span class="lozenge" data-testid="issue-status">{{ info.status }}</span>
      </template>
      <template v-else-if="state === 'loading'">
        <span class="ref" dir="ltr">{{ node.attrs.reference }}</span> <span class="muted">{{ t('editor.issue.loading') }}</span>
      </template>
      <template v-else-if="state === 'error'">
        <span class="ref" dir="ltr">{{ node.attrs.reference }}</span> <span class="muted" data-testid="issue-error">{{ t('editor.issue.unavailable') }}</span>
      </template>
      <span v-else class="muted">{{ node.attrs.reference || t('editor.issue.placeholder') }}</span>
    </span>
    <span v-if="editing && editor.isEditable" class="issue-editor" contenteditable="false">
      <span v-if="trackers.length === 0" class="muted" data-testid="issue-none">{{ t('editor.issue.none') }}</span>
      <template v-else>
        <select v-model="draftConnector" :aria-label="t('editor.issue.connector')" data-testid="issue-connector">
          <option v-for="c in trackers" :key="c.id" :value="c.id">{{ c.name }}</option>
        </select>
        <input v-model="draftReference" dir="ltr" maxlength="300" :aria-label="t('editor.issue.reference')" :placeholder="t('editor.issue.reference')" data-testid="issue-reference" @keydown.enter.prevent="apply" />
        <button type="button" :disabled="draftReference.trim() === '' || !draftConnector" data-testid="issue-apply" @click="apply">{{ t('common.save') }}</button>
      </template>
      <button type="button" @click="editing = false">{{ t('common.close') }}</button>
    </span>
  </NodeViewWrapper>
</template>

<style scoped>
.raavi-issue { display: inline-flex; gap: 0.35em; align-items: baseline; padding: 0 0.35em; border: 1px solid var(--raavi-border, #d1d5db); border-radius: 4px; font-size: 0.9em; cursor: default; }
.raavi-issue a { color: inherit; text-decoration: none; }
.raavi-issue a:hover { text-decoration: underline; }
.ref { font-family: var(--raavi-font-mono, monospace); font-size: 0.85em; color: var(--raavi-text-muted, #6b7280); }
.lozenge { padding: 0 0.4em; border-radius: 3px; background: #e5e7eb; color: #374151; font-size: 0.75em; font-weight: 700; text-transform: uppercase; }
.muted { color: var(--raavi-text-muted, #6b7280); }
.issue-editor { display: inline-flex; gap: 4px; margin-inline-start: 4px; font-size: 0.8rem; flex-wrap: wrap; }
.issue-editor input { inline-size: 14rem; }
</style>
