# JSON Specs, Markdown Authoring, Patching & `.mbypkg` v2.0

`mby` organizes schemas into **Session & Quiz Content Specs** (`@content`: containers, article blocks, quiz questions) and **Other Specs** (`@other`: authoring formats, layouts, patching, batching, packaging, building, analysis).

## Strict JSON Rules

- UTF-8 JSON with camelCase keys; unrecognized properties fail with exit `2` (`Json.ParseError` + typo `hint` when applicable).
- `imagePath` fields must be relative paths confined to the input directory/workspace root (including safe nested `../assets/...` references within the spec root), validated via image magic bytes (`.png`, `.jpg`, `.jpeg`, `.gif`, `.webp`, `.bmp`, `.svg`) and copied to `<workspace>/assets/`.
- Optional `ref` slugs (`^[a-z0-9][a-z0-9_-]{0,31}$`) must be unique within a workspace; omitted component `ref` values auto-generate deterministic slugs from `title`/filename. Explicit invalid `ref` values fail strictly with `Address.InvalidRef` + suggested valid slug.

---

## 1. Session & Quiz Content Specs (`@content`)

### Markdown + Frontmatter & Modular Directories (`mby spec markdown`)

Pass a `.md` file or directory (`./articles/`) directly to `mby apply` or `mby build`:
- **YAML Frontmatter:** `kind` (`article` | `quiz` | `mcq` | `flashcard` | `mixed` | `session`), `quizType` (`mcq` | `flashcard` | `mixed`, defaults to `mixed`), `title`, `ref`, `name`, `description`, `order`, `layoutOverrides`.
- **Modular Directories:** Combine frontmatter-only `_session.md` (`kind` omitted or `session`, no body lines) or `session.json` + naturally sorted `*.md` and `*.json` component files (`*.masks.json` and `*.meta.json` sidecars are ignored).
- **Article Directives:**
  - Standalone image `![alt](assets/fig_2.1.png "Caption")` → `image` block (`<figure><figcaption>`).
  - Callout fences `:::callout{ref?="slug" tone="clinical" title="Clinical Correlation"} ... :::` (or `:::pearl`, `:::clinical`, `:::warning`) → `callout` block.
  - Interactive table fence `:::table{ref?="slug" title="Loop (\"High-Ceiling\") Diuretics" caption?="..." stickyFirstCol?=true hideableCols?="2,3"} ... :::footnote ... :::` → `table` block (supports `\"` / `\'` escaped quotes and KaTeX in titles/headers/cells).
- **Quiz Directives (`kind: quiz` or `quizType: ...`):**
  - `:::mcq{ref?="slug" multi?=false maxOptions?=4 image?="path"}` with `- [x]` / `- [ ]` options and optional `:::explanation`.
  - `:::flashcard{ref?="slug" frontPosition?="belowImage"}` with `:::front` / `:::back` and optional `:::layout`.
  - `:::fill-blank{ref?="slug" fuzzy?=true caseSensitive?=false}` using `{{primary | alias}}` slots (preserves `$...$` math and `` `...` `` inline code spans; supports `:::explanation`).
  - `:::assertion-reason{ref?="slug" answer="both-explain"|"both-no-explain"|"a-true-r-false"|"a-false-r-true"|"both-false" scheme?="4opt"|"5opt"}` with `A:` and `R:` statements (`both-false` selects `5opt`).
  - `:::spot-error{ref?="slug" requireFix?=false allowNoError?=true noError?=false}` with `[candidate]` and `[~~wrong~~ -> correction | alias]` spans (`\\[...]` renders literal `\` before `[...]`).

### Declarative Session JSON Spec (`apply` / `build`)

```json
{
  "kind": "session",
  "name": "Physics: Kinematics",
  "description": "Motion and acceleration review",
  "components": [
    {
      "kind": "article",
      "ref": "intro",
      "title": "Equations of Motion",
      "blocks": [
        { "kind": "text", "text": "# Kinematics\n\n$$v = u + at$$" },
        { "kind": "image", "imagePath": "images/velocity.png", "caption": "Velocity vs time", "alt": "Velocity graph" },
        { "kind": "callout", "tone": "pearl", "title": "Key Formula", "text": "Area under $v(t)$ equals displacement." },
        {
          "kind": "table",
          "title": "Variables",
          "stickyFirstCol": true,
          "hideableCols": [2],
          "headers": ["Symbol", "Meaning"],
          "rows": [["$v$", "Final velocity"]]
        }
      ]
    },
    {
      "kind": "quiz",
      "ref": "check",
      "title": "Quick Check",
      "quizType": "mixed",
      "questions": [
        {
          "type": "mcq",
          "text": "SI unit of acceleration?",
          "imagePath": "images/accel.png",
          "explanation": "Velocity changes in $m/s$ per second.",
          "isMultiCorrect": false,
          "maxOptions": 4,
          "options": [
            { "text": "$m/s^2$", "isCorrect": true },
            { "text": "$m/s$", "isCorrect": false }
          ]
        },
        {
          "type": "flashcard",
          "front": { "text": "Binary search complexity?", "position": "belowImage" },
          "back": { "text": "$O(\\log n)$" }
        },
        {
          "type": "fill-blank",
          "text": "Rate-limiting enzyme of glycolysis: {{phosphofructokinase-1 | PFK-1}}.",
          "fuzzy": true
        },
        {
          "type": "assertion-reason",
          "assertion": "Aspirin is avoided in children with viral infections.",
          "reason": "Aspirin irreversibly acetylates COX-1 and COX-2.",
          "answer": "both-no-explain",
          "scheme": "4opt"
        },
        {
          "type": "spot-error",
          "text": "In [primary hyperparathyroidism], calcium is [elevated] and phosphate is [~~elevated~~ -> decreased | low].",
          "allowNoError": true
        }
      ]
    }
  ]
}
```

---

## 2. Other Specs (`@other`: Patching & `.mbypkg` v2.0)

### Surgical Patch (`mby spec patch`)

- `session` / `quiz`: `{ name?, description? }`
- `component`: `{ title?, ref?, layoutOverrides? }`
- `articleBlock`:
  - `text`: `{ text?, ref? }`
  - `image`: `{ imagePath?, caption?, alt?, ref? }`
  - `callout`: `{ tone?, title?, text?, ref? }`
  - `table`: `{ title?, caption?, stickyFirstCol?, hideableCols?, headers?, alignments?, rows?, footnote?, ref? }` *(row widths and `alignments` are validated immediately against `headers.length`)*
- `question`:
  - `mcq`: `{ text?, imagePath?, explanation?, isMultiCorrect?, maxOptions?, options?, ref?, layoutOverrides? }`
  - `flashcard`: `{ front?: { text?, imagePath?, position? }, back?: { ... }, ref?, layoutOverrides? }`
  - `fill-blank`: `{ text?, fuzzy?, caseSensitive?, explanation?, ref?, layoutOverrides? }`
  - `assertion-reason`: `{ assertion?, reason?, answer?, scheme?, explanation?, ref?, layoutOverrides? }`
  - `spot-error`: `{ text?, requireFix?, allowNoError?, noError?, explanation?, ref?, layoutOverrides? }`
- Set any `layoutOverrides.<key>` to `null` to remove an override; pass `--move <index>:<shift|exchange>` to reorder.

### `.mbypkg` Package Format (`v2.0` — Backward-Compatible `v1.0`/`v2.0` Import)

ZIP archive produced after mandatory `check`:
- `manifest.json`: `packageVersion` (`"2.0"`), `kind` (`session`\|`quiz`), `id`, `exportedAtUtc`, `assets[]`, `targetPlatform` (`desktop`\|`android`)
- `workspace.json`: serialized components/blocks/questions, including `payloadJson` on `table`, `fill-blank`, `assertion-reason`, and `spot-error` items (omits `layoutOverrides` unless `--include-layout` is passed)
- `layouts.json`: global profiles (included only with `--include-layout`)
- `assets/<filename>`: deduped verified media files

`pkg export` defaults to `<projectRoot>/<wsId>.mbypkg` for workspaces under `.meloberry/workspaces/` (or `<workspaceParent>/<wsId>.mbypkg` otherwise) when `[outPath]` is omitted. `pkg import` supports both `v1.0` and `v2.0` packages (stripping UTF-8 BOM if present), defaults to CWD when `[destDir]` is omitted, validates payload schemas, enforces single-segment IDs and ZIP entry confinement, and caps decompression at `50 MB` per-entry / `250 MB` total via atomic staging.
