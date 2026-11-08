<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import { databasesApi, type SpaceDatabase } from '@/core/api/databases'
import { wikiApi, type Space } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'

/** Databases of a space (Phase 9c): the list, and creation of a new database with a first "Name" column. */
const { t, te } = useI18n()
const route = useRoute()
const router = useRouter()
const toast = useToast()

const key = computed(() => String(route.params.key))
const space = ref<Space | null>(null)
const databases = ref<SpaceDatabase[]>([])
const loading = ref(true)
const creating = ref(false)
const name = ref('')
const saving = ref(false)

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
const canCreate = computed(() => !!space.value?.rights.canCreate)

async function load() {
  loading.value = true
  try {
    const [list, sp] = await Promise.all([databasesApi.list(key.value), wikiApi.space(key.value)])
    databases.value = list
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
  if (!name.value.trim()) return
  saving.value = true
  try {
    const db = await databasesApi.create(key.value, { name: name.value.trim(), description: null, columns: [{ name: t('databases.firstColumn'), type: 'Text' }] })
    creating.value = false
    name.value = ''
    await router.push({ name: 'database', params: { id: db.id } })
  } catch (e) {
    fail(e)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <section class="databases" data-testid="databases">
    <header class="head">
      <h1>{{ t('databases.spaceTitle', { name: space?.name ?? key }) }}</h1>
      <Button v-if="canCreate" icon="pi pi-plus" :label="t('databases.new')" data-testid="new-database" @click="creating = true" />
    </header>

    <p v-if="!loading && databases.length === 0" class="muted" data-testid="no-databases">{{ t('databases.none') }}</p>
    <ul class="list">
      <li v-for="d in databases" :key="d.id" class="item" data-testid="database">
        <RouterLink :to="{ name: 'database', params: { id: d.id } }" class="name" dir="auto">{{ d.name }}</RouterLink>
        <span class="muted">{{ t('databases.summary', { columns: d.columns.length, rows: d.rowCount }) }}</span>
      </li>
    </ul>

    <Dialog v-model:visible="creating" modal :header="t('databases.new')" :style="{ inlineSize: 'min(26rem, 95vw)' }">
      <form class="form" @submit.prevent="create">
        <label for="db-name">{{ t('databases.name') }}</label>
        <InputText id="db-name" v-model="name" maxlength="200" required data-testid="database-name" />
        <Button type="submit" :label="t('common.save')" :loading="saving" :disabled="!name.trim()" data-testid="save-database" />
      </form>
    </Dialog>
  </section>
</template>

<style scoped>
.databases { display: grid; gap: var(--raavi-space-3); max-inline-size: 56rem; }
.head { display: flex; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; align-items: center; }
h1 { margin: 0; font-size: 1.5rem; }
.list { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-2); }
.item { display: flex; justify-content: space-between; gap: var(--raavi-space-3); padding: var(--raavi-space-3); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); }
.name { font-weight: 600; overflow-wrap: anywhere; }
.form { display: grid; gap: var(--raavi-space-2); }
.muted { color: var(--raavi-text-muted); margin: 0; }
</style>
