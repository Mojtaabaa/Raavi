import { describe, expect, it } from 'vitest'
import { isSafeHref } from '@/modules/editor/setup'
import { rewritePathHref, setPathRewritesForTests } from '@/modules/editor/pathRewrites'

describe('path rewrites', () => {
  it('rewrites configured prefixes and allows them as hrefs', () => {
    setPathRewritesForTests([{ prefix: 'vianna', baseUrl: 'https://svn.viannacloud.ir' }])
    expect(rewritePathHref('vianna/UI/TMS/Personnel/Current-classes.html')).toBe(
      'https://svn.viannacloud.ir/UI/TMS/Personnel/Current-classes.html',
    )
    expect(isSafeHref('vianna/UI/a.html')).toBe(true)
    expect(isSafeHref('other/UI/a.html')).toBe(false)
    expect(rewritePathHref('https://example.com')).toBeNull()
  })
})
