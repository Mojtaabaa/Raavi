<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { avatarUrl } from '@/core/api/avatars'
import { useAvatarStore } from '@/stores/avatars'
import { useHostStore } from '@/stores/host'

/**
 * A user's picture, or their initials on a colour derived from the name when there is none (or it cannot be shown).
 * Pictures are looked up in batches (stores/avatars.ts) and exist only inside a workspace.
 */
const props = withDefaults(defineProps<{ userId?: string | null; name?: string | null; size?: number }>(), { userId: null, name: null, size: 28 })

const store = useAvatarStore()
const host = useHostStore()
const failed = ref(false)

const inWorkspace = computed(() => host.context?.kind === 'tenant')
watch(
  () => [props.userId, inWorkspace.value] as const,
  ([id, ok]) => {
    failed.value = false
    if (ok) store.want(id)
  },
  { immediate: true },
)

const version = computed(() => (props.userId && inWorkspace.value ? store.versions[props.userId] : null))
const src = computed(() => (props.userId && version.value && !failed.value ? avatarUrl(props.userId, version.value) : null))

const initials = computed(() => {
  const words = (props.name ?? '').trim().split(/\s+/).filter(Boolean)
  if (words.length === 0) return '?'
  const first = [...words[0]!][0] ?? '?'
  const second = words.length > 1 ? ([...words[words.length - 1]!][0] ?? '') : ''
  return (first + second).toLocaleUpperCase()
})

const hue = computed(() => {
  const seed = props.userId ?? props.name ?? ''
  let h = 0
  for (const ch of seed) h = (h * 31 + ch.codePointAt(0)!) % 360
  return h
})
</script>

<template>
  <span class="avatar" :style="{ '--size': `${size}px`, '--hue': String(hue) }" :title="name ?? undefined" data-testid="avatar">
    <img v-if="src" :src="src" alt="" :width="size" :height="size" loading="lazy" decoding="async" @error="failed = true" />
    <!-- the letters come from CSS (::before), so they are decoration only: not part of the text a reader or a test sees next to a name -->
    <span v-else class="initials" :data-initials="initials" aria-hidden="true"></span>
  </span>
</template>

<style scoped>
.avatar {
  display: inline-grid;
  place-items: center;
  flex: none;
  inline-size: var(--size);
  block-size: var(--size);
  border-radius: 50%;
  overflow: hidden;
  vertical-align: middle;
  background: hsl(var(--hue) 55% 88%);
  color: hsl(var(--hue) 55% 25%);
  font-size: calc(var(--size) * 0.4);
  font-weight: 700;
  line-height: 1;
  user-select: none;
}
@media (prefers-color-scheme: dark) {
  .avatar { background: hsl(var(--hue) 35% 28%); color: hsl(var(--hue) 60% 90%); }
}
.initials::before { content: attr(data-initials); }
img { inline-size: 100%; block-size: 100%; object-fit: cover; display: block; }
</style>
