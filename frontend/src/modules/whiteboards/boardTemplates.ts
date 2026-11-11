import type { BoardElement, BoardShape } from '@/core/api/whiteboards'
import { newId, PALETTE } from './boardModel'

/** Ready-made starting points. Each builds fresh elements (new ids) around (cx, cy); the board puts them in one undoable step. */
export type TemplateId = 'flowchart' | 'kanban' | 'swot' | 'mindmap'

export const TEMPLATE_IDS: TemplateId[] = ['flowchart', 'kanban', 'swot', 'mindmap']

export const TEMPLATE_WORD_KEYS = ['start', 'step', 'step2', 'decision', 'yes', 'no', 'end', 'todo', 'doing', 'done', 'task', 'strengths', 'weaknesses', 'opportunities', 'threats', 'topic', 'idea'] as const

/** Words of the current language, keyed the way `buildTemplate` reads them. */
export function templateLabels(translate: (key: string) => string): Record<string, string> {
  return Object.fromEntries(TEMPLATE_WORD_KEYS.map((k) => [k, translate(`whiteboards.templateWords.${k}`)]))
}

type Labels = Record<string, string>

const el = (type: BoardShape, x: number, y: number, w: number, h: number, extra: Partial<BoardElement> = {}): BoardElement => ({
  id: newId(), type, x, y, w, h, rotation: 0, stroke: PALETTE[0], fill: null, strokeWidth: 2, text: '', fontSize: 20, rtl: false, points: [], ...extra,
})
const text = (x: number, y: number, value: string, fontSize = 20, w = 0): BoardElement => el('Text', x, y, w, 0, { text: value, fontSize })
const arrow = (x: number, y: number, dx: number, dy: number): BoardElement => el('Arrow', x, y, 0, 0, { points: [0, 0, dx, dy] })
const sticky = (x: number, y: number, label: string, fill: string): BoardElement => el('Sticky', x, y, 170, 110, { text: label, fill })

/** `labels` carries the words of the current language (so the template reads naturally in Persian and in English). */
export function buildTemplate(id: TemplateId, cx: number, cy: number, labels: Labels): BoardElement[] {
  switch (id) {
    case 'flowchart': {
      const x = cx - 80
      const y0 = cy - 260
      return [
        el('Ellipse', x, y0, 160, 70, { fill: '#bbf7d0' }), text(x + 50, y0 + 22, labels.start!),
        arrow(cx, y0 + 70, 0, 50),
        el('Rect', x, y0 + 120, 160, 70, { fill: '#bfdbfe' }), text(x + 40, y0 + 142, labels.step!),
        arrow(cx, y0 + 190, 0, 50),
        el('Diamond', x - 20, y0 + 240, 200, 120, { fill: '#fde68a' }), text(x + 50, y0 + 285, labels.decision!),
        arrow(cx + 100, y0 + 300, 90, 0), text(cx + 120, y0 + 270, labels.yes!, 16),
        el('Rect', cx + 190, y0 + 265, 160, 70, { fill: '#bfdbfe' }), text(cx + 225, y0 + 287, labels.step2!),
        arrow(cx, y0 + 360, 0, 50), text(cx + 10, y0 + 372, labels.no!, 16),
        el('Ellipse', x, y0 + 410, 160, 70, { fill: '#fecaca' }), text(x + 60, y0 + 432, labels.end!),
      ]
    }
    case 'kanban': {
      const colW = 220
      const x0 = cx - (colW * 3 + 40) / 2
      const y0 = cy - 220
      const cols = [labels.todo!, labels.doing!, labels.done!]
      const fills = ['#fde68a', '#bfdbfe', '#bbf7d0']
      return cols.flatMap((c, i) => [
        el('Rect', x0 + i * (colW + 20), y0, colW, 440, { fill: '#f3f4f6', stroke: '#9ca3af' }),
        text(x0 + i * (colW + 20) + 16, y0 + 12, c, 22),
        sticky(x0 + i * (colW + 20) + 25, y0 + 60, labels.task!, fills[i]!),
        ...(i === 0 ? [sticky(x0 + 25, y0 + 190, labels.task!, fills[0]!)] : []),
      ])
    }
    case 'swot': {
      const s = 230
      const x0 = cx - s
      const y0 = cy - s
      const quads: [string, string, number, number][] = [[labels.strengths!, '#bbf7d0', 0, 0], [labels.weaknesses!, '#fecaca', 1, 0], [labels.opportunities!, '#bfdbfe', 0, 1], [labels.threats!, '#fde68a', 1, 1]]
      return quads.flatMap(([label, fill, col, row]) => [el('Rect', x0 + col * s, y0 + row * s, s, s, { fill }), text(x0 + col * s + 16, y0 + row * s + 14, label, 24)])
    }
    case 'mindmap': {
      const out: BoardElement[] = [el('Ellipse', cx - 90, cy - 40, 180, 80, { fill: '#ddd6fe' }), text(cx - 55, cy - 12, labels.topic!, 22)]
      const arms: [number, number][] = [[-330, -170], [160, -170], [-330, 110], [160, 110]]
      arms.forEach(([dx, dy], i) => {
        out.push(el('Rect', cx + dx, cy + dy, 170, 60, { fill: ['#bfdbfe', '#bbf7d0', '#fde68a', '#fbcfe8'][i]! }), text(cx + dx + 28, cy + dy + 18, `${labels.idea!} ${i + 1}`, 18))
        const fromX = dx < 0 ? cx - 60 : cx + 60
        const toX = dx < 0 ? cx + dx + 170 : cx + dx
        out.push(arrow(fromX, cy + (dy < 0 ? -30 : 30), toX - fromX, cy + dy + 30 - (cy + (dy < 0 ? -30 : 30))))
      })
      return out
    }
  }
}
