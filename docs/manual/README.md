# Zeus Operator's Manual — living document

This folder is the **source of truth** for the Zeus Operator's Manual. The manual
is a *living document*: it is version-controlled here, edited per chapter, and
re-typeset to PDF on demand. Do not treat the PDF as the source — the PDF is a
build artifact; the Markdown chapters are the source.

## Layout

- `chapters/NN-*.md` — one Markdown file per chapter, numbered for order. Each
  begins with a single `## Chapter Title` (H2); use `###` for sections.
- `assemble.mjs` — builds a cover + clickable table of contents + page-broken
  chapters into `build/Zeus-Operator-Manual.html`.
- `build.sh` — runs the assembler then prints the HTML to PDF via headless Chrome.
- `build/` and `node_modules/` are git-ignored (artifacts only).

## Regenerate the PDF

```bash
cd docs/manual
./build.sh                                   # -> build/Zeus-Operator-Manual.pdf
./build.sh "$HOME/Desktop/Zeus Operator's Manual.pdf"   # or a chosen path
```

Requires Node + npm and Google Chrome (or any Chromium-family browser) for the
print engine. The edition and cover lines are taken from the nearest release
tag (`git describe --tags --match "v[0-9]*"`), so build the manual **after**
tagging. They can still be overridden with the `MANUAL_EDITION` and
`MANUAL_COVERS` environment variables.

## ⚙️ Update rule — keep the manual current with every release

**Hard rule:** the operator manual is part of the release deliverable. Whenever a
Zeus version is released, the manual MUST be updated in the same release work:

1. **Edit the affected chapters** in `chapters/` for every operator-visible change
   in the release — new panels/controls, changed defaults or behaviour, new
   settings tabs, renamed features. Cross-check against `CHANGELOG.md` for the
   release and against the live UI (settings tabs, the panel catalog, the `/api`
   surface) so nothing is missed. Move anything that was documented as
   "experimental/opt-in" into its normal chapter once it ships as default.
2. **Tag first.** The cover names the release from the git tag automatically —
   there is no edition to bump by hand. (It used to be a hard-coded string that
   stayed at 1.43 for months.)
3. **Regenerate the PDF** with `./build.sh` and attach it to the release (and
   refresh the copy on the maintainer's Desktop).

This mirrors the other per-release hard rules (bump the About section + CHANGELOG,
pin the release announcement issue). It is a documented process step, not an
automated trigger: a Zeus release is a git/GitHub event, not something a local
editor tool can hook, so the manual is regenerated as part of the release
checklist. (If fully-automatic regeneration is ever wanted, add a step to the
release CI workflow that runs `docs/manual/build.sh` and uploads the PDF as a
release asset.)
