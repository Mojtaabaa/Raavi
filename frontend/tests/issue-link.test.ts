import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { Editor } from '@tiptap/core'
import { EditorContent } from '@tiptap/vue-3'
import { defineComponent, h } from 'vue'
import PrimeVue from 'primevue/config'
import { configureHttp } from '@/core/api/http'
import { createRaaviI18n } from '@/core/i18n'
import { createEditorRegistry } from '@/modules/editor/setup'
import { fakeFetch, json, problem } from './support/fakeApi'

const CONNECTOR = '0b9f6a3c-1d2e-4f50-8a71-92b3c4d5e6f7'
const doc = (reference: string, connector: string | null = CONNECTOR) => ({
  type: 'doc',
  content: [{ type: 'paragraph', attrs: { textAlign: null, dir: null }, content: [{ type: 'text', text: 'See ' }, { type: 'issue', attrs: { connector, reference } }] }],
})

describe('issue link in the editor', () => {
  let wrapper: VueWrapper | null = null
  let editor: Editor | null = null
  beforeEach(() => {
    (globalThis as { __raaviIssueCache?: Map<string, unknown> }).__raaviIssueCache?.clear()
  })
  afterEach(() => {
    wrapper?.unmount()
    editor?.destroy()
    wrapper = null
    editor = null
    vi.unstubAllGlobals()
  })

  async function show(f: ReturnType<typeof fakeFetch>, content: object, editable = false) {
    vi.stubGlobal('fetch', f.fn)
    configureHttp(f.fn)
    const i18n = createRaaviI18n('en')
    editor = new Editor({ extensions: createEditorRegistry().extensions({ views: true }), content, editable })
    const host = defineComponent({ render: () => h(EditorContent, { editor: editor! }) })
    wrapper = mount(host, { attachTo: document.body, global: { plugins: [i18n, PrimeVue] } })
    for (let i = 0; i < 5; i++) {
      await new Promise((r) => setTimeout(r, 20))
      await flushPromises()
    }
    return wrapper
  }

  it('resolves the reference when the page is shown and renders title, status and a safe link', async () => {
    const f = fakeFetch({
      [`GET /api/v1/integrations/issues/lookup`]: () => json({ reference: 'acme/wiki#42', title: 'Fix the login', status: 'open', url: 'https://github.com/acme/wiki/issues/42', type: 'issue' }),
    })
    const w = await show(f, doc('acme/wiki#42'))
    const link = w.get('[data-testid="issue-link"]')
    expect(link.text()).toContain('Fix the login')
    expect(link.attributes('href')).toBe('https://github.com/acme/wiki/issues/42')
    expect(link.attributes('rel')).toContain('noopener')
    expect(w.get('[data-testid="issue-status"]').text()).toBe('open')
    expect(f.calls[0]!.url).toBe(`/api/v1/integrations/issues/lookup?connector=${CONNECTOR}&reference=acme%2Fwiki%2342`)
    expect(JSON.stringify(editor!.getJSON())).not.toContain('Fix the login') // nothing from the tracker is stored in the document
  })

  it('falls back to the plain reference when the tracker cannot be reached', async () => {
    const f = fakeFetch({ 'GET /api/v1/integrations/issues/lookup': () => problem(503, 'issue.upstream_unreachable') })
    const w = await show(f, doc('acme/wiki#7'))
    expect(w.get('[data-testid="issue-error"]').text()).toBe('Not available')
    expect(w.text()).toContain('acme/wiki#7')
    expect(w.find('[data-testid="issue-link"]').exists()).toBe(false)
  })

  it('looks the same reference up once for repeated occurrences', async () => {
    const f = fakeFetch({ 'GET /api/v1/integrations/issues/lookup': () => json({ reference: 'WIKI-9', title: 'Plan', status: 'Open', url: 'https://jira.example.test/browse/WIKI-9', type: 'issue' }) })
    const two = { type: 'doc', content: [doc('WIKI-9').content[0]!, doc('WIKI-9').content[0]!] }
    await show(f, two)
    expect(f.calls.filter((c) => c.url.includes('/lookup'))).toHaveLength(1)
  })

  it('lets an editor pick a tracker and type a reference', async () => {
    const f = fakeFetch({
      'GET /api/v1/integrations/issues/connectors': () => json([{ id: CONNECTOR, name: 'Company GitHub', kind: 'GitHub' }]),
      'GET /api/v1/integrations/issues/lookup': () => json({ reference: 'acme/wiki#1', title: 'First', status: 'open', url: 'https://github.com/acme/wiki/issues/1', type: 'issue' }),
    })
    const w = await show(f, doc('', null), true) // new empty link opens its editor
    const reference = w.get<HTMLInputElement>('[data-testid="issue-reference"]')
    await reference.setValue('acme/wiki#1')
    await w.get('[data-testid="issue-apply"]').trigger('click')
    for (let i = 0; i < 4; i++) {
      await new Promise((r) => setTimeout(r, 20))
      await flushPromises()
    }
    expect(JSON.stringify(editor!.getJSON())).toContain(`"connector":"${CONNECTOR}","reference":"acme/wiki#1"`)
    expect(w.get('[data-testid="issue-link"]').text()).toContain('First')
  })

  it('says so when no tracker is connected', async () => {
    const f = fakeFetch({ 'GET /api/v1/integrations/issues/connectors': () => json([]) })
    const w = await show(f, doc('', null), true)
    expect(w.get('[data-testid="issue-none"]').text()).toContain('No issue tracker is connected')
  })
})
