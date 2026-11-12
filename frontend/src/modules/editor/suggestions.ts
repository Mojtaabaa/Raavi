import { Extension, type Editor, type Range } from '@tiptap/core'
import { PluginKey } from '@tiptap/pm/state'
import Suggestion, { type SuggestionOptions, type SuggestionProps, type SuggestionKeyDownProps } from '@tiptap/suggestion'
import { VueRenderer } from '@tiptap/vue-3'
import SuggestionList from './components/SuggestionList.vue'
import type { SuggestionEntry } from './types'

type Labels = { empty: string; aria: string }

/** Mounts {@link SuggestionList} in a floating container positioned at the caret (logical inline-start in RTL). */
export function floatingRenderer(labels: () => Labels): SuggestionOptions<SuggestionEntry>['render'] {
  return () => {
    let renderer: VueRenderer | null = null
    let host: HTMLDivElement | null = null
    const place = (props: SuggestionProps<SuggestionEntry>) => {
      const rect = props.clientRect?.()
      if (!host || !rect) return
      host.style.insetBlockStart = `${rect.bottom + window.scrollY + 4}px`
      host.style.removeProperty('left')
      host.style.removeProperty('right')
      // inset-inline-start follows html[dir]: in RTL it is the physical right edge of the viewport.
      if (document.documentElement.dir === 'rtl') {
        host.style.insetInlineStart = `${Math.max(8, document.documentElement.clientWidth - rect.right)}px`
      } else {
        host.style.insetInlineStart = `${rect.left + window.scrollX}px`
      }
    }
    return {
      onStart: (props) => {
        host = document.createElement('div')
        host.className = 'raavi-suggestion-host'
        host.style.position = 'absolute'
        host.style.zIndex = '1200'
        document.body.append(host)
        renderer = new VueRenderer(SuggestionList, {
          props: { items: props.items, command: props.command, emptyLabel: labels().empty, ariaLabel: labels().aria },
          editor: props.editor,
        })
        if (renderer.element) host.append(renderer.element)
        place(props)
      },
      onUpdate: (props) => {
        renderer?.updateProps({ items: props.items, command: props.command })
        place(props)
      },
      onKeyDown: (props: SuggestionKeyDownProps) => {
        if (props.event.key === 'Escape') {
          host?.remove()
          return true
        }
        return (renderer?.ref as { onKeyDown?: (p: SuggestionKeyDownProps) => boolean } | undefined)?.onKeyDown?.(props) ?? false
      },
      onExit: () => {
        renderer?.destroy()
        host?.remove()
        renderer = null
        host = null
      },
    }
  }
}

export interface SlashCommandOptions {
  items: (query: string) => (SuggestionEntry & { run: (editor: Editor, range: Range) => void })[]
  labels: () => Labels
}

/** "/" command menu over the registry's slash items. */
export const SlashCommand = Extension.create<SlashCommandOptions>({
  name: 'raaviSlashCommand',
  addOptions() {
    return { items: () => [], labels: () => ({ empty: '', aria: '' }) }
  },
  addProseMirrorPlugins() {
    const options = this.options
    return [
      Suggestion<SuggestionEntry & { run: (editor: Editor, range: Range) => void }>({
        editor: this.editor,
        pluginKey: new PluginKey('raaviSlash'),
        char: '/',
        startOfLine: false,
        allowSpaces: false,
        items: ({ query }) => options.items(query).slice(0, 12),
        command: ({ editor, range, props }) => props.run(editor, range),
        render: floatingRenderer(options.labels) as SuggestionOptions<SuggestionEntry & { run: (editor: Editor, range: Range) => void }>['render'],
      }),
    ]
  },
})
