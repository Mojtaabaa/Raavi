# ADR-029 — Slides (Phase 9d)

**Status:** Accepted (2026-11-08)

## Context

Fourth step of Phase 9. A slide deck is an ordered list of slides owned by a space, with an editor, a presenter mode and export to PDF and PPTX. The requirement is persistence, tenant ownership, permissions, navigation and export — in both Persian (right-to-left) and English.

## Decisions

1. **A deck is one document.** `content.SlideDecks` stores the title, the reading direction (`Ltr`/`Rtl`) and the slide list as validated JSON (`SlidesJson`). A deck is edited and saved as a whole with one ETag (`"r{n}"`, `If-Match` required → 428/412), exactly like a Confluence-style page rather than row by row: slides are reordered and edited together, and a deck is capped at 100 slides, so one write is small. The table is tenant-owned with RLS and a composite FK to the space.
2. **Slides are text, not free-form canvases.** A slide has a stable server-assigned id, a layout (`Title`, `Bullets`, `Text`), a title, a body (one line per bullet/paragraph) and speaker notes. Limits: 100 slides, title 200, body 4 000 characters / 30 lines, notes 2 000; control characters other than newline/tab are refused. Ids are stable across reordering; a forged id is refused. Free-form shapes, images and animations are the whiteboard's job (9e) and depend on media storage (7b, deferred).
3. **Permissions come from the space**, per request: View to read, present and export; Edit on an active space to create, change or delete. A deck of a space the caller cannot view is a 404 (including export).
4. **PPTX is produced on the server** with the Open XML SDK (`DocumentFormat.OpenXml` 3.5.1, MIT): a 16:9 file with one master, theme, blank layout, text boxes per slide and real speaker notes. Right-to-left decks get `rtl` paragraphs, right alignment and the `fa-IR` language tag; text is always written as data (no markup injection). The file is schema-validated in the tests with `OpenXmlValidator` (0 errors) and re-read through the SDK; **it was not opened in PowerPoint itself**, so appearance details (RTL bullet margins, autofit on very long text) are unverified there.
5. **PDF is produced by the browser.** `/slides/{id}/print` is a shell-less page with one 13.333×7.5 in page per slide; "Save as PDF" in the print dialog creates the file. This keeps Persian shaping identical to the screen and avoids a server-side PDF engine and its licence (QuestPDF's community licence has a revenue threshold; no PDF library is a dependency). The cost: PDF export is a print-dialog step, not a one-click download, and it cannot be produced by a server job (e.g. scheduled). If server-side PDF is required later, it is an additive endpoint behind a licence decision.
6. **Presenter mode** is a full-screen overlay in the editor (keyboard: arrows/space/page keys, Esc; arrows are mirrored for RTL decks) with the speaker notes shown below the slide. The editor warns before leaving with unsaved changes; export links are disabled until the deck is saved because they export the saved version.
7. **One renderer.** `SlideCanvas` renders the preview, present mode and printed pages from the same markup using container-query units; text is rendered as text.

## Not in 9d

Images, shapes, tables, charts, animations/transitions, themes and per-deck colours, slide masters, import from PPTX, slide thumbnails as images, embedding a deck in a page, comments or co-editing (Yjs) on slides, deck history, server-side PDF, presenter view in a second window.
