import type { CellValue, DbColumn } from '@/core/api/databases'

/** Minimal RFC 4180 reader: quoted fields (with "" escapes and line breaks inside), `,` `;` or tab as separator (taken from the first line). */
export function parseCsv(text: string): string[][] {
  const src = text.replace(/^﻿/, '')
  const firstLine = src.split(/\r?\n/, 1)[0] ?? ''
  const sep = [',', ';', '\t'].map((s) => [s, firstLine.split(s).length] as const).sort((a, b) => b[1] - a[1])[0]![0]
  const rows: string[][] = []
  let row: string[] = []
  let field = ''
  let quoted = false
  for (let i = 0; i < src.length; i++) {
    const ch = src[i]!
    if (quoted) {
      if (ch === '"') {
        if (src[i + 1] === '"') {
          field += '"'
          i++
        } else quoted = false
      } else field += ch
    } else if (ch === '"' && field === '') quoted = true
    else if (ch === sep) {
      row.push(field)
      field = ''
    } else if (ch === '\n' || ch === '\r') {
      if (ch === '\r' && src[i + 1] === '\n') i++
      row.push(field)
      field = ''
      if (row.some((c) => c.trim() !== '')) rows.push(row)
      row = []
    } else field += ch
  }
  row.push(field)
  if (row.some((c) => c.trim() !== '')) rows.push(row)
  return rows
}

export interface CsvImportPlan {
  /** Rows ready to be added: column id → value. */
  rows: Record<string, CellValue>[]
  /** Header names that match no column (their cells are ignored). */
  ignored: string[]
  /** Cells that could not be converted (row number counted from 1 after the header, column name). */
  problems: { row: number; column: string }[]
}

const norm = (s: string) => s.trim().toLocaleLowerCase()
const TRUE = new Set(['true', '1', 'yes', 'y', 'x', '✓', 'بله', 'درست'])
const FALSE = new Set(['false', '0', 'no', 'n', '', 'خیر', 'نادرست'])

/** Maps the parsed table to the columns of a database by header name; the first line is the header. */
export function planImport(table: string[][], columns: DbColumn[]): CsvImportPlan {
  const [header = [], ...body] = table
  const map = header.map((h) => columns.find((c) => norm(c.name) === norm(h)) ?? null)
  const ignored = header.filter((_, i) => !map[i]).map((h) => h.trim()).filter(Boolean)
  const rows: Record<string, CellValue>[] = []
  const problems: CsvImportPlan['problems'] = []
  body.forEach((cells, r) => {
    const values: Record<string, CellValue> = {}
    cells.forEach((raw, i) => {
      const col = map[i]
      const text = raw.trim()
      if (!col || text === '' && col.type !== 'Checkbox') return
      switch (col.type) {
        case 'Number': {
          const n = Number(text.replace(/[,\s]/g, ''))
          if (Number.isFinite(n)) values[col.id] = n
          else problems.push({ row: r + 1, column: col.name })
          break
        }
        case 'Checkbox':
          if (TRUE.has(norm(text))) values[col.id] = true
          else if (!FALSE.has(norm(text))) problems.push({ row: r + 1, column: col.name })
          break
        case 'Select': {
          const o = col.options.find((x) => norm(x.label) === norm(text))
          if (o) values[col.id] = o.id
          else problems.push({ row: r + 1, column: col.name })
          break
        }
        case 'Date':
          if (/^\d{4}-\d{2}-\d{2}$/.test(text)) values[col.id] = text
          else problems.push({ row: r + 1, column: col.name })
          break
        default:
          values[col.id] = text
      }
    })
    rows.push(values)
  })
  return { rows, ignored, problems }
}
