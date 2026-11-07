/**
 * Client-side renderers for source-based nodes (EDITOR_ARCHITECTURE.md §5). Libraries are loaded lazily so pages
 * without diagrams or formulas do not pay for them. Nothing here ever runs on the server.
 */
let mermaidReady: Promise<typeof import('mermaid')['default']> | null = null
let counter = 0

export async function renderMermaid(target: HTMLElement, source: string): Promise<void> {
  target.replaceChildren()
  if (!source.trim()) return
  mermaidReady ??= import('mermaid').then((m) => {
    // 'sandbox' renders the SVG inside a sandboxed iframe: diagram text can never run script in the page.
    m.default.initialize({ startOnLoad: false, securityLevel: 'sandbox', htmlLabels: false, theme: 'neutral' })
    return m.default
  })
  const mermaid = await mermaidReady
  const { svg } = await mermaid.render(`raavi-mermaid-${++counter}`, source)
  // Output of mermaid in sandbox mode is an <iframe sandbox srcdoc=…>; parse it and only accept that element.
  const parsed = new DOMParser().parseFromString(svg, 'text/html').body.firstElementChild
  if (parsed?.tagName === 'IFRAME' && parsed.hasAttribute('sandbox')) {
    parsed.removeAttribute('sandbox')
    parsed.setAttribute('sandbox', '')
    target.append(document.importNode(parsed, true))
  } else {
    throw new Error('unexpected mermaid output')
  }
}

let katexReady: Promise<typeof import('katex')['default']> | null = null
let katexModule: typeof import('katex')['default'] | null = null

export async function loadKatex() {
  katexReady ??= Promise.all([import('katex'), import('katex/dist/katex.min.css')]).then(([k]) => (katexModule = k.default))
  return katexReady
}

export function renderMath(target: HTMLElement, latex: string, displayMode: boolean): void {
  if (!latex.trim()) {
    target.replaceChildren()
    return
  }
  if (!katexModule) {
    void loadKatex().then(() => renderMath(target, latex, displayMode))
    return
  }
  // trust: false blocks \href, \includegraphics etc.; maxExpand bounds macro expansion.
  katexModule.render(latex, target, { displayMode, throwOnError: false, trust: false, strict: 'ignore', maxExpand: 500, output: 'htmlAndMathml' })
}
