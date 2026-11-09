import { onBeforeUnmount, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { spaceTrail } from './trail'
import { useCrumbsStore } from '@/stores/crumbs'

/** Trail of a view for one thing inside a space (database, board, deck): Home › Spaces › Space › Section › Title. */
export function useEntityCrumbs(entity: () => { spaceKey: string; spaceName: string; title: string } | null | undefined, section: { labelKey: string; routeName: string }) {
  const { t } = useI18n()
  const crumbs = useCrumbsStore()
  watch(
    entity,
    (e) => {
      if (!e) return
      crumbs.set(spaceTrail({ key: e.spaceKey, name: e.spaceName }, t, { label: t(section.labelKey), to: { name: section.routeName, params: { key: e.spaceKey } } }, { label: e.title }))
    },
    { immediate: true },
  )
  onBeforeUnmount(() => crumbs.clear())
}
