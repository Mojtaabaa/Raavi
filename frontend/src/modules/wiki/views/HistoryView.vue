<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { useToast } from 'primevue/usetoast'
import { useConfirm } from 'primevue/useconfirm'
import Button from 'primevue/button'
import Tag from 'primevue/tag'
import Dialog from 'primevue/dialog'
import RadioButton from 'primevue/radiobutton'
import { wikiApi, type Diff, type Page, type Version, type VersionSummary } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'
import RaaviEditor from '@/modules/editor/components/RaaviEditor.vue'
import { useSpaceContext } from '../spaceContext'
import { useCrumbsStore } from '@/stores/crumbs'
import { spaceTrail } from '@/core/navigation/trail'

/** Page history (ADR-019): every published version stays available; compare any two, view, restore as a new version. */
const { t, te } = useI18n()
const route = useRoute()
const router = useRouter()
const toast = useToast()
const confirm = useConfirm()
const locale = useLocaleStore()
const ctx = useSpaceContext()
const crumbs = useCrumbsStore()

const id = computed(() => String(route.params.id))
const page = ref<Page | null>(null)
const etag = ref<string | null>(null)
const versions = ref<VersionSummary[]>([])
const from = ref<number | null>(null)
const to = ref<number | 'draft' | null>(null)
const diff = ref<Diff | null>(null)
const viewing = ref<Version | null>(null)
const fmt = (iso: string) => formatDate(iso, locale.locale, { dateStyle: 'medium', timeStyle: 'short' })
const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })

async function load() {
  try {
    const p = await wikiApi.content(id.value)
    page.value = p.value
    etag.value = p.etag
    ctx.activePath.value = [...p.value.breadcrumbs.map((b) => b.id), p.value.id]
    crumbs.set(spaceTrail({ key: p.value.spaceKey, name: p.value.spaceName }, t, ...p.value.breadcrumbs.map((b) => ({ label: b.title, to: { name: 'page', params: { key: p.value.spaceKey, id: b.id } } })), { label: p.value.title, to: { name: 'page', params: { key: p.value.spaceKey, id: p.value.id } } }, { label: t('wiki.page.history') }))
    versions.value = await wikiApi.versions(id.value)
    if (versions.value.length >= 2) {
      to.value = versions.value[0]!.version
      from.value = versions.value[1]!.version
      await compare()
    }
  } catch (e) {
    fail(e)
  }
}
onMounted(load)

async function compare() {
  if (from.value === null || to.value === null) return
  try {
    diff.value = await wikiApi.diff(id.value, from.value, to.value)
  } catch (e) {
    fail(e)
  }
}

async function view(v: VersionSummary) {
  try {
    viewing.value = await wikiApi.version(id.value, v.version)
  } catch (e) {
    fail(e)
  }
}

function restore(v: VersionSummary) {
  confirm.require({
    header: t('common.confirmTitle'),
    message: t('wiki.history.confirmRestore', { n: v.version }),
    acceptLabel: t('common.yes'),
    rejectLabel: t('common.no'),
    accept: async () => {
      try {
        const r = await wikiApi.restoreVersion(id.value, v.version, etag.value)
        toast.add({ severity: 'success', summary: t(`wiki.publish.outcome.${r.value.outcome}`), life: 4000 })
        if (r.value.outcome === 'RestoredToDraft') await router.push({ name: 'page-edit', params: { key: r.value.page.spaceKey, id: id.value } })
        else await load()
      } catch (e) {
        fail(e)
      }
    },
  })
}
</script>

<template>
  <section v-if="page" class="history">
    <nav class="crumbs"><RouterLink :to="{ name: 'page', params: { key: page.spaceKey, id: page.id } }">← {{ page.title }}</RouterLink></nav>
    <h1>{{ t('wiki.history.title', { title: page.title }) }}</h1>

    <table class="versions">
      <thead>
        <tr>
          <th scope="col">{{ t('wiki.history.from') }}</th>
          <th scope="col">{{ t('wiki.history.to') }}</th>
          <th scope="col">{{ t('wiki.history.version') }}</th>
          <th scope="col">{{ t('wiki.history.details') }}</th>
          <th scope="col"><span class="sr-only">{{ t('common.actions') }}</span></th>
        </tr>
      </thead>
      <tbody>
        <tr v-if="page.hasDraft">
          <td />
          <td><RadioButton v-model="to" value="draft" input-id="to-draft" :aria-label="t('wiki.history.compareTo', { n: t('wiki.history.draft') })" @change="compare" /></td>
          <td><Tag severity="info" :value="t('wiki.history.draft')" /></td>
          <td class="muted">{{ t('wiki.history.draftHint') }}</td>
          <td />
        </tr>
        <tr v-for="v in versions" :key="v.version" :class="{ current: v.version === page.version }">
          <td><RadioButton v-model="from" :value="v.version" :input-id="`from-${v.version}`" :aria-label="t('wiki.history.compareFrom', { n: v.version })" @change="compare" /></td>
          <td><RadioButton v-model="to" :value="v.version" :input-id="`to-${v.version}`" :aria-label="t('wiki.history.compareTo', { n: v.version })" @change="compare" /></td>
          <td>
            <strong>{{ t('wiki.page.versionN', { n: v.version }) }}</strong>
            <Tag v-if="v.version === page.version" severity="success" :value="t('wiki.history.current')" class="tag" />
            <Tag v-if="v.type === 'Restore'" severity="warn" :value="t('wiki.history.restoredFrom', { n: v.restoredFrom ?? '?' })" class="tag" />
            <Tag v-if="v.type === 'Import'" severity="secondary" :value="t('wiki.history.imported')" class="tag" />
          </td>
          <td>
            <div dir="auto">{{ v.title }}</div>
            <small class="muted">{{ (v.contributors.length ? v.contributors.join('، ') : v.createdByName) ?? t('wiki.unknownUser') }} · {{ fmt(v.createdAt) }}</small>
            <div v-if="v.changeSummary" class="summary" dir="auto">{{ v.changeSummary }}</div>
          </td>
          <td class="tools">
            <Button text size="small" :label="t('wiki.history.view')" @click="view(v)" />
            <Button v-if="page.rights.canPublish && v.version !== page.version" text size="small" :label="t('wiki.history.restore')" data-testid="restore-version" @click="restore(v)" />
          </td>
        </tr>
      </tbody>
    </table>

    <section v-if="diff" class="diff" :aria-label="t('wiki.history.comparison')">
      <h2>{{ t('wiki.history.comparing', { from: diff.from.version ?? '', to: diff.to.version ?? t('wiki.history.draft') }) }}</h2>
      <p v-if="diff.titleChanged" class="title-change"><del dir="auto">{{ diff.from.title }}</del> → <ins dir="auto">{{ diff.to.title }}</ins></p>
      <p v-if="diff.blocks.every((b) => b.op === 'Equal') && !diff.titleChanged" class="muted">{{ t('wiki.history.noChanges') }}</p>
      <div v-for="(b, i) in diff.blocks" :key="i" :class="['block', b.op.toLowerCase()]">
        <span class="marker" aria-hidden="true">{{ b.op === 'Insert' ? '+' : b.op === 'Delete' ? '−' : b.op === 'Change' ? '~' : '' }}</span>
        <span class="sr-only">{{ t(`wiki.history.ops.${b.op}`) }}</span>
        <span class="text" dir="auto">
        <template v-if="b.op === 'Change' && b.segments">
          <template v-for="(s, j) in b.segments" :key="j">
            <ins v-if="s.op === 'Insert'">{{ s.text }}</ins>
            <del v-else-if="s.op === 'Delete'">{{ s.text }}</del>
            <span v-else>{{ s.text }}</span>
          </template>
        </template>
        <ins v-else-if="b.op === 'Insert'">{{ b.after }}</ins>
        <del v-else-if="b.op === 'Delete'">{{ b.before }}</del>
        <span v-else>{{ b.after }}</span>
        </span>
      </div>
    </section>

    <Dialog :visible="!!viewing" modal :header="viewing ? t('wiki.history.viewing', { n: viewing.version }) : ''" :style="{ inlineSize: 'min(60rem, 95vw)' }" @update:visible="(v) => !v && (viewing = null)">
      <template v-if="viewing">
        <h1 class="preview-title" dir="auto">{{ viewing.title }}</h1>
        <RaaviEditor :model-value="viewing.body" />
      </template>
    </Dialog>
  </section>
</template>

<style scoped>
.history { display: grid; gap: var(--raavi-space-3); max-inline-size: 64rem; }
h1 { margin: 0; font-size: 1.4rem; }
h2 { font-size: 1.05rem; margin: 0 0 var(--raavi-space-2); }
.versions { border-collapse: collapse; inline-size: 100%; }
.versions th, .versions td { padding: var(--raavi-space-2); border-block-end: 1px solid var(--raavi-border); text-align: start; vertical-align: top; }
.versions tr.current { background: rgba(22, 163, 74, 0.05); }
.tag { margin-inline-start: var(--raavi-space-1); }
.tools { white-space: nowrap; }
.summary { font-style: italic; }
.muted { color: var(--raavi-text-muted); }
.diff { border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); padding: var(--raavi-space-3); }
.block { display: flex; gap: var(--raavi-space-2); padding: 2px var(--raavi-space-2); white-space: pre-wrap; overflow-wrap: anywhere; }
.block.insert { background: #f0fdf4; }
.block.delete { background: #fef2f2; }
.block.change { background: #fffbeb; }
.text { flex: 1; unicode-bidi: plaintext; }
.marker { inline-size: 1em; color: var(--raavi-text-muted); flex: none; }
ins { background: #bbf7d0; text-decoration: none; }
del { background: #fecaca; }
.title-change { font-weight: 600; }
.preview-title { font-size: 1.8rem; }
.sr-only { position: absolute; inline-size: 1px; block-size: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
</style>
