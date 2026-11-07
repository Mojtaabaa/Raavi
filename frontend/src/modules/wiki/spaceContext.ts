import { inject, type InjectionKey, type Ref } from 'vue'
import type { Space } from '@/core/api/wiki'

/** Shared by the space layout and its child views (sidebar tree refresh after create/move/delete). */
export interface SpaceContext {
  space: Ref<Space | null>
  etag: Ref<string | null>
  treeVersion: Ref<number>
  /** Ids from the space root to the current page (expanded in the sidebar tree). */
  activePath: Ref<string[]>
  reloadSpace: () => Promise<void>
  reloadTree: () => void
}

export const SPACE_CONTEXT: InjectionKey<SpaceContext> = Symbol('raavi.space')

export function useSpaceContext(): SpaceContext {
  const ctx = inject(SPACE_CONTEXT)
  if (!ctx) throw new Error('useSpaceContext() must be used inside SpaceLayout')
  return ctx
}
