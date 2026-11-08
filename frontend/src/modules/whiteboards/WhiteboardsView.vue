<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import { whiteboardsApi, type Whiteboard } from '@/core/api/whiteboards'
import { wikiApi, type Space } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'

/** Whiteboards of a space (Phase 9e): the list, and creation of an empty board. */
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
const saving = ref(false)

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
const canCreate = computed(() => !!space.value?.rights.canCreate)

async function load() {
  loading.value = true
  try {
    const [list, sp] = await Promise.all([whiteboardsApi.list(key.value), wikiApi.space(key.value)])
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

async function create() {
  if (!title.value.trim()) return
  saving.value = true
  try {
    const board = await whiteboardsApi.create(key.value, { title: title.value.trim(), elements: [] })
    creating.value = false
    title.value = ''
    await router.push({ name: 'whiteboard', params: { id: board.id } })
  } catch (e) {
    fail(e)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <section class="boards" data-testid="whiteboards">
    <header class="head">
      <h1>{{ t('whiteboards.spaceTitle', { name: space?.name ?? key }) }}</h1>
      <Button v-if="canCreate" icon="pi pi-plus" :label="t('whiteboards.new')" data-testid="new-board" @click="creating = true" />
    </header>

    <p v-if="!loading && boards.length === 0" class="muted" data-testid="no-boards">{{ t('whiteboards.none') }}</p>
    <ul class="list">
      <li v-for="b in boards" :key="b.id" class="item" data-testid="board">
        <RouterLink :to="{ name: 'whiteboard', params: { id: b.id } }" class="name" dir="auto">{{ b.title }}</RouterLink>
        <span class="muted">{{ t('whiteboards.summary', { count: b.elementCount }) }}</span>
      </li>
    </ul>

    <Dialog v-model:visible="creating" modal :header="t('whiteboards.new')" :style="{ inlineSize: 'min(26rem, 95vw)' }">
      <form class="form" @submit.prevent="create">
        <label for="board-title">{{ t('whiteboards.boardTitle') }}</label>
        <InputText id="board-title" v-model="title" maxlength="200" required data-testid="board-title" />
        <Button type="submit" :label="t('common.save')" :loading="saving" :disabled="!title.trim()" data-testid="save-board" />
      </form>
    </Dialog>
  </section>
</template>

<style scoped>
.boards { display: grid; gap: var(--raavi-space-3); max-inline-size: 56rem; }
.head { display: flex; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; align-items: center; }
h1 { margin: 0; font-size: 1.5rem; }
.list { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-2); }
.item { display: flex; justify-content: space-between; gap: var(--raavi-space-3); padding: var(--raavi-space-3); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); }
.name { font-weight: 600; overflow-wrap: anywhere; }
.form { display: grid; gap: var(--raavi-space-2); }
.muted { color: var(--raavi-text-muted); margin: 0; }
</style>
