<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import { whiteboardsApi, type Whiteboard, type WhiteboardInput } from '@/core/api/whiteboards'
import { wikiApi, type Space } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import { buildTemplate, TEMPLATE_IDS, templateLabels, type TemplateId } from './boardTemplates'

/** Whiteboards of a space: the list, favourites, archive, and creation from a blank board or a client template. */
const { t, te } = useI18n()
const route = useRoute()
const router = useRouter()
const toast = useToast()

const key = computed(() => String(route.params.key))
const space = ref<Space | null>(null)
const boards = ref<Whiteboard[]>([])
const loading = ref(true)
const creating = ref(false)
const title = ref('')
const description = ref('')
const templateChoice = ref<TemplateId | ''>('')
const showArchived = ref(false)
const saving = ref(false)

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
const canCreate = computed(() => !!space.value?.rights.canCreate)

async function load() {
  loading.value = true
  try {
    const [list, sp] = await Promise.all([whiteboardsApi.list(key.value, showArchived.value), wikiApi.space(key.value)])
    boards.value = list
    space.value = sp.value
  } catch (e) {
    fail(e)
  } finally {
    loading.value = false
  }
}
onMounted(load)
watch(key, load)
watch(showArchived, load)

function openCreate() {
  title.value = ''
  description.value = ''
  templateChoice.value = ''
  creating.value = true
}

async function create() {
  if (!title.value.trim()) return
  saving.value = true
  try {
    const made = templateChoice.value
      ? buildTemplate(templateChoice.value, 480, 320, templateLabels((k) => t(k))).map((e) => ({ ...e, rtl: /[\u0590-\u08FF]/.test(e.text) }))
      : []
    const body: WhiteboardInput = { title: title.value.trim(), elements: made }
    const note = description.value.trim()
    if (note) body.description = note
    const board = await whiteboardsApi.create(key.value, body)
    creating.value = false
    title.value = ''
    description.value = ''
    templateChoice.value = ''
    await router.push({ name: 'whiteboard', params: { id: board.id } })
  } catch (e) {
    fail(e)
  } finally {
    saving.value = false
  }
}

async function toggleFavorite(board: Whiteboard) {
  try {
    if (board.favorited) await whiteboardsApi.unfavorite(board.id)
    else await whiteboardsApi.favorite(board.id)
    boards.value = boards.value.map((b) => (b.id === board.id ? { ...b, favorited: !b.favorited } : b))
  } catch (e) {
    fail(e)
  }
}

async function duplicateBoard(board: Whiteboard) {
  try {
    const copy = await whiteboardsApi.duplicate(board.id)
    await router.push({ name: 'whiteboard', params: { id: copy.id } })
  } catch (e) {
    fail(e)
  }
}

async function archiveBoard(board: Whiteboard) {
  try {
    await whiteboardsApi.archive(board.id)
    await load()
  } catch (e) {
    fail(e)
  }
}

async function restoreBoard(board: Whiteboard) {
  try {
    await whiteboardsApi.restore(board.id)
    await load()
  } catch (e) {
    fail(e)
  }
}
</script>

<template>
  <section class="boards" data-testid="whiteboards">
    <header class="head">
      <h1>{{ t('whiteboards.spaceTitle', { name: space?.name ?? key }) }}</h1>
      <div class="tools">
        <Button :icon="showArchived ? 'pi pi-eye' : 'pi pi-eye-slash'" :label="t('whiteboards.showArchived')" :outlined="!showArchived" :severity="showArchived ? 'primary' : 'secondary'" data-testid="show-archived" @click="showArchived = !showArchived" />
        <Button v-if="canCreate" icon="pi pi-plus" :label="t('whiteboards.new')" data-testid="new-board" @click="openCreate" />
      </div>
    </header>

    <p v-if="!loading && boards.length === 0" class="muted" data-testid="no-boards">{{ t('whiteboards.none') }}</p>
    <ul class="list">
      <li v-for="b in boards" :key="b.id" class="item" data-testid="board">
        <div class="main">
          <RouterLink :to="{ name: 'whiteboard', params: { id: b.id } }" class="name" dir="auto">{{ b.title }}</RouterLink>
          <p v-if="b.description" class="muted desc" dir="auto">{{ b.description }}</p>
          <span class="muted">{{ t('whiteboards.summary', { count: b.elementCount }) }}<template v-if="b.archivedAt"> · {{ t('whiteboards.archived') }}</template></span>
        </div>
        <div class="actions">
          <Button :icon="b.favorited ? 'pi pi-star-fill' : 'pi pi-star'" text rounded size="small" :aria-pressed="!!b.favorited" :aria-label="b.favorited ? t('whiteboards.unfavorite') : t('whiteboards.favorite')" data-testid="favorite-board" @click="toggleFavorite(b)" />
          <Button v-if="b.canEdit" icon="pi pi-clone" text size="small" :label="t('whiteboards.duplicateBoard')" data-testid="duplicate-board" @click="duplicateBoard(b)" />
          <Button v-if="b.canEdit && !b.archivedAt" icon="pi pi-inbox" text size="small" :label="t('whiteboards.archive')" data-testid="archive-board" @click="archiveBoard(b)" />
          <Button v-if="b.canEdit && b.archivedAt" icon="pi pi-replay" text size="small" :label="t('whiteboards.restore')" data-testid="restore-board" @click="restoreBoard(b)" />
        </div>
      </li>
    </ul>

    <Dialog v-model:visible="creating" modal :header="t('whiteboards.new')" :style="{ inlineSize: 'min(26rem, 95vw)' }">
      <form class="form" @submit.prevent="create">
        <label for="board-title">{{ t('whiteboards.boardTitle') }}</label>
        <InputText id="board-title" v-model="title" maxlength="200" required data-testid="board-title" />
        <label for="board-description">{{ t('whiteboards.description') }}</label>
        <textarea id="board-description" v-model="description" maxlength="2000" rows="3" dir="auto" data-testid="board-description" />
        <label for="board-template">{{ t('whiteboards.templates') }}</label>
        <select id="board-template" v-model="templateChoice" data-testid="board-template">
          <option value="">{{ t('whiteboards.templateBlank') }}</option>
          <option v-for="id in TEMPLATE_IDS" :key="id" :value="id">{{ t(`whiteboards.templateNames.${id}`) }}</option>
        </select>
        <Button type="submit" :label="t('common.save')" :loading="saving" :disabled="!title.trim()" data-testid="save-board" />
      </form>
    </Dialog>
  </section>
</template>

<style scoped>
.boards { display: grid; gap: var(--raavi-space-3); max-inline-size: 56rem; }
.head { display: flex; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; align-items: center; }
h1 { margin: 0; font-size: 1.5rem; }
.tools, .actions { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; }
.list { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-2); }
.item { display: flex; justify-content: space-between; gap: var(--raavi-space-3); align-items: start; padding: var(--raavi-space-3); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); }
.main { display: grid; gap: 0.2rem; min-inline-size: 0; }
.name { font-weight: 600; overflow-wrap: anywhere; }
.desc { overflow-wrap: anywhere; }
.form { display: grid; gap: var(--raavi-space-2); }
.form textarea, .form select { font: inherit; padding: 0.35rem; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); background: var(--raavi-surface); color: inherit; }
.muted { color: var(--raavi-text-muted); margin: 0; }
</style>
