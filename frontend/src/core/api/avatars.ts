import { api, apiUpload } from './http'
import { prefixed } from '@/core/runtime/base'

/** Profile pictures (ADR-046). Other members' pictures exist only inside a workspace. */
export const avatarsApi = {
  /** Versions of the pictures that exist among the ids (ids without a picture are absent). At most 100 ids. */
  versions: (ids: string[]) => api<Record<string, number>>('GET', `/users/avatars?ids=${ids.map(encodeURIComponent).join(',')}`),
  upload: (file: Blob, name = 'avatar') => {
    const form = new FormData()
    form.append('file', file, name)
    return apiUpload<{ version: number }>('/auth/me/avatar', form, 'PUT')
  },
  remove: () => api<void>('DELETE', '/auth/me/avatar'),
}

/** The version in the address makes the picture cacheable for good: a new upload has a new address. */
export const avatarUrl = (userId: string, version: number): string => prefixed(`/api/v1/users/${encodeURIComponent(userId)}/avatar?v=${version}`)
