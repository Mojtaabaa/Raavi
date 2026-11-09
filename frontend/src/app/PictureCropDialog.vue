<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import Dialog from 'primevue/dialog'
import Button from 'primevue/button'
import Slider from 'primevue/slider'

/**
 * Square crop of a picked picture: drag to move, slider (or mouse wheel) to zoom; the result is a small square picture
 * (default 256 px, WebP/JPEG, or PNG with transparency for logos) made in the browser. The server accepts only such pictures.
 */
const props = withDefaults(defineProps<{ file: File | null; size?: number; transparent?: boolean; title?: string }>(), { size: 256, transparent: false, title: undefined })
const emit = defineEmits<{ cancel: []; confirm: [blob: Blob] }>()

const { t } = useI18n()
const VIEW = 280
const canvas = ref<HTMLCanvasElement | null>(null)
const bitmap = ref<ImageBitmap | null>(null)
const zoom = ref(1)
const offset = ref({ x: 0, y: 0 })
const failed = ref(false)
const busy = ref(false)
let drag: { x: number; y: number; ox: number; oy: number } | null = null

const base = computed(() => (bitmap.value ? VIEW / Math.min(bitmap.value.width, bitmap.value.height) : 1))
const scale = computed(() => base.value * zoom.value)

function clamp() {
  const b = bitmap.value
  if (!b) return
  const w = b.width * scale.value
  const h = b.height * scale.value
  offset.value = { x: Math.min(0, Math.max(VIEW - w, offset.value.x)), y: Math.min(0, Math.max(VIEW - h, offset.value.y)) }
}

function draw() {
  const c = canvas.value
  const b = bitmap.value
  if (!c || !b) return
  const ctx = c.getContext('2d')
  if (!ctx) return
  ctx.clearRect(0, 0, VIEW, VIEW)
  ctx.fillStyle = props.transparent ? 'rgba(128,128,128,0.15)' : '#ffffff'
  ctx.fillRect(0, 0, VIEW, VIEW)
  ctx.imageSmoothingQuality = 'high'
  ctx.drawImage(b, offset.value.x, offset.value.y, b.width * scale.value, b.height * scale.value)
}

async function load(file: File | null) {
  bitmap.value?.close()
  bitmap.value = null
  failed.value = false
  zoom.value = 1
  if (!file) return
  try {
    const b = await createImageBitmap(file)
    bitmap.value = b
    const s = VIEW / Math.min(b.width, b.height)
    offset.value = { x: (VIEW - b.width * s) / 2, y: (VIEW - b.height * s) / 2 }
    // wait for the canvas of the dialog to exist
    setTimeout(draw, 0)
  } catch {
    failed.value = true
  }
}

watch(() => props.file, load, { immediate: true })
watch([zoom, offset], () => {
  clamp()
  draw()
})
onBeforeUnmount(() => bitmap.value?.close())

function down(e: PointerEvent) {
  drag = { x: e.clientX, y: e.clientY, ox: offset.value.x, oy: offset.value.y }
  ;(e.target as HTMLElement).setPointerCapture?.(e.pointerId)
}
function move(e: PointerEvent) {
  if (!drag) return
  offset.value = { x: drag.ox + (e.clientX - drag.x), y: drag.oy + (e.clientY - drag.y) }
}
function up() {
  drag = null
}
function wheel(e: WheelEvent) {
  zoom.value = Math.min(4, Math.max(1, zoom.value + (e.deltaY < 0 ? 0.1 : -0.1)))
}
function key(e: KeyboardEvent) {
  const step = 12
  const moves: Record<string, [number, number]> = { ArrowLeft: [step, 0], ArrowRight: [-step, 0], ArrowUp: [0, step], ArrowDown: [0, -step] }
  const m = moves[e.key]
  if (m) {
    e.preventDefault()
    offset.value = { x: offset.value.x + m[0], y: offset.value.y + m[1] }
  } else if (e.key === '+' || e.key === '=') zoom.value = Math.min(4, zoom.value + 0.1)
  else if (e.key === '-') zoom.value = Math.max(1, zoom.value - 0.1)
}

async function confirm() {
  const b = bitmap.value
  if (!b || busy.value) return
  busy.value = true
  try {
    const out = document.createElement('canvas')
    out.width = props.size
    out.height = props.size
    const ctx = out.getContext('2d')
    if (!ctx) throw new Error('canvas unavailable')
    if (!props.transparent) {
      ctx.fillStyle = '#ffffff'
      ctx.fillRect(0, 0, props.size, props.size)
    }
    ctx.imageSmoothingQuality = 'high'
    const k = props.size / VIEW
    ctx.drawImage(b, offset.value.x * k, offset.value.y * k, b.width * scale.value * k, b.height * scale.value * k)
    const encode = (type: string, quality: number) => new Promise<Blob | null>((resolve) => out.toBlob(resolve, type, quality))
    let blob: Blob | null
    if (props.transparent) blob = await encode('image/png', 1)
    else {
      const webp = await encode('image/webp', 0.9)
      blob = webp && webp.type === 'image/webp' ? webp : await encode('image/jpeg', 0.9)
    }
    if (!blob) throw new Error('encode failed')
    emit('confirm', blob)
  } catch {
    failed.value = true
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <Dialog :visible="!!file" modal :header="title ?? t('crop.title')" :style="{ inlineSize: 'min(24rem, 96vw)' }" @update:visible="(v: boolean) => !v && emit('cancel')">
    <div class="crop">
      <p v-if="failed" class="error" role="alert">{{ t('crop.failed') }}</p>
      <template v-else>
        <div class="stage" :style="{ inlineSize: `${VIEW}px`, blockSize: `${VIEW}px` }">
          <canvas
            ref="canvas"
            :width="VIEW"
            :height="VIEW"
            tabindex="0"
            role="img"
            :aria-label="t('crop.hint')"
            data-testid="crop-canvas"
            @pointerdown="down"
            @pointermove="move"
            @pointerup="up"
            @pointercancel="up"
            @wheel.prevent="wheel"
            @keydown="key"
          />
          <span class="frame" aria-hidden="true"></span>
        </div>
        <small class="muted">{{ t('crop.hint') }}</small>
        <label class="zoom">
          <i class="pi pi-search-minus" aria-hidden="true" />
          <Slider v-model="zoom" :min="1" :max="4" :step="0.05" class="slider" :aria-label="t('crop.zoom')" />
          <i class="pi pi-search-plus" aria-hidden="true" />
        </label>
      </template>
    </div>
    <template #footer>
      <Button text :label="t('common.cancel')" @click="emit('cancel')" />
      <Button :label="t('crop.use')" icon="pi pi-check" :disabled="!bitmap || failed" :loading="busy" data-testid="crop-confirm" @click="confirm" />
    </template>
  </Dialog>
</template>

<style scoped>
.crop { display: grid; gap: var(--raavi-space-2); justify-items: center; }
.stage { position: relative; max-inline-size: 100%; touch-action: none; border-radius: var(--raavi-radius-sm); overflow: hidden; background: var(--raavi-bg); }
canvas { display: block; cursor: grab; max-inline-size: 100%; }
canvas:active { cursor: grabbing; }
.frame { position: absolute; inset: 0; pointer-events: none; box-shadow: inset 0 0 0 2px var(--p-primary-color); border-radius: inherit; }
.zoom { display: flex; align-items: center; gap: var(--raavi-space-2); inline-size: 100%; }
.slider { flex: 1; }
.muted { color: var(--raavi-text-muted); }
.error { color: var(--raavi-danger); }
</style>
