<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Select from 'primevue/select'
import type { Grant, PermissionAction, SubjectType } from '@/core/api/wiki'
import PrincipalPicker from './PrincipalPicker.vue'

/**
 * Editable grant set: one row per principal, one column per action (Allow). Deny rules are listed separately; deny
 * always wins over allow (ADR-012).
 */
const grants = defineModel<Grant[]>({ required: true })
const props = defineProps<{ actions: PermissionAction[]; idPrefix: string; groupsOnly?: boolean }>()
const { t } = useI18n()

interface Row {
  subjectType: SubjectType
  subjectId: string | null
  name: string
}
const extraRows = ref<Row[]>([])
const rowKey = (r: { subjectType: SubjectType; subjectId: string | null }) => `${r.subjectType}:${r.subjectId ?? '*'}`

const rows = computed<Row[]>(() => {
  const map = new Map<string, Row>()
  for (const g of grants.value.filter((x) => x.effect === 'Allow')) {
    map.set(rowKey(g), { subjectType: g.subjectType, subjectId: g.subjectId, name: subjectName(g) })
  }
  for (const r of extraRows.value) if (!map.has(rowKey(r))) map.set(rowKey(r), r)
  return [...map.values()]
})
const denies = computed(() => grants.value.filter((g) => g.effect === 'Deny'))

function subjectName(g: { subjectType: SubjectType; subjectName?: string | null; subjectId: string | null }) {
  return g.subjectType === 'AllMembers' ? t('wiki.permissions.allMembers') : (g.subjectName ?? g.subjectId ?? '')
}

function has(row: Row, action: PermissionAction) {
  return grants.value.some((g) => g.effect === 'Allow' && rowKey(g) === rowKey(row) && g.action === action)
}

function toggle(row: Row, action: PermissionAction, on: boolean) {
  const others = grants.value.filter((g) => !(g.effect === 'Allow' && rowKey(g) === rowKey(row) && g.action === action))
  grants.value = on ? [...others, { subjectType: row.subjectType, subjectId: row.subjectId, subjectName: row.name, action, effect: 'Allow' }] : others
}

function removeRow(row: Row) {
  grants.value = grants.value.filter((g) => !(g.effect === 'Allow' && rowKey(g) === rowKey(row)))
  extraRows.value = extraRows.value.filter((r) => rowKey(r) !== rowKey(row))
}

function addRow(s: { subjectType: SubjectType; subjectId: string | null; name: string }) {
  if (rows.value.some((r) => rowKey(r) === rowKey(s))) return
  extraRows.value = [...extraRows.value, { subjectType: s.subjectType, subjectId: s.subjectId, name: s.name }]
  toggle({ ...s }, props.actions[0]!, true)
}

const denyAction = ref<PermissionAction>(props.actions[0]!)
const actionOptions = computed(() => props.actions.map((a) => ({ value: a, label: t(`wiki.actions.${a}`) })))
function addDeny(s: { subjectType: SubjectType; subjectId: string | null; name: string }) {
  if (grants.value.some((g) => g.effect === 'Deny' && rowKey(g) === rowKey(s) && g.action === denyAction.value)) return
  grants.value = [...grants.value, { subjectType: s.subjectType, subjectId: s.subjectId, subjectName: s.name, action: denyAction.value, effect: 'Deny' }]
}
function removeDeny(g: Grant) {
  grants.value = grants.value.filter((x) => x !== g)
}
</script>

<template>
  <div class="matrix">
    <div class="scroll">
      <table>
        <thead>
          <tr>
            <th scope="col">{{ t('wiki.permissions.principal') }}</th>
            <th v-for="a in actions" :key="a" scope="col">{{ t(`wiki.actions.${a}`) }}</th>
            <th scope="col"><span class="sr-only">{{ t('common.actions') }}</span></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="r in rows" :key="rowKey(r)">
            <th scope="row">
              <i :class="r.subjectType === 'Group' ? 'pi pi-users' : r.subjectType === 'AllMembers' ? 'pi pi-globe' : 'pi pi-user'" aria-hidden="true" />
              {{ r.name }}
            </th>
            <td v-for="a in actions" :key="a">
              <Checkbox
                :model-value="has(r, a)"
                binary
                :input-id="`${idPrefix}-${rowKey(r)}-${a}`"
                :aria-label="`${r.name}: ${t(`wiki.actions.${a}`)}`"
                @update:model-value="(v: boolean) => toggle(r, a, v)"
              />
            </td>
            <td><Button text rounded size="small" severity="danger" icon="pi pi-trash" :aria-label="t('common.delete')" @click="removeRow(r)" /></td>
          </tr>
        </tbody>
      </table>
    </div>
    <label :for="`${idPrefix}-add`">{{ t('wiki.permissions.addPrincipal') }}</label>
    <PrincipalPicker :input-id="`${idPrefix}-add`" allow-all-members :groups-only="groupsOnly" @pick="addRow" />

    <details class="deny">
      <summary>{{ t('wiki.permissions.denyRules', { n: denies.length }) }}</summary>
      <p class="muted">{{ t('wiki.permissions.denyHelp') }}</p>
      <ul>
        <li v-for="g in denies" :key="rowKey(g) + g.action">
          <span>{{ subjectName(g) }} — {{ t(`wiki.actions.${g.action}`) }}</span>
          <Button text rounded size="small" icon="pi pi-times" :aria-label="t('common.delete')" @click="removeDeny(g)" />
        </li>
      </ul>
      <div class="deny-add">
        <label :for="`${idPrefix}-deny-action`" class="sr-only">{{ t('wiki.permissions.action') }}</label>
        <Select v-model="denyAction" :input-id="`${idPrefix}-deny-action`" :options="actionOptions" option-label="label" option-value="value" />
        <label :for="`${idPrefix}-deny`" class="sr-only">{{ t('wiki.permissions.addPrincipal') }}</label>
        <PrincipalPicker :input-id="`${idPrefix}-deny`" allow-all-members :groups-only="groupsOnly" @pick="addDeny" />
      </div>
    </details>
  </div>
</template>

<style scoped>
.matrix { display: grid; gap: var(--raavi-space-2); }
.scroll { overflow-x: auto; }
table { border-collapse: collapse; inline-size: 100%; }
th, td { padding: var(--raavi-space-1) var(--raavi-space-2); border-block-end: 1px solid var(--raavi-border); text-align: center; }
th[scope='row'], thead th:first-child { text-align: start; white-space: nowrap; }
thead th { font-size: 0.8rem; color: var(--raavi-text-muted); font-weight: 600; }
.deny ul { list-style: none; padding: 0; margin: 0 0 var(--raavi-space-2); }
.deny li { display: flex; align-items: center; gap: var(--raavi-space-2); }
.deny-add { display: grid; grid-template-columns: 12rem minmax(0, 1fr); gap: var(--raavi-space-2); }
.muted { color: var(--raavi-text-muted); }
.sr-only { position: absolute; inline-size: 1px; block-size: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
</style>
