<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import Dialog from 'primevue/dialog'

/** Keyboard shortcuts of the whole application (opened with ?). One place, so the list cannot drift from what is documented. */
const visible = defineModel<boolean>('visible', { required: true })
const { t } = useI18n()

const groups: { title: string; rows: [string[], string][] }[] = [
  { title: 'shortcuts.general', rows: [[['?'], 'shortcuts.help'], [['Ctrl', 'K'], 'shortcuts.quickFind'], [['/'], 'shortcuts.smartSearch'], [['Esc'], 'shortcuts.close']] },
  { title: 'shortcuts.editor', rows: [[['Ctrl', 'B'], 'shortcuts.bold'], [['Ctrl', 'I'], 'shortcuts.italic'], [['/'], 'shortcuts.slash'], [['Ctrl', 'Z'], 'shortcuts.undo']] },
  { title: 'shortcuts.board', rows: [[['V', 'H', 'R', 'E'], 'shortcuts.tools'], [['Ctrl', 'C / V'], 'shortcuts.copyPaste'], [['Tab'], 'shortcuts.nextElement'], [['+', '−', '0'], 'shortcuts.zoom']] },
]
</script>

<template>
  <Dialog v-model:visible="visible" modal :header="t('shortcuts.title')" :style="{ inlineSize: 'min(34rem, 95vw)' }" data-testid="shortcuts-dialog">
    <div class="groups">
      <section v-for="g in groups" :key="g.title">
        <h3>{{ t(g.title) }}</h3>
        <dl>
          <template v-for="(row, i) in g.rows" :key="i">
            <dt><kbd v-for="k in row[0]" :key="k">{{ k }}</kbd></dt>
            <dd>{{ t(row[1]) }}</dd>
          </template>
        </dl>
      </section>
    </div>
  </Dialog>
</template>

<style scoped>
.groups { display: grid; gap: var(--raavi-space-4); }
h3 { margin: 0 0 var(--raavi-space-2); font-size: 0.85rem; color: var(--raavi-text-muted); }
dl { display: grid; grid-template-columns: max-content 1fr; gap: 6px var(--raavi-space-4); margin: 0; align-items: center; }
dd { margin: 0; }
dt { display: inline-flex; gap: 4px; direction: ltr; }
kbd { font: inherit; font-size: 0.8rem; padding: 1px 7px; border: 1px solid var(--raavi-border); border-bottom-width: 2px; border-radius: 5px; background: var(--raavi-bg); }
</style>
