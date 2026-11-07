<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { NodeViewWrapper, nodeViewProps } from '@tiptap/vue-3'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { contentRoute, wikiApi, type TreeNode } from '@/core/api/wiki'

/**
 * "Child pages" list (Confluence `children` macro). Computed when the page is viewed from the page tree, so it only ever lists
 * pages the viewer may see and is always current. Outside a page (templates, comments) it shows a placeholder.
 */
interface Branch extends TreeNode {
  children: Branch[]
}
const props = defineProps(nodeViewProps)
const { t } = useI18n()
const route = useRoute()
const tree = ref<Branch[]>([])
const state = ref<'idle' | 'loading' | 'ready' | 'error'>('idle')

const pageId = () => (typeof route.params.id === 'string' ? route.params.id : null)
const spaceKey = () => (typeof route.params.key === 'string' ? route.params.key : null)

async function branch(key: string, parentId: string, depth: number): Promise<Branch[]> {
  const nodes = await wikiApi.tree(key, parentId)
  return Promise.all(nodes.map(async (n) => ({ ...n, children: depth > 1 && n.hasChildren ? await branch(key, n.id, depth - 1) : [] })))
}

async function load() {
  const id = pageId()
  const key = spaceKey()
  if (!id || !key) {
    state.value = 'idle'
    return
  }
  state.value = 'loading'
  try {
    tree.value = await branch(key, id, Math.min(5, Math.max(1, Number(props.node.attrs.depth) || 1)))
    state.value = 'ready'
  } catch {
    state.value = 'error'
  }
}
onMounted(load)
watch(() => [route.params.id, props.node.attrs.depth], load)

const target = (n: TreeNode) => contentRoute({ id: n.id, slug: n.slug, spaceKey: spaceKey() ?? '' })
</script>

<script lang="ts">
export default { name: 'ChildrenView' }
</script>

<template>
  <NodeViewWrapper class="raavi-children" contenteditable="false" data-testid="children-list">
    <nav :aria-label="t('editor.children.title')">
      <strong>{{ t('editor.children.title') }}</strong>
      <p v-if="state === 'idle'" class="muted">{{ t('editor.children.placeholder') }}</p>
      <p v-else-if="state === 'error'" class="muted">{{ t('editor.children.error') }}</p>
      <p v-else-if="state === 'ready' && tree.length === 0" class="muted">{{ t('editor.children.empty') }}</p>
      <ul v-if="state === 'ready' && tree.length">
        <template v-for="a in tree" :key="a.id">
          <li><RouterLink :to="target(a)" dir="auto">{{ a.title }}</RouterLink>
            <ul v-if="a.children.length">
              <li v-for="b in a.children" :key="b.id">
                <RouterLink :to="target(b)" dir="auto">{{ b.title }}</RouterLink>
                <ul v-if="b.children.length">
                  <li v-for="c in b.children" :key="c.id">
                    <RouterLink :to="target(c)" dir="auto">{{ c.title }}</RouterLink>
                    <ul v-if="c.children.length">
                      <li v-for="d in c.children" :key="d.id"><RouterLink :to="target(d)" dir="auto">{{ d.title }}</RouterLink></li>
                    </ul>
                  </li>
                </ul>
              </li>
            </ul>
          </li>
        </template>
      </ul>
      <label v-if="props.editor.isEditable" class="opt">
        {{ t('editor.children.depth') }}
        <select :value="node.attrs.depth" @change="(e) => props.updateAttributes({ depth: Number((e.target as HTMLSelectElement).value) })">
          <option v-for="l in 5" :key="l" :value="l">{{ l }}</option>
        </select>
      </label>
    </nav>
  </NodeViewWrapper>
</template>

<style scoped>
.raavi-children { border-inline-start: 3px solid var(--raavi-border); padding-inline-start: var(--raavi-space-3); margin-block: var(--raavi-space-3); }
ul { margin: var(--raavi-space-1) 0; padding-inline-start: 1.2rem; }
.muted { color: var(--raavi-text-muted); margin: var(--raavi-space-1) 0; }
.opt { font-size: 0.8rem; color: var(--raavi-text-muted); }
</style>
