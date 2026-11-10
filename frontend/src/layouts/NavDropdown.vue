<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'

/** A header menu button that opens a list of links: closes on Escape, on a click outside and when the route changes. */
export interface NavItem {
  to: { name: string }
  label: string
  icon: string
}
const props = defineProps<{ items: NavItem[]; label: string; icon: string; toggleTestid?: string; listTestid?: string }>()
const route = useRoute()
const open = ref(false)
const root = ref<HTMLElement | null>(null)
const active = computed(() => props.items.some((i) => route?.name === i.to.name))

const onDocumentClick = (e: MouseEvent) => {
  if (open.value && root.value && !root.value.contains(e.target as Node)) open.value = false
}
function onKey(e: KeyboardEvent) {
  if (e.key === 'Escape' && open.value) {
    open.value = false
    ;(root.value?.querySelector('button') as HTMLElement | null)?.focus()
  }
}
watch(() => route?.fullPath, () => (open.value = false))
onMounted(() => document.addEventListener('click', onDocumentClick))
onBeforeUnmount(() => document.removeEventListener('click', onDocumentClick))
</script>

<template>
  <div ref="root" class="dropdown" @keydown="onKey">
    <button type="button" :class="['nav-link', 'toggle', { active }]" aria-haspopup="true" :aria-expanded="open" :data-testid="toggleTestid" @click="open = !open">
      <i :class="icon" aria-hidden="true" /> <span>{{ label }}</span> <i class="pi pi-angle-down" aria-hidden="true" />
    </button>
    <ul v-if="open" class="list" :aria-label="label" :data-testid="listTestid">
      <li v-for="item in items" :key="item.label">
        <RouterLink :to="item.to" class="nav-link" active-class="active"><i :class="item.icon" aria-hidden="true" /> <span>{{ item.label }}</span></RouterLink>
      </li>
    </ul>
  </div>
</template>

<style scoped>
.dropdown { position: relative; }
.toggle { border: 0; background: transparent; font: inherit; cursor: pointer; }
.list {
  position: absolute;
  inset-inline-start: 0;
  inset-block-start: 100%;
  z-index: 20;
  min-inline-size: 14rem;
  margin: 0;
  padding: var(--raavi-space-1);
  list-style: none;
  background: var(--raavi-surface);
  border: 1px solid var(--raavi-border);
  border-radius: var(--raavi-radius-sm);
  box-shadow: 0 6px 20px rgba(0, 0, 0, 0.12);
  display: grid;
  gap: 2px;
}
.list .nav-link { display: flex; }
.nav-link {
  display: inline-flex;
  align-items: center;
  gap: var(--raavi-space-1);
  padding: var(--raavi-space-1) var(--raavi-space-3);
  border-radius: var(--raavi-radius-sm);
  color: var(--raavi-text-muted);
  text-decoration: none;
}
.nav-link:hover, .nav-link.active { background: var(--p-primary-50, rgba(0, 0, 0, 0.04)); color: var(--p-primary-color); }
@media (max-width: 960px) {
  .dropdown { display: grid; }
  .list { position: static; box-shadow: none; border: 0; padding-inline-start: var(--raavi-space-4); }
  .nav-link { padding: 0.55rem var(--raavi-space-3); }
}
</style>
