# JSON Specs, Markdown Authoring, Patching & `.mbypkg` v2.0

`mby` organizes schemas into **Session & Quiz Content Specs** (`@content`: containers, article blocks, quiz questions) and **Other Specs** (`@other`: authoring formats, layouts, patching, batching, packaging, building, analysis).

## Strict JSON Rules

- UTF-8 JSON with camelCase keys; unrecognized properties fail with exit `2` (`Json.ParseError` + typo `hint` when applicable).
- `imagePath` fields must be relative paths confined to the input directory/workspace root (including safe nested `../assets/...` references within the spec root), validated via image magic bytes (`.png`, `.jpg`, `.jpeg`, `.gif`, `.webp`, `.bmp`, `.svg`) and copied to `<workspace>/assets/` with a content-hash suffix (`assets/<stem>_<hash8>.<ext>`).
- Optional `ref` slugs (`^[a-z0-9][a-z0-9_-]{0,31}$`) must be unique within a workspace; omitted component `ref` values auto-generate deterministic slugs from `title`/filename. Explicit invalid `ref` values fail strictly with `Address.InvalidRef` + suggested valid slug.

---

## 1. Session & Quiz Content Specs (`@content`)

### Markdown + Frontmatter & Modular Directories (`mby spec markdown`)

Pass a `.md` file or directory (`./articles/`) directly to `mby apply` or `mby build`:
- **YAML Frontmatter:** `kind` (`article` | `quiz` | `mcq` | `flashcard` | `mixed` | `session`), `quizType` (`mcq` | `flashcard` | `mixed`, defaults to `mixed`), `title`, `ref`, `name`, `description`, `order`, `layoutOverrides`.
- **Modular Directories:** Combine frontmatter-only `_session.md` (`kind` omitted or `session`, no body content after closing `---`) or `session.json` + naturally sorted `*.md` and `*.json` component files (`*.masks.json` and `*.meta.json` sidecars are ignored).
- **Article Directives:**
  - Standalone image `![alt](assets/fig_2.1.png "Caption")` → `image` block (`<figure><figcaption>`, stored as `assets/<stem>_<hash8>.<ext>`).
  - Callout fences `:::callout{ref?="slug" tone="clinical" title="Clinical Correlation"} ... :::` (or `:::pearl`, `:::clinical`, `:::warning`) → `callout` block.
  - Interactive table fence `:::table{ref?="slug" title="Loop (\"High-Ceiling\") Diuretics" caption?="..." stickyFirstCol?=true hideableCols?="2,3"} ... :::footnote ... :::` → `table` block (`hideableCols` uses **1-based** column indices `1..headers.length`; supports `\"` / `\'` escaped quotes and KaTeX in titles/headers/cells).
  - Native link cards & links: `:::link{id="slug" title?="..." image?="assets/fig.png"} ... :::` referenced via `[text](mby://card/<id>)`, plus intra-session `[art](mby://article/<ref>)` and cross-session `[ext](mby://session/<Session_Id>/<ref>)` links.
  - Interactive diagram explorer fence `:::diagram-explore{ref?="slug" image="assets/fig.png" masks?="assets/fig.masks.json" title?="..." caption?="..."}` with `- Label: Annotation Markdown` bullets (matched case-insensitively against sidecar boxes) or inline YAML `boxes:` (`label`, `rect`, `note`).
  - Progressive step-through reveal fence `:::reveal{ref?="slug" title?="..." showFirst?=false}` using sequential `> Step Title` cards with Markdown/KaTeX bodies.
- **Quiz Directives (`kind: quiz` or `quizType: ...`):**
  - `:::mcq{ref?="slug" multi?=false maxOptions?=auto image?="path" explanation?="..."}` with `- [x]` / `- [ ]` options and optional `:::explanation` (Markdown auto-sizes `maxOptions` to the option count; JSON auto-expands `maxOptions` up to `26` when omitted and `>4` options are supplied, keeping default `4` for `<=4`).
  - `:::flashcard{ref?="slug" frontPosition?="belowImage"}` with `:::front` / `:::back` and optional `:::layout`.
  - `:::fill-blank{ref?="slug" fuzzy?=true caseSensitive?=false placeholder?="..." explanation?="..."}` using `{{primary | alias}}` slots (preserves `$...$` math and `` `...` `` inline code spans; supports `:::explanation`).
  - `:::assertion-reason{ref?="slug" answer="both-explain"|"both-no-explain"|"a-true-r-false"|"a-false-r-true"|"both-false" scheme?="4opt"|"5opt" explanation?="..."}` with `A:` and `R:` statements (`scheme` defaults to `5opt` when `answer` is `both-false`, else `4opt`; explicit `4opt` with `both-false` fails lint).
  - `:::spot-error{ref?="slug" image?="assets/fig.png" requireFix?=false allowNoError?=false noError?=false explanation?="..."}` with `[candidate]` and `[~~wrong~~ -> correction | alias]` spans (`allowNoError` defaults to `false`, flipping to `true` when `noError=true`; `\\[...]` renders literal `\` before `[...]`).
  - `:::diff{ref?="slug" mode="fill"|"spot-wrong"|"rearrange" title?="..." bank?=false fuzzy?=true caseSensitive?=false allowNoError?=false explanation?="..."}` with a Markdown table using `{{ans | alias}}` blanks (`fill`) or `~~wrong~~ -> fix \| alias` / `~~wrong~~ -> {{fix | alias}}` cells (`spot-wrong`), plus optional `:::explanation`.
  - `:::mechanism{ref?="slug" title?="..." explanation?="..."}` with numbered ordered steps (`1. ...`), commutative parallel groups (`2a. ...`, `2b. ...`), and optional decoy steps (`! ...`), plus optional `:::explanation`.
  - `:::relabel{ref?="slug" image="assets/fig.png" masks?="assets/fig.masks.json" mode?="bank"|"reveal"|"recall" style?="solid"|"blur"|"pixelate"|"whiteout" fuzzy?=true caseSensitive?=false explanation?="..."}` with YAML body (`prompt`, `include`, `distractors`, inline `boxes` with `label`, `aliases`, `rect: [x,y,w,h]` or `x/y/w/h`) and optional `:::explanation`.
  - `:::classify{ref?="slug" title?="..." explanation?="..."}` with `[Bucket Name]` headers and `- Item` bullets.
  - `:::match{ref?="slug" title?="..." explanation?="..."}` with `- Left :: Right` pair lines and optional `! Distractor` lines.
  - `:::numeric{ref?="slug" answer=125 tolerance?="5%" toleranceMode?="absolute"|"percent" unit?="mL/min" placeholder?="..." image?="..." explanation?="..."}` with prompt body and optional standalone image.
  - `:::case{ref?="slug" title?="..." image?="..." caption?="..." explanation?="..."}` with shared vignette stem + 2 or more quad-colon child fences (`::::mcq`, `::::fill-blank`, `::::numeric`, etc.; supports `::::explanation`/`::::front`/`::::back`/`::::layout` and `explanation="..."` attribute shorthand; nested `::::case` is forbidden).

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
        { "kind": "text", "text": "# Kinematics\n\n$$v = u + at$$\n\nSee [acceleration card](mby://card/accel)." },
        { "kind": "image", "imagePath": "images/velocity.png", "caption": "Velocity vs time", "alt": "Velocity graph" },
        { "kind": "callout", "tone": "pearl", "title": "Key Formula", "text": "Area under $v(t)$ equals displacement." },
        {
          "kind": "table",
          "title": "Variables",
          "stickyFirstCol": true,
          "hideableCols": [2],
          "headers": ["Symbol", "Meaning"],
          "alignments": ["left", "left"],
          "rows": [["$v$", "Final velocity"]]
        }
      ],
      "links": [
        { "id": "accel", "title": "Acceleration", "text": "Rate of change of velocity ($m/s^2$)." }
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
          "fuzzy": true,
          "placeholder": "Enzyme name"
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
        },
        {
          "type": "diff",
          "mode": "spot-wrong",
          "title": "Gram-Positive vs Gram-Negative",
          "headers": ["Property", "Gram-Positive", "Gram-Negative"],
          "rows": [["Outer membrane", "~~Present~~ -> Absent | None", "Present"]]
        },
        {
          "type": "mechanism",
          "title": "RAAS Activation",
          "steps": [
            { "orderGroup": 1, "text": "Renin cleaves angiotensinogen to Angiotensin I" },
            { "orderGroup": 2, "text": "ACE converts Angiotensin I to Angiotensin II" },
            { "isDistractor": true, "text": "ANP converts Angiotensin I to Angiotensin II" }
          ]
        },
        {
          "type": "relabel",
          "imagePath": "images/nephron.png",
          "mode": "bank",
          "style": "blur",
          "boxes": [
            { "label": "Glomerulus", "rect": [0.45, 0.15, 0.14, 0.05] }
          ]
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
  - `table`: `{ title?, caption?, stickyFirstCol?, hideableCols?, headers?, alignments?, rows?, footnote?, ref? }` *(1-based `hideableCols`; row widths and `alignments` are validated immediately against `headers.length`)*
- `question`:
  - `mcq`: `{ text?, imagePath?, explanation?, isMultiCorrect?, maxOptions?, options?, ref?, layoutOverrides? }` *(`options` requires `2..26` items on `patch`; omitting `maxOptions` auto-expands up to `26` when `>4` options are passed)*
  - `flashcard`: `{ front?: { text?, imagePath?, position? }, back?: { ... }, ref?, layoutOverrides? }`
  - `fill-blank`: `{ text?, fuzzy?, caseSensitive?, placeholder?, explanation?, ref?, layoutOverrides? }`
  - `assertion-reason`: `{ assertion?, reason?, answer?, scheme?, explanation?, ref?, layoutOverrides? }`
  - `spot-error`: `{ text?, imagePath?, requireFix?, allowNoError?, noError?, explanation?, ref?, layoutOverrides? }`
  - `diff`: `{ mode?, title?, bank?, fuzzy?, caseSensitive?, allowNoError?, headers?, alignments?, rows?, explanation?, ref?, layoutOverrides? }`
  - `mechanism`: `{ title?, steps?, explanation?, ref?, layoutOverrides? }`
  - `relabel`: `{ imagePath?, prompt?, mode?, style?, boxes?, distractors?, fuzzy?, caseSensitive?, explanation?, ref?, layoutOverrides? }`
- `--set <k=v>` converts `null` to JSON `null` (`hideableCols=null` clears to `[]`), clearing nullable fields/overrides and rejecting non-nullable fields with `Patch.InvalidValue`. An empty `{}` patch without `--set` or `--move` fails with `Patch.Empty`. Pass `--move <index>:<shift|exchange>` to reorder.

### `.mbypkg` Package Format (`v2.0` — Backward-Compatible `v1.0`/`v2.0` Import)

ZIP archive produced after mandatory `check`:
- `manifest.json`: `packageVersion` (`"2.0"`), `kind` (`session`\|`quiz`), `id`, `exportedAtUtc`, `assets[]`, `targetPlatform` (`desktop`\|`android`)
- `workspace.json`: serialized components/blocks/questions (including article `links` and `payloadJson` on `table`, `fill-blank`, `assertion-reason`, `spot-error`, `diff`, `mechanism`, and `relabel` items; omits `layoutOverrides` unless `--include-layout` is passed)
- `layouts.json`: global profiles (included only with `--include-layout`)
- `assets/<filename>`: deduped verified media files

`pkg export` defaults to `<projectRoot>/<wsId>.mbypkg` for workspaces under `.meloberry/workspaces/` (or `<workspaceParent>/<wsId>.mbypkg` otherwise) when `[outPath]` is omitted; `[outPath]` resolves as a directory if it exists or ends with `/` or `\`, and as a `.mbypkg` file path otherwise. `pkg import` supports both `v1.0` and `v2.0` packages (stripping UTF-8 BOM if present), defaults to CWD when `[destDir]` is omitted, validates payload schemas, enforces single-segment IDs and ZIP entry confinement, and caps decompression at `50 MB` per-entry / `250 MB` total via atomic staging.
