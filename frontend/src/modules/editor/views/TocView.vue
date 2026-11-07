<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { NodeViewWrapper, nodeViewProps } from '@tiptap/vue-3'
import { useI18n } from 'vue-i18n'

/** Table of contents computed from the live document; entries scroll to the n-th heading. */
const props = defineProps(nodeViewProps)
const { t } = useI18n()
const items = ref<{ level: number; text: string; index: number }[]>([])

function collect() {
  const list: { level: number; text: string; index: number }[] = []
  let index = 0
  props.editor.state.doc.descendants((n) => {
    if (n.type.name === 'heading') {
      const level = Number(n.attrs.level)
      if (level <= Number(props.node.attrs.maxLevel)) list.push({ level, text: n.textContent, index })
      index++
    }
    return true
  })
  items.value = list
}

function go(index: number) {
  const headings = props.editor.view.dom.querySelectorAll('h1, h2, h3, h4, h5, h6')
  headings[index]?.scrollIntoView({ behavior: 'smooth', block: 'start' })
}

onMounted(() => {
  collect()
  props.editor.on('update', collect)
})
onBeforeUnmount(() => props.editor.off('update', collect))
</script>

<template>
  <NodeViewWrapper class="raavi-toc" contenteditable="false">
    <nav :aria-label="t('editor.toc.title')">
      <strong>{{ t('editor.toc.title') }}</strong>
      <p v-if="items.length === 0" class="muted">{{ t('editor.toc.empty') }}</p>
      <ul>
        <li v-for="h in items" :key="h.index" :style="{ paddingInlineStart: `${(h.level - 1) * 1}rem` }">
          <a href="#" dir="auto" @click.prevent="go(h.index)">{{ h.text }}</a>
        </li>
      </ul>
      <label v-if="props.editor.isEditable" class="level">
        {{ t('editor.toc.maxLevel') }}
        <select :value="node.attrs.maxLevel" @change="(e) => props.updateAttributes({ maxLevel: Number((e.target as HTMLSelectElement).value) })">
          <option v-for="l in 6" :key="l" :value="l">{{ l }}</option>
        </select>
      </label>
    </nav>
  </NodeViewWrapper>
</template>

<style scoped>
.raavi-toc { border-inline-start: 3px solid var(--raavi-border); padding-inline-start: var(--raavi-space-3); margin-block: var(--raavi-space-3); }
ul { list-style: none; padding: 0; margin: var(--raavi-space-1) 0; }
.muted { color: var(--raavi-text-muted); margin: var(--raavi-space-1) 0; }
.level { font-size: 0.8rem; color: var(--raavi-text-muted); }
</style>
