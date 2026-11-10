/**
 * Folds text for searching exactly like the server does (Raavi.Application.Common.SearchText): ي ى ئ → ی, ك → ک, ة ۀ → ه, أ إ ٱ آ → ا, ؤ → و,
 * digits of every script → 0-9, Arabic marks and tatweel removed, ZWNJ / no-break spaces → space, lower case. Used to highlight the words a
 * person typed in the passages the server found; a test keeps it equal to the server's table.
 */
export function normalizeSearchText(text: string | null | undefined): string {
  if (!text) return ''
  let out = ''
  for (const ch of text) {
    const c = ch.codePointAt(0)!
    if ('يىئی'.includes(ch)) out += 'ی'
    else if ('كک'.includes(ch)) out += 'ک'
    else if ('ةۀە'.includes(ch)) out += 'ه'
    else if ('أإٱآ'.includes(ch)) out += 'ا'
    else if (ch === 'ؤ') out += 'و'
    else if (ch === '\u200C' || ch === '\u200D' || ch === '\u00A0' || ch === '\u202F') out += ' '
    else if (ch === 'ـ' || (c >= 0x064b && c <= 0x065f) || c === 0x0670 || (c >= 0x06d6 && c <= 0x06ed)) continue
    else if (c >= 0x0660 && c <= 0x0669) out += String(c - 0x0660)
    else if (c >= 0x06f0 && c <= 0x06f9) out += String(c - 0x06f0)
    else out += ch.toLowerCase()
  }
  return out
}
