import { api, apiRequest } from './http'

export type ColumnType = 'Text' | 'Number' | 'Date' | 'Checkbox' | 'Select' | 'Url'

export interface DbOption {
  id: string
  label: string
}

export interface DbColumn {
  id: string
  name: string
  type: ColumnType
  options: DbOption[]
}

export interface SpaceDatabase {
  id: string
  spaceKey: string
  spaceName: string
  name: string
  description: string | null
  columns: DbColumn[]
  rowCount: number
  createdByName: string | null
  createdAt: string
  updatedAt: string
  revision: number
  canEdit: boolean
}

export type CellValue = string | number | boolean

export interface DbRow {
  id: string
  databaseId: string
  values: Record<string, CellValue>
  createdByName: string | null
  createdAt: string
  updatedAt: string
  revision: number
}

export interface DbRowPage {
  total: number
  rows: DbRow[]
}

export interface DatabaseInput {
  name: string
  description: string | null
  columns: { id?: string; name: string; type: ColumnType; options?: { id?: string; label: string }[] }[]
}

export interface RowQuery {
  q?: string
  sort?: string | null
  dir?: 'asc' | 'desc'
  skip?: number
  take?: number
}

const enc = encodeURIComponent

export const databasesApi = {
  list: (spaceKey: string) => api<SpaceDatabase[]>('GET', `/spaces/${enc(spaceKey)}/databases`),
  get: (id: string) => api<SpaceDatabase>('GET', `/databases/${enc(id)}`),
  create: (spaceKey: string, body: DatabaseInput) => api<SpaceDatabase>('POST', `/spaces/${enc(spaceKey)}/databases`, body),
  update: (db: SpaceDatabase, body: DatabaseInput) => apiRequest<SpaceDatabase>('PUT', `/databases/${enc(db.id)}`, body, { ifMatch: `"r${db.revision}"` }).then((r) => r.data),
  remove: (id: string) => api<void>('DELETE', `/databases/${enc(id)}`),
  rows: (id: string, query: RowQuery = {}) => {
    const p = new URLSearchParams()
    if (query.q) p.set('q', query.q)
    if (query.sort) p.set('sort', query.sort)
    if (query.sort && query.dir) p.set('dir', query.dir)
    p.set('skip', String(query.skip ?? 0))
    p.set('take', String(query.take ?? 50))
    return api<DbRowPage>('GET', `/databases/${enc(id)}/rows?${p.toString()}`)
  },
  addRow: (id: string, values: Record<string, CellValue>) => api<DbRow>('POST', `/databases/${enc(id)}/rows`, { values }),
  updateRow: (row: DbRow, values: Record<string, CellValue>) => apiRequest<DbRow>('PUT', `/database-rows/${enc(row.id)}`, { values }, { ifMatch: `"r${row.revision}"` }).then((r) => r.data),
  removeRow: (id: string) => api<void>('DELETE', `/database-rows/${enc(id)}`),
  exportUrl: (id: string) => `/api/v1/databases/${enc(id)}/export`,
}
