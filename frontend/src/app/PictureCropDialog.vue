<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import Dialog from 'primevue/dialog'
import Button from 'primevue/button'
import Slider from 'primevue/slider'

/**
 * Crop a picked picture to a square: drag to move, pinch / wheel / slider / + − to zoom, rotate and mirror, double-click to reset.
 * A round mask shows what an avatar will look like (the saved picture is still a square: the app rounds it when it shows it);
 * live previews show the result at the sizes it is used at. The result is a small square picture (default 256 px, WebP/JPEG, or PNG
 * with transparency for logos) made in the browser. Nothing is uploaded until "Use this picture".
 */
const props = withDefaults(defineProps<{ file: File | null; size?: number; transparent?: boolean; round?: boolean; title?: string }>(), { size: 256, transparent: false, round: false, title: undefined })
const emit = defineEmits<{ cancel: []; confirm: [blob: Blob] }>()

const { t } = useI18n()
const VIEW = 300
const MAX_ZOOM = 5
const canvas = ref<HTMLCanvasElement | null>(null)
const previews: HTMLCanvasElement[] = [] // plain (non-reactive) on purpose: canvases must not be wrapped in proxies
const original = shallowRef<ImageBitmap | null>(null) // shallow: a reactive proxy is not a valid drawing source
const source = shallowRef<HTMLCanvasElement | null>(null) // the picture after rotation and mirroring
const rotation = ref(0)
const mirrored = ref(false)
const zoom = ref(1)
const offset = ref({ x: 0, y: 0 })
const failed = ref(false)
const busy = ref(false)
const info = ref('')

const base = computed(() => (source.value ? VIEW / Math.min(source.value.width, source.value.height) : 1))
const scale = computed(() => base.value * zoom.value)
const zoomPercent = computed(() => Math.round(zoom.value * 100))

function clamp() {
  const s = source.value
  if (!s) return
  const x = Math.min(0, Math.max(VIEW - s.width * scale.value, offset.value.x))
  const y = Math.min(0, Math.max(VIEW - s.height * scale.value, offset.value.y))
  // only assign when something changed: this runs inside a watcher of the same value (a new object every time would loop for ever)
  if (x !== offset.value.x || y !== offset.value.y) offset.value = { x, y }
}

/** Centre of the frame in picture coordinates, kept fixed while zooming. */
function setZoom(next: number, anchor = { x: VIEW / 2, y: VIEW / 2 }) {
  const s = source.value
  if (!s) return
  const z = Math.min(MAX_ZOOM, Math.max(1, next))
  const before = scale.value
  const px = (anchor.x - offset.value.x) / before
  const py = (anchor.y - offset.value.y) / before
  zoom.value = z
  const after = base.value * z
  offset.value = { x: anchor.x - px * after, y: anchor.y - py * after }
  clamp()
}

function buildSource() {
  const b = original.value
  if (!b) return
  const quarter = rotation.value % 180 !== 0
  const c = document.createElement('canvas')
  c.width = quarter ? b.height : b.width
  c.height = quarter ? b.width : b.height
  const ctx = c.getContext('2d')
  if (!ctx) return
  ctx.translate(c.width / 2, c.height / 2)
  ctx.rotate((rotation.value * Math.PI) / 180)
  if (mirrored.value) ctx.scale(-1, 1)
  ctx.drawImage(b, -b.width / 2, -b.height / 2)
  source.value = c
}

function recentre() {
  const s = source.value
  if (!s) return
  zoom.value = 1
  offset.value = { x: (VIEW - s.width * base.value) / 2, y: (VIEW - s.height * base.value) / 2 }
}

function draw() {
  const c = canvas.value
  const s = source.value
  if (!c || !s) return
  const ctx = c.getContext('2d')
  if (!ctx) return
  ctx.clearRect(0, 0, VIEW, VIEW)
  ctx.fillStyle = props.transparent ? '#d9d9d9' : '#ffffff'
  ctx.fillRect(0, 0, VIEW, VIEW)
  if (props.transparent) {
    // checkerboard: transparent parts of a logo must be visible as such
    ctx.fillStyle = '#f2f2f2'
    for (let y = 0; y < VIEW; y += 20) for (let x = (y / 20) % 2 ? 0 : 20; x < VIEW; x += 40) ctx.fillRect(x, y, 20, 20)
  }
  ctx.imageSmoothingQuality = 'high'
  ctx.drawImage(s, offset.value.x, offset.value.y, s.width * scale.value, s.height * scale.value)
  // previews first (they must not contain the mask), then the mask on top
  for (const p of previews) {
    const pc = p?.getContext('2d')
    if (!pc) continue
    pc.clearRect(0, 0, p.width, p.height)
    pc.imageSmoothingQuality = 'high'
    pc.drawImage(c, 0, 0, VIEW, VIEW, 0, 0, p.width, p.height)
  }
  ctx.save()
  ctx.fillStyle = 'rgba(0,0,0,0.5)'
  ctx.beginPath()
  ctx.rect(0, 0, VIEW, VIEW)
  if (props.round) ctx.arc(VIEW / 2, VIEW / 2, VIEW / 2 - 2, 0, Math.PI * 2, true)
  ctx.fill('evenodd')
  ctx.restore()
  // thirds grid inside the frame, so the subject can be placed with intent
  ctx.strokeStyle = 'rgba(255,255,255,0.35)'
  ctx.lineWidth = 1
  ctx.beginPath()
  for (const f of [1 / 3, 2 / 3]) {
    ctx.moveTo(VIEW * f, 0)
    ctx.lineTo(VIEW * f, VIEW)
    ctx.moveTo(0, VIEW * f)
    ctx.lineTo(VIEW, VIEW * f)
  }
  ctx.stroke()
  if (props.round) {
    ctx.strokeStyle = '#fff'
    ctx.lineWidth = 2
    ctx.beginPath()
    ctx.arc(VIEW / 2, VIEW / 2, VIEW / 2 - 2, 0, Math.PI * 2)
    ctx.stroke()
  }
}

async function load(file: File | null) {
  original.value?.close()
  original.value = null
  source.value = null
  failed.value = false
  rotation.value = 0
  mirrored.value = false
  zoom.value = 1
  info.value = ''
  if (!file) return
  try {
    const b = await createImageBitmap(file)
    original.value = b
    info.value = `${b.width} × ${b.height} px · ${(file.size / 1024).toFixed(0)} KB`
    buildSource()
    recentre()
    await nextTick()
    setTimeout(draw, 0) // the dialog's canvas exists a moment after the file is set
  } catch (e) {
    console.warn('crop: could not read the picture', e)
    failed.value = true
  }
}

watch(() => props.file, load, { immediate: true })
watch([zoom, offset], () => {
  clamp()
  draw()
})
watch([rotation, mirrored], () => {
  buildSource()
  recentre()
  draw()
})
onBeforeUnmount(() => original.value?.close())

// ── gestures: one pointer drags, two pointers pinch ───────────────────────────────────────────
const pointers = new Map<number, { x: number; y: number }>()
let dragStart: { x: number; y: number; ox: number; oy: number } | null = null
let pinchStart: { distance: number; zoom: number } | null = null
const local = (e: PointerEvent) => {
  const r = (e.currentTarget as HTMLElement).getBoundingClientRect()
  return { x: ((e.clientX - r.left) / r.width) * VIEW, y: ((e.clientY - r.top) / r.height) * VIEW }
}

function down(e: PointerEvent) {
  ;(e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId)
  pointers.set(e.pointerId, local(e))
  if (pointers.size === 1) dragStart = { ...local(e), ox: offset.value.x, oy: offset.value.y }
  if (pointers.size === 2) {
    const [a, b] = [...pointers.values()]
    pinchStart = { distance: Math.hypot(a!.x - b!.x, a!.y - b!.y) || 1, zoom: zoom.value }
    dragStart = null
  }
}
function move(e: PointerEvent) {
  if (!pointers.has(e.pointerId)) return
  pointers.set(e.pointerId, local(e))
  if (pointers.size === 2 && pinchStart) {
    const [a, b] = [...pointers.values()]
    setZoom(pinchStart.zoom * (Math.hypot(a!.x - b!.x, a!.y - b!.y) / pinchStart.distance), { x: (a!.x + b!.x) / 2, y: (a!.y + b!.y) / 2 })
  } else if (dragStart) {
    const p = local(e)
    offset.value = { x: dragStart.ox + (p.x - dragStart.x), y: dragStart.oy + (p.y - dragStart.y) }
  }
}
function up(e: PointerEvent) {
  pointers.delete(e.pointerId)
  pinchStart = null
  dragStart = null
  if (pointers.size === 1) {
    const [p] = [...pointers.values()]
    dragStart = { ...p!, ox: offset.value.x, oy: offset.value.y }
  }
}
function wheel(e: WheelEvent) {
  const r = (e.currentTarget as HTMLElement).getBoundingClientRect()
  setZoom(zoom.value * (e.deltaY < 0 ? 1.1 : 1 / 1.1), { x: ((e.clientX - r.left) / r.width) * VIEW, y: ((e.clientY - r.top) / r.height) * VIEW })
}
function key(e: KeyboardEvent) {
  const step = e.shiftKey ? 30 : 10
  const moves: Record<string, [number, number]> = { ArrowLeft: [step, 0], ArrowRight: [-step, 0], ArrowUp: [0, step], ArrowDown: [0, -step] }
  const m = moves[e.key]
  if (m) {
    e.preventDefault()
    offset.value = { x: offset.value.x + m[0], y: offset.value.y + m[1] }
  } else if (e.key === '+' || e.key === '=') setZoom(zoom.value * 1.1)
  else if (e.key === '-') setZoom(zoom.value / 1.1)
  else if (e.key === 'r' || e.key === 'R') rotate(e.shiftKey ? -90 : 90)
  else if (e.key === '0') recentre()
}
const rotate = (by: number) => (rotation.value = (rotation.value + by + 360) % 360)
const reset = () => {
  rotation.value = 0
  mirrored.value = false
  buildSource()
  recentre()
  draw()
}

async function confirm() {
  const s = source.value
  if (!s || busy.value) return
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
    ctx.drawImage(s, offset.value.x * k, offset.value.y * k, s.width * scale.value * k, s.height * scale.value * k)
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
  <Dialog :visible="!!file" modal :header="title ?? t('crop.title')" :style="{ inlineSize: 'min(34rem, 96vw)' }" @update:visible="(v: boolean) => !v && emit('cancel')">
    <div class="crop">
      <p v-if="failed" class="error" role="alert">{{ t('crop.failed') }}</p>
      <template v-else>
        <div class="main">
          <div class="stage">
            <canvas
              ref="canvas"
              :width="VIEW"
              :height="VIEW"
              tabindex="0"
              role="application"
              :aria-label="t('crop.hint')"
              data-testid="crop-canvas"
              @pointerdown="down"
              @pointermove="move"
              @pointerup="up"
              @pointercancel="up"
              @wheel.prevent="wheel"
              @dblclick="recentre"
              @keydown="key"
            />
          </div>
          <aside class="side">
            <h3>{{ t('crop.preview') }}</h3>
            <div class="previews" aria-hidden="true">
              <canvas v-for="px in [96, 48, 28]" :key="px" :ref="(el) => { if (el) previews[[96, 48, 28].indexOf(px)] = el as HTMLCanvasElement }" :width="px" :height="px" :class="['pv', { round }]" />
            </div>
            <p class="muted small">{{ info }}</p>
            <p class="muted small">{{ t('crop.result', { size }) }}</p>
          </aside>
        </div>

        <div class="tools" role="toolbar" :aria-label="t('crop.tools')">
          <Button text rounded size="small" icon="pi pi-search-minus" :aria-label="t('crop.zoomOut')" :disabled="zoom <= 1" @click="setZoom(zoom / 1.2)" />
          <Slider v-model="zoom" :min="1" :max="MAX_ZOOM" :step="0.02" class="slider" :aria-label="t('crop.zoom')" />
          <Button text rounded size="small" icon="pi pi-search-plus" :aria-label="t('crop.zoomIn')" :disabled="zoom >= MAX_ZOOM" @click="setZoom(zoom * 1.2)" />
          <span class="pct" aria-live="polite">{{ zoomPercent }}%</span>
          <span class="sep" />
          <Button text rounded size="small" icon="pi pi-replay" class="flip" :aria-label="t('crop.rotateLeft')" :title="t('crop.rotateLeft')" @click="rotate(-90)" />
          <Button text rounded size="small" icon="pi pi-refresh" :aria-label="t('crop.rotateRight')" :title="t('crop.rotateRight')" @click="rotate(90)" />
          <Button text rounded size="small" icon="pi pi-arrows-h" :class="{ on: mirrored }" :aria-pressed="mirrored" :aria-label="t('crop.mirror')" :title="t('crop.mirror')" @click="mirrored = !mirrored" />
          <Button text rounded size="small" icon="pi pi-undo" :aria-label="t('crop.reset')" :title="t('crop.reset')" @click="reset" />
        </div>
        <small class="muted hint">{{ t('crop.hint') }}</small>
      </template>
    </div>
    <template #footer>
      <Button text :label="t('common.cancel')" @click="emit('cancel')" />
      <Button :label="t('crop.use')" icon="pi pi-check" :disabled="!source || failed" :loading="busy" data-testid="crop-confirm" @click="confirm" />
    </template>
  </Dialog>
</template>

<style scoped>
.crop { display: grid; gap: var(--raavi-space-3); }
.main { display: flex; gap: var(--raavi-space-4); align-items: flex-start; flex-wrap: wrap; justify-content: center; }
.stage { position: relative; inline-size: min(300px, 100%); aspect-ratio: 1; touch-action: none; border-radius: var(--raavi-radius-md); overflow: hidden; background: var(--raavi-bg); box-shadow: var(--raavi-shadow-1); }
.stage canvas { display: block; inline-size: 100%; block-size: 100%; cursor: grab; touch-action: none; }
.stage canvas:active { cursor: grabbing; }
.stage canvas:focus-visible { outline: 3px solid var(--p-primary-color); outline-offset: -3px; }
.side { display: grid; gap: var(--raavi-space-2); align-content: start; min-inline-size: 8rem; flex: 1; }
.side h3 { margin: 0; font-size: 0.85rem; color: var(--raavi-text-muted); font-weight: 600; }
.previews { display: flex; gap: var(--raavi-space-3); align-items: flex-end; }
.pv { border: 1px solid var(--raavi-border); background: #fff; }
.pv.round { border-radius: 50%; }
.tools { display: flex; align-items: center; gap: var(--raavi-space-1); flex-wrap: wrap; }
.slider { flex: 1; min-inline-size: 6rem; margin-inline: var(--raavi-space-2); }
.pct { min-inline-size: 3rem; font-size: 0.82rem; color: var(--raavi-text-muted); font-variant-numeric: tabular-nums; }
.sep { inline-size: 1px; block-size: 1.4rem; background: var(--raavi-border); margin-inline: var(--raavi-space-1); }
.tools :deep(.on) { background: var(--p-primary-100, rgba(0, 0, 0, 0.08)); }
.hint { text-align: center; }
.muted { color: var(--raavi-text-muted); margin: 0; }
.small { font-size: 0.8rem; }
.error { color: var(--raavi-danger); }
</style>
