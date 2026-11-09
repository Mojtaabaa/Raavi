<script setup lang="ts">
import { ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import Tree from 'primevue/tree'
import type { TreeNode as PrimeTreeNode } from 'primevue/treenode'
import { wikiApi, type TreeNode } from '@/core/api/wiki'

/**
 * Lazily loaded page tree of one space. Children are fetched on expand (the API only returns what the caller may view);
 * `expandPath` opens the ancestors of the current page.
 */
const props = defineProps<{
  spaceKey: string
  selectedId?: string | null
  expandPath?: string[]
  /** Bump to reload from the root. */
  version?: number
  /** Ids that cannot be selected (e.g. the page being moved). */
  disabledIds?: string[]
}>()
const emit = defineEmits<{ select: [node: TreeNode] }>()
const { t } = useI18n()

const nodes = ref<PrimeTreeNode[]>([])
const loading = ref(false)
const failed = ref(false)
const expandedKeys = ref<Record<string, boolean>>({})
const selectionKeys = ref<Record<string, boolean>>({})

function toNode(n: TreeNode): PrimeTreeNode {
  return {
    key: n.id,
    label: n.title,
    leaf: !n.hasChildren,
    icon: n.contentType === 'folder' ? 'pi pi-folder' : 'pi pi-file',
    selectable: !(props.disabledIds ?? []).includes(n.id),
    data: n,
  }
}

function find(list: PrimeTreeNode[], key: string): PrimeTreeNode | null {
  for (const n of list) {
    if (n.key === key) return n
    const hit = n.children ? find(n.children, key) : null
    if (hit) return hit
  }
  return null
}

async function loadChildren(node: PrimeTreeNode) {
  if (node.children) return
  node.loading = true
  try {
    node.children = (await wikiApi.tree(props.spaceKey, String(node.key))).map(toNode)
    node.leaf = node.children.length === 0
  } finally {
    node.loading = false
    nodes.value = [...nodes.value]
  }
}

async function loadRoot() {
  loading.value = true
  failed.value = false
  try {
    nodes.value = (await wikiApi.tree(props.spaceKey)).map(toNode)
    expandedKeys.value = {}
    await openPath()
  } catch {
    failed.value = true
  } finally {
    loading.value = false
  }
}

async function openPath() {
  for (const id of props.expandPath ?? []) {
    const node = find(nodes.value, id)
    if (!node || node.leaf) continue
    await loadChildren(node)
    expandedKeys.value = { ...expandedKeys.value, [id]: true }
  }
}

watch(() => [props.spaceKey, props.version], loadRoot, { immediate: true })
watch(() => (props.expandPath ?? []).join('/'), openPath)
watch(
  () => props.selectedId,
  (id) => {
    selectionKeys.value = id ? { [id]: true } : {}
  },
  { immediate: true },
)

function onSelect(node: PrimeTreeNode) {
  emit('select', node.data as TreeNode)
}
</script>

<template>
  <div class="page-tree">
    <p v-if="failed" class="muted">{{ t('wiki.tree.failed') }}</p>
    <p v-else-if="!loading && nodes.length === 0" class="muted">{{ t('wiki.tree.empty') }}</p>
    <Tree
      v-else
      v-model:expanded-keys="expandedKeys"
      v-model:selection-keys="selectionKeys"
      :value="nodes"
      :loading="loading"
      selection-mode="single"
      :meta-key-selection="false"
      loading-mode="icon"
      :aria-label="t('wiki.tree.label')"
      class="tree"
      @node-expand="loadChildren"
      @node-select="onSelect"
    />
  </div>
</template>

<style scoped>
.page-tree { min-inline-size: 0; }
.tree { padding: 0; background: transparent; border: 0; }
.tree :deep(.p-tree-node-label) { overflow-wrap: anywhere; }

/* Connector lines: each child hangs on a vertical guide from its parent with an elbow to its own row. */
.tree :deep(.p-tree-node-children) { margin-inline-start: 1rem; padding: 0; }
.tree :deep(.p-tree-node-children > .p-tree-node) { position: relative; padding-inline-start: 1rem; }
.tree :deep(.p-tree-node-children > .p-tree-node)::before {
  content: '';
  position: absolute;
  inset-inline-start: 0;
  inset-block-start: 0;
  inline-size: 0.85rem;
  block-size: 1.15rem;
  border-inline-start: 1px solid color-mix(in srgb, var(--raavi-text-muted) 55%, transparent);
  border-block-end: 1px solid color-mix(in srgb, var(--raavi-text-muted) 55%, transparent);
  border-end-start-radius: 6px;
}
.tree :deep(.p-tree-node-children > .p-tree-node:not(:last-child))::after {
  content: '';
  position: absolute;
  inset-inline-start: 0;
  inset-block-start: 1.15rem;
  inset-block-end: 0;
  border-inline-start: 1px solid color-mix(in srgb, var(--raavi-text-muted) 55%, transparent);
}
.muted { color: var(--raavi-text-muted); font-size: 0.9rem; }
</style>
