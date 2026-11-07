<script setup lang="ts">
import { ref, watch } from 'vue'

import type { SuggestionEntry } from '../types'

/** Keyboard-driven list used by the slash menu and @mentions (rendered by Tiptap's suggestion plugin). */

const props = defineProps<{ items: SuggestionEntry[]; command: (item: SuggestionEntry) => void; emptyLabel: string; ariaLabel: string }>()
const active = ref(0)
watch(
  () => props.items,
  () => (active.value = 0),
)

function onKeyDown({ event }: { event: KeyboardEvent }): boolean {
  if (event.key === 'ArrowDown') {
    active.value = (active.value + 1) % Math.max(1, props.items.length)
    return true
  }
  if (event.key === 'ArrowUp') {
    active.value = (active.value - 1 + props.items.length) % Math.max(1, props.items.length)
    return true
  }
  if (event.key === 'Enter' || event.key === 'Tab') {
    const item = props.items[active.value]
    if (item) props.command(item)
    return true
  }
  return false
}

defineExpose({ onKeyDown })
</script>

<template>
  <div class="suggestions" role="listbox" :aria-label="ariaLabel">
    <button
      v-for="(item, i) in items"
      :key="item.key"
      type="button"
      role="option"
      :aria-selected="i === active"
      :class="{ active: i === active }"
      @mousedown.prevent="command(item)"
      @mousemove="active = i"
    >
      <i v-if="item.icon" :class="item.icon" aria-hidden="true" />
      <span>{{ item.label }}</span>
      <small v-if="item.detail">{{ item.detail }}</small>
    </button>
    <p v-if="items.length === 0" class="empty">{{ emptyLabel }}</p>
  </div>
</template>

<style scoped>
.suggestions { display: grid; min-inline-size: 14rem; max-block-size: 18rem; overflow: auto; padding: 4px; background: var(--raavi-surface, #fff); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); box-shadow: 0 6px 24px rgba(0, 0, 0, 0.12); }
button { display: flex; align-items: center; gap: var(--raavi-space-2); padding: 6px 8px; border: 0; background: none; text-align: start; border-radius: 4px; cursor: pointer; font: inherit; color: inherit; }
button.active { background: var(--p-primary-50, rgba(0, 0, 0, 0.06)); }
small { color: var(--raavi-text-muted); margin-inline-start: auto; }
.empty { margin: 4px 8px; color: var(--raavi-text-muted); }
</style>
