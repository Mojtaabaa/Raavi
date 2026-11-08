<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { WfAction, WfCondition } from '@/core/api/workflows'

/**
 * Read-only flow picture of a rule: trigger → conditions → actions, drawn with plain HTML so it follows the reading direction, scales with
 * text size and is announced as an ordered list to screen readers. It mirrors the form; editing happens in the form.
 */
const props = defineProps<{ trigger: string; staleDays: number | null; conditions: WfCondition[]; actions: WfAction[] }>()
const { t } = useI18n()

const triggerText = computed(() => (props.trigger === 'content.stale' ? t('workflows.triggers.content_stale', { n: props.staleDays ?? '…' }) : t(`workflows.triggers.${props.trigger.replace('.', '_')}`)))
const conditionText = (c: WfCondition) => `${t(`workflows.fields.${c.field}`)} ${t(`workflows.operators.${c.operator}`)} “${c.value}”`
const actionText = (a: WfAction) =>
  a.type === 'AddLabel' ? `${t('workflows.actionTypes.AddLabel')}: ${a.label ?? ''}` : `${t('workflows.actionTypes.Notify')} (${a.recipients === 'Actor' ? t('workflows.recipientsActor') : t('workflows.recipientsWatchers')}): ${a.message ?? ''}`
</script>

<template>
  <ol class="flow" :aria-label="t('workflows.flow')" data-testid="flow">
    <li class="step trigger" data-testid="flow-trigger">
      <span class="kind">{{ t('workflows.when') }}</span>
      <span dir="auto">{{ triggerText }}</span>
    </li>
    <li class="step cond" data-testid="flow-conditions">
      <span class="kind">{{ t('workflows.ifAll') }}</span>
      <span v-if="conditions.length === 0" class="muted">{{ t('workflows.always') }}</span>
      <ul v-else>
        <li v-for="(c, i) in conditions" :key="i" dir="auto" data-testid="flow-condition">{{ conditionText(c) }}</li>
      </ul>
    </li>
    <li class="step act" data-testid="flow-actions">
      <span class="kind">{{ t('workflows.then') }}</span>
      <ul>
        <li v-for="(a, i) in actions" :key="i" dir="auto" data-testid="flow-action">{{ actionText(a) }}</li>
      </ul>
    </li>
  </ol>
</template>

<style scoped>
.flow { list-style: none; margin: 0; padding: 0; display: grid; gap: 1.1rem; }
.step { position: relative; display: grid; gap: 0.25rem; padding: 0.6rem 0.8rem; border: 2px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); background: var(--raavi-surface, transparent); }
.step + .step::before { content: ''; position: absolute; inset-block-start: -1.1rem; inset-inline-start: 1.5rem; block-size: 1.1rem; border-inline-start: 2px solid var(--raavi-border); }
.step + .step::after { content: '▾'; position: absolute; inset-block-start: -1.05rem; inset-inline-start: calc(1.5rem - 0.4rem); font-size: 0.8rem; color: var(--raavi-text-muted); line-height: 1.1rem; }
.trigger { border-color: #3b82f6; }
.cond { border-color: #f59e0b; }
.act { border-color: #10b981; }
.kind { font-size: 0.75rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.04em; color: var(--raavi-text-muted); }
.step ul { margin: 0; padding-inline-start: 1.1rem; display: grid; gap: 0.15rem; }
.muted { color: var(--raavi-text-muted); }
</style>
