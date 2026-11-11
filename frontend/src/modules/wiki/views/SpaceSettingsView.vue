<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import { useConfirm } from 'primevue/useconfirm'
import Tabs from 'primevue/tabs'
import TabList from 'primevue/tablist'
import Tab from 'primevue/tab'
import TabPanels from 'primevue/tabpanels'
import TabPanel from 'primevue/tabpanel'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Textarea from 'primevue/textarea'
import Select from 'primevue/select'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import { SPACE_TYPES, wikiApi, type Grant, type PermissionAction, type RaaviDoc, type SpaceType, type Template } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import { downloadFrom } from '@/core/api/ops'
import GrantMatrix from '../components/GrantMatrix.vue'
import SpacePublishingPanel from '../components/SpacePublishingPanel.vue'
import SpaceTrashPanel from '../components/SpaceTrashPanel.vue'
import RaaviEditor from '@/modules/editor/components/RaaviEditor.vue'
import { useSpaceContext } from '../spaceContext'

const { t, te } = useI18n()
const toast = useToast()
const confirm = useConfirm()
const ctx = useSpaceContext()

const space = computed(() => ctx.space.value!)
const form = ref({ name: '', description: '', type: 'Team' as SpaceType })
const grants = ref<Grant[]>([])
const actions = ref<PermissionAction[]>([])
const templates = ref<Template[]>([])
const tplOpen = ref(false)
const tplForm = ref({ id: null as string | null, name: '', description: '', body: null as RaaviDoc | null })
const busy = ref(false)
// Panels with live lists load when their tab is opened (and reload each time).
const tab = ref<string | number>('details')

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
const ok = () => toast.add({ severity: 'success', summary: t('common.saved'), life: 2500 })
const typeOptions = computed(() => SPACE_TYPES.map((v) => ({ value: v, label: t(`wiki.spaceTypes.${v}`) })))

onMounted(async () => {
  ctx.activePath.value = []
  form.value = { name: space.value.name, description: space.value.description ?? '', type: space.value.type }
  try {
    const p = await wikiApi.permissions(space.value.key)
    grants.value = p.grants
    actions.value = p.actions
    if (space.value.rights.canView) templates.value = (await wikiApi.spaceTemplates(space.value.key)).filter((x) => x.spaceId !== null)
  } catch (e) {
    fail(e)
  }
})

async function saveDetails() {
  busy.value = true
  try {
    await wikiApi.updateSpace(space.value.key, ctx.etag.value, { name: form.value.name, description: form.value.description || null, type: form.value.type, homePageId: space.value.homePageId })
    await ctx.reloadSpace()
    ok()
  } catch (e) {
    fail(e)
  } finally {
    busy.value = false
  }
}

const exporting = ref(false)
async function exportSpace() {
  exporting.value = true
  try {
    await downloadFrom(`/spaces/${space.value.key}/export`, `raavi-space-${space.value.key}.zip`)
    toast.add({ severity: 'success', summary: t('spaceTransfer.exported'), life: 4000 })
  } catch (status) {
    toast.add({ severity: 'error', summary: status === 409 ? t('export.inProgress') : t('export.failed'), life: 5000 })
  } finally {
    exporting.value = false
  }
}

async function savePermissions() {
  busy.value = true
  try {
    grants.value = (await wikiApi.savePermissions(space.value.key, grants.value)).grants
    await ctx.reloadSpace()
    ctx.reloadTree()
    ok()
  } catch (e) {
    fail(e)
  } finally {
    busy.value = false
  }
}

function toggleArchive() {
  const archive = space.value.status === 'Active'
  confirm.require({
    header: t('common.confirmTitle'),
    message: archive ? t('wiki.settings.confirmArchive') : t('wiki.settings.confirmRestore'),
    acceptLabel: t('common.yes'),
    rejectLabel: t('common.no'),
    accept: async () => {
      try {
        await wikiApi.archiveSpace(space.value.key, ctx.etag.value, archive)
        await ctx.reloadSpace()
      } catch (e) {
        fail(e)
      }
    },
  })
}

function editTemplate(tpl: Template | null) {
  tplForm.value = tpl ? { id: tpl.id, name: tpl.name, description: tpl.description ?? '', body: tpl.body } : { id: null, name: '', description: '', body: null }
  tplOpen.value = true
}

async function saveTemplate() {
  const f = tplForm.value
  const body = { name: f.name, description: f.description || null, body: f.body ?? { schema: 'raavi.doc' as const, schemaVersion: 3, doc: { type: 'doc' as const, content: [{ type: 'paragraph' }] } } }
  try {
    if (f.id) {
      const current = await wikiApi.template(f.id)
      await wikiApi.updateTemplate(f.id, current.etag, body)
    } else {
      await wikiApi.createSpaceTemplate(space.value.key, body)
    }
    tplOpen.value = false
    templates.value = (await wikiApi.spaceTemplates(space.value.key)).filter((x) => x.spaceId !== null)
  } catch (e) {
    fail(e)
  }
}

async function deleteTemplate(tpl: Template) {
  try {
    await wikiApi.deleteTemplate(tpl.id)
    templates.value = templates.value.filter((x) => x.id !== tpl.id)
  } catch (e) {
    fail(e)
  }
}
</script>

<template>
  <section class="settings">
    <header class="head">
      <h1>{{ t('wiki.settings.title', { name: space.name }) }}</h1>
      <Button v-if="space.rights.canAdminister" outlined size="small" icon="pi pi-download" :label="t('spaceTransfer.export')" :loading="exporting" data-testid="export-space" @click="exportSpace" />
    </header>
    <Message v-if="!space.rights.canView" severity="warn" :closable="false">{{ t('wiki.space.adminOnly') }}</Message>
    <Tabs v-model:value="tab">
      <TabList>
        <Tab value="details">{{ t('wiki.settings.details') }}</Tab>
        <Tab value="permissions">{{ t('wiki.settings.permissions') }}</Tab>
        <Tab v-if="space.rights.canManageTemplates" value="templates">{{ t('wiki.settings.templates') }}</Tab>
        <Tab v-if="space.rights.canView" value="publishing">{{ t('wiki.settings.publishing') }}</Tab>
        <Tab v-if="space.rights.canView" value="trash">{{ t('wiki.settings.trash') }}</Tab>
      </TabList>
      <TabPanels>
        <TabPanel value="details">
          <form class="form" @submit.prevent="saveDetails">
            <label for="ss-name">{{ t('wiki.spaces.name') }}</label>
            <InputText id="ss-name" v-model="form.name" required maxlength="200" :disabled="space.status !== 'Active'" />
            <label for="ss-type">{{ t('wiki.spaces.type') }}</label>
            <Select v-model="form.type" input-id="ss-type" :options="typeOptions" option-label="label" option-value="value" :disabled="space.status !== 'Active'" />
            <label for="ss-desc">{{ t('wiki.spaces.description') }}</label>
            <Textarea id="ss-desc" v-model="form.description" rows="3" maxlength="2000" auto-resize :disabled="space.status !== 'Active'" />
            <div class="row">
              <Button type="submit" :label="t('common.save')" :loading="busy" :disabled="space.status !== 'Active'" />
              <Button
                type="button"
                :severity="space.status === 'Active' ? 'warn' : 'secondary'"
                outlined
                :icon="space.status === 'Active' ? 'pi pi-inbox' : 'pi pi-replay'"
                :label="space.status === 'Active' ? t('wiki.settings.archive') : t('wiki.settings.restore')"
                @click="toggleArchive"
              />
            </div>
          </form>
        </TabPanel>
        <TabPanel value="permissions">
          <p class="muted">{{ t('wiki.settings.permissionsHelp') }}</p>
          <GrantMatrix v-if="actions.length" v-model="grants" :actions="actions" id-prefix="sp" />
          <Button class="save" :label="t('common.save')" :loading="busy" data-testid="save-permissions" @click="savePermissions" />
        </TabPanel>
        <TabPanel v-if="space.rights.canManageTemplates" value="templates">
          <Button icon="pi pi-plus" :label="t('wiki.templates.create')" size="small" @click="editTemplate(null)" />
          <p v-if="templates.length === 0" class="muted">{{ t('wiki.templates.none') }}</p>
          <ul class="templates">
            <li v-for="tpl in templates" :key="tpl.id">
              <span class="grow">{{ tpl.name }}</span>
              <Button text size="small" :label="t('common.edit')" @click="editTemplate(tpl)" />
              <Button text size="small" severity="danger" :label="t('common.delete')" @click="deleteTemplate(tpl)" />
            </li>
          </ul>
        </TabPanel>
        <TabPanel v-if="space.rights.canView" value="publishing"><SpacePublishingPanel v-if="tab === 'publishing'" /></TabPanel>
        <TabPanel v-if="space.rights.canView" value="trash"><SpaceTrashPanel v-if="tab === 'trash'" /></TabPanel>
      </TabPanels>
    </Tabs>

    <Dialog v-model:visible="tplOpen" modal :header="tplForm.id ? t('wiki.templates.edit') : t('wiki.templates.create')" :style="{ inlineSize: 'min(56rem, 95vw)' }">
      <form class="form wide" @submit.prevent="saveTemplate">
        <label for="tpl-name">{{ t('wiki.templates.name') }}</label>
        <InputText id="tpl-name" v-model="tplForm.name" required maxlength="200" />
        <label for="tpl-desc">{{ t('wiki.spaces.description') }}</label>
        <InputText id="tpl-desc" v-model="tplForm.description" maxlength="1000" />
        <span>{{ t('wiki.edit.body') }}</span>
        <RaaviEditor v-if="tplOpen" v-model="tplForm.body" editable />
        <Button type="submit" :label="t('common.save')" />
      </form>
    </Dialog>
  </section>
</template>

<style scoped>
.settings { display: grid; gap: var(--raavi-space-3); max-inline-size: 64rem; }
h1 { margin: 0; font-size: 1.4rem; }
.head { display: flex; align-items: center; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; }
.form { display: grid; gap: var(--raavi-space-2); max-inline-size: 36rem; }
.form.wide { max-inline-size: none; }
.row { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; }
.muted { color: var(--raavi-text-muted); }
.save { margin-block-start: var(--raavi-space-3); }
.templates { list-style: none; padding: 0; margin: var(--raavi-space-3) 0 0; display: grid; gap: var(--raavi-space-1); }
.templates li { display: flex; align-items: center; gap: var(--raavi-space-2); }
.grow { flex: 1; }
</style>
