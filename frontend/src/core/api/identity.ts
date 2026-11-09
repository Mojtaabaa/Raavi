import { api, apiUpload } from './http'

export interface Me {
  id: string
  displayName: string
  email: string | null
  preferredLocale: string | null
  hostKind: 'tenant' | 'system'
  isTenantAdministrator: boolean
  isSystemAdministrator: boolean
  provider: string
  theme?: string | null
}

export interface ProviderSummary {
  id: string
  type: string
  flow: 'credentials' | 'redirect'
  displayName: { fa: string; en: string }
  isDefault: boolean
}

export interface Member {
  userId: string
  displayName: string
  email: string | null
  status: 'Active' | 'Suspended' | 'PendingApproval'
  isTenantAdministrator: boolean
  joinedAt: string
  lastLoginAt: string | null
}

export interface Invitation {
  id: string
  email: string
  tenantAdministrator: boolean
  createdAt: string
  expiresAt: string
  status: 'pending' | 'accepted' | 'revoked' | 'expired'
}

export interface InvitationCreated {
  id: string
  email: string
  expiresAt: string
  acceptUrl: string
}

export interface Group {
  id: string
  name: string
  description: string | null
  source: string
  memberCount: number
}

export interface LinkedIdentity {
  id: string
  providerId: string
  providerName: { fa: string; en: string }
  externalUsername: string | null
  externalEmail: string | null
  createdAt: string
  lastAuthenticatedAt: string | null
}

export interface Workspace {
  slug: string
  displayName: { fa: string; en: string }
  url: string
  isCurrent: boolean
}

export type ProvisioningMode = 'ExistingUsersOnly' | 'AutomaticCreation' | 'AdministratorApproval'

export interface ProviderPolicy {
  providerId: string
  enabled: boolean
  isDefault: boolean
  displayOrder: number
  provisioning: ProvisioningMode
  allowedEmailDomains: string[]
  allowedExternalGroups: string[]
}

export interface ProviderHealth {
  isHealthy: boolean
  statusCode: string
  detail: string | null
}

export interface Tenant {
  id: string
  code: string
  slug: string
  displayNameFa: string
  displayNameEn: string
  status: 'Provisioning' | 'Active' | 'Suspended' | 'Deactivated' | 'PendingDeletion' | 'Deleted'
  statusReason: string | null
  createdAt: string
  /** Address of the workspace (system list only). */
  url?: string | null
}

export const identityApi = {
  me: () => api<Me>('GET', '/auth/me'),
  providers: () => api<ProviderSummary[]>('GET', '/auth/providers'),
  loginLocal: (email: string, password: string) => api<Me>('POST', '/auth/login/local', { email, password }),
  logout: () => api<void>('POST', '/auth/logout'),
  loginCredentials: (providerId: string, email: string, password: string) =>
    api<Me>('POST', `/auth/login/credentials/${encodeURIComponent(providerId)}`, { email, password }),
  beginLogin: (providerId: string, returnPath: string) =>
    api<{ redirectUrl: string }>('POST', `/auth/login/${encodeURIComponent(providerId)}/begin`, { returnPath }),
  identities: () => api<LinkedIdentity[]>('GET', '/auth/me/identities'),
  beginLink: (providerId: string) => api<{ redirectUrl: string }>('POST', `/auth/me/identities/${encodeURIComponent(providerId)}/link`),
  unlink: (id: string) => api<void>('DELETE', `/auth/me/identities/${id}`),
  workspaces: () => api<Workspace[]>('GET', '/auth/me/workspaces'),
  providerPolicies: () => api<ProviderPolicy[]>('GET', '/tenant/authentication-providers'),
  saveProviderPolicies: (p: ProviderPolicy[]) => api<void>('PUT', '/tenant/authentication-providers', p),
  testProvider: (providerId: string) => api<ProviderHealth>('POST', `/tenant/authentication-providers/${encodeURIComponent(providerId)}/test`),
  setTheme: (theme: string | null) => api<void>('PUT', '/auth/me/theme', { theme }),
  systemOptions: () => api<{ mfaRequired: boolean }>('GET', '/auth/system/options'),
  setMemberPassword: (id: string, newPassword: string) => api<void>('PUT', `/tenant/members/${id}/password`, { newPassword }),
  setMemberPicture: (id: string, file: Blob) => {
    const form = new FormData()
    form.append('file', file, 'avatar')
    return apiUpload<{ version: number }>(`/tenant/members/${id}/avatar`, form, 'PUT')
  },
  removeMemberPicture: (id: string) => api<void>('DELETE', `/tenant/members/${id}/avatar`),
  setTenantLogo: (id: string, file: Blob) => {
    const form = new FormData()
    form.append('file', file, 'logo')
    return apiUpload<number>(`/system/tenants/${id}/logo`, form, 'PUT')
  },
  removeTenantLogo: (id: string) => api<void>('DELETE', `/system/tenants/${id}/logo`),
  setLocale: (locale: string) => api<void>('PUT', '/auth/me/preferences', { locale }),
  inspectInvitation: (token: string) => api<{ email: string; expiresAt: string; tenantAdministrator: boolean }>('POST', '/auth/invitations/inspect', { token }),
  acceptInvitation: (token: string, mode: 'new' | 'existing', password: string, displayName?: string) =>
    api<Me>('POST', '/auth/invitations/accept', { token, mode, password, displayName }),

  systemLogin: (email: string, password: string, code: string) => api<Me>('POST', '/auth/system/login', { email, password, code }),
  /** First sign-in of a bootstrap account: verifies the temporary password and returns the setup link (no session). */
  systemFirstLogin: (email: string, password: string) => api<{ acceptUrl: string }>('POST', '/auth/system/first-login', { email, password }),
  inspectSetup: (token: string) => api<{ email: string; expiresAt: string; passwordChangeRequired?: boolean }>('POST', '/auth/system/setup/inspect', { token }),
  beginSetup: (token: string) => api<{ secret: string; otpAuthUri: string }>('POST', '/auth/system/setup/begin', { token }),
  completeSetup: (token: string, displayName: string, password: string, code: string, newPassword?: string) =>
    api<Me>('POST', '/auth/system/setup/complete', { token, displayName, password, code, ...(newPassword ? { newPassword } : {}) }),

  members: () => api<Member[]>('GET', '/tenant/members'),
  suspendMember: (id: string) => api<void>('POST', `/tenant/members/${id}/suspend`),
  activateMember: (id: string) => api<void>('POST', `/tenant/members/${id}/activate`),
  removeMember: (id: string) => api<void>('DELETE', `/tenant/members/${id}`),
  setAdmin: (id: string, tenantAdministrator: boolean) => api<void>('PUT', `/tenant/members/${id}/roles`, { tenantAdministrator }),
  invitations: () => api<Invitation[]>('GET', '/tenant/invitations'),
  invite: (email: string, tenantAdministrator: boolean) => api<InvitationCreated>('POST', '/tenant/invitations', { email, tenantAdministrator }),
  revokeInvitation: (id: string) => api<void>('DELETE', `/tenant/invitations/${id}`),
  groups: () => api<Group[]>('GET', '/tenant/groups'),
  createGroup: (name: string, description: string | null) => api<Group>('POST', '/tenant/groups', { name, description }),
  deleteGroup: (id: string) => api<void>('DELETE', `/tenant/groups/${id}`),
  groupMembers: (id: string) => api<string[]>('GET', `/tenant/groups/${id}/members`),
  addGroupMember: (id: string, userId: string) => api<void>('POST', `/tenant/groups/${id}/members/${userId}`),
  removeGroupMember: (id: string, userId: string) => api<void>('DELETE', `/tenant/groups/${id}/members/${userId}`),

  tenants: () => api<{ items: Tenant[]; total: number }>('GET', '/system/tenants?take=100'),
  createTenant: (t: { code: string; slug: string; displayNameFa: string; displayNameEn: string; defaultLocale: string }) => api<Tenant>('POST', '/system/tenants', t),
  suspendTenant: (id: string, reason: string) => api<Tenant>('POST', `/system/tenants/${id}/suspend`, { reason }),
  activateTenant: (id: string) => api<Tenant>('POST', `/system/tenants/${id}/activate`),
  inviteIntoTenant: (id: string, email: string, tenantAdministrator: boolean) =>
    api<InvitationCreated>('POST', `/system/tenants/${id}/invitations`, { email, tenantAdministrator }),
  discover: (code: string) => api<{ url: string }>('GET', `/discovery/tenants/${encodeURIComponent(code)}`),
}

/** Provider redirect URLs come from our own API; only http(s) targets are ever followed. */
export function safeExternalUrl(value: string): string | null {
  try {
    const url = new URL(value)
    return url.protocol === 'https:' || url.protocol === 'http:' ? url.toString() : null
  } catch {
    return null
  }
}

/** Reads the one-time token from the URL fragment (never sent to servers or in Referer) and removes it from the address bar. */
export function takeFragmentToken(location: Location = window.location, history: History = window.history): string | null {
  const match = /(?:^#|&)token=([A-Za-z0-9_-]{20,128})/.exec(location.hash)
  if (!match) return null
  history.replaceState(history.state, '', location.pathname + location.search)
  return match[1] ?? null
}
