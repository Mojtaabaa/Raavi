export interface Crumb {
  label: string
  to?: { name: string; params?: Record<string, string> }
}

type Translate = (key: string) => string

/** Route name → label key of the page itself (the last crumb). */
const LABEL: Record<string, string> = {
  spaces: 'nav.spaces',
  tasks: 'nav.tasks',
  calendar: 'nav.calendar',
  approvals: 'nav.approvals',
  profile: 'profile.title',
  'admin-members': 'nav.members',
  'admin-groups': 'nav.groups',
  'admin-providers': 'nav.providers',
  'admin-permissions': 'nav.permissions',
  'admin-ai': 'nav.ai',
  'admin-connectors': 'nav.connectors',
  'admin-webhooks': 'nav.webhooks',
  'admin-export': 'nav.export',
  'admin-migrations': 'nav.migrations',
  'admin-activity': 'activityLog.title',
  'system-tenants': 'nav.tenants',
  'space-workflows': 'workflows.nav',
  'space-whiteboards': 'whiteboards.nav',
  'space-slides': 'slides.nav',
  'space-databases': 'databases.nav',
  'space-calendar': 'calendar.nav',
  'space-tasks': 'tasks.nav',
  'space-settings': 'wiki.space.settings',
  'system-errors': 'errorLog.title',
}

/**
 * The trail above a page: Home, then (for administration pages) Administration, then the page. Views that know more (the space and the page
 * ancestors, a database or board with its space) hand over their own trail; this is the fallback for plain routes.
 */
export function staticTrail(routeName: string | null | undefined, t: Translate): Crumb[] {
  if (!routeName || routeName === 'home') return []
  const home: Crumb = { label: t('nav.home'), to: { name: 'home' } }
  const key = LABEL[routeName]
  if (!key) return []
  const crumbs: Crumb[] = [home]
  if (routeName.startsWith('admin-')) crumbs.push({ label: t('nav.administration') })
  crumbs.push({ label: t(key) })
  return crumbs
}

/** Home › Spaces › Space › section — shared by the views that live inside a space. */
export function spaceTrail(space: { key: string; name: string }, t: Translate, ...rest: Crumb[]): Crumb[] {
  return [
    { label: t('nav.home'), to: { name: 'home' } },
    { label: t('nav.spaces'), to: { name: 'spaces' } },
    { label: space.name, to: { name: 'space', params: { key: space.key } } },
    ...rest,
  ]
}
