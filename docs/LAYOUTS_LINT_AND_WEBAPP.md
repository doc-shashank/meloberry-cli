# Layouts, Lint & WebApp

## Layout System & Bounds

Three global profiles (`article`, `mcq`, `flashcard`) persist in `<app-data>/app_settings.db`. Per-component and per-question `layoutOverrides` store in the workspace SQLite DB. `themeMode` and `slideTransitionMs` are **deck-global only**: they synchronize across all three profiles via `mby layout set` / `mby layout reset` / `mby layout edit` and cannot be set on per-component or per-question `layoutOverrides`.
- **Profile inheritance:** `table`, `callout`, `diagram-explore`, and `reveal` article blocks inherit the `article` profile; `fill-blank`, `assertion-reason`, `spot-error`, `diff`, `mechanism`, `relabel`, `classify`, `match`, `numeric`, and `case` questions inherit the `mcq` profile.
- **CLI & GUI layout management:** Use `mby layout get [profile]`, `mby layout set <profile> <prop> <val>`, `mby layout reset [profile] [prop...]`, or `mby layout edit [target]` (supporting `article-text`, `callout`, `table`, `image`, `mcq-question`, `mcq-options`, `flashcard`, `fill-blank`, `assertion-reason`, `spot-error`, `diff`, `mechanism`, `relabel`).

| Property | Profiles | Bounds / Enum | Default |
| :--- | :--- | :--- | :--- |
| `cardMaxWidthPx` | `article`, `mcq`, `flashcard` | `480..1400` px | `840` |
| `cardPaddingPx` | `article`, `mcq`, `flashcard` | `12..64` px | `32` |
| `promptFontSizePx` | `article`, `mcq`, `flashcard` | `14..42` px | `22` |
| `bodyFontSizePx` | `article`, `mcq`, `flashcard` | `12..28` px | `16` |
| `imageMaxHeightPx` | `article`, `mcq`, `flashcard` | `120..800` px | `360` |
| `contentAlign` | `article`, `mcq`, `flashcard` | `left`, `center`, `right` | `left` |
| `themeMode` | `article`, `mcq`, `flashcard` | `berry`, `light`, `dark`, `paper` | `berry` |
| `slideTransitionMs` | `article`, `mcq`, `flashcard` | `100..1200` ms | `380` |
| `blockSpacingPx` | `article` | `8..48` px | `20` |
| `optionColumns` | `mcq` | `1..2` | `1` |
| `optionGapPx` | `mcq` | `6..32` px | `12` |
| `flipDurationMs` | `flashcard` | `150..1000` ms | `450` |
| `defaultTextPosition` | `flashcard` | `belowImage`, `aboveImage` | `belowImage` |

## Mandatory Lint (`check`)

`build`, `apply --build`, `apply --export`, and `pkg export` run `check` automatically and abort with exit `2` (`Lint.Failed`) on errors.

| Code | Severity | Condition |
| :--- | :--- | :--- |
| `Lint.EmptySession` | Error | Session has zero components |
| `Lint.EmptyComponent` | Error | Article has zero blocks or Quiz has zero questions |
| `Lint.EmptyArticleBlock` | Error | Article text or callout block is empty |
| `Lint.QuestionTypeMismatch` | Error | Quiz contains a question whose type conflicts with quiz type (`mcq`/`flashcard`) |
| `Lint.EmptyQuestionPrompt` | Error | MCQ, `fill-blank`, or `spot-error` prompt text is missing |
| `Lint.InsufficientOptions` | Error | MCQ has `< 2` populated options |
| `Lint.UnfilledOptionSlot` | Warning | MCQ has fewer populated options than `maxOptions` |
| `Lint.NoCorrectOption` | Error | MCQ has zero options marked `isCorrect` |
| `Lint.MultipleCorrectInSingle` | Error | Single-select MCQ has `> 1` correct options |
| `Lint.EmptyOption` | Error | MCQ option has neither text nor image |
| `Lint.EmptyFlashcardSide` | Error | Flashcard front or back face has neither text nor image |
| `Lint.BrokenAssetDirectory` | Error | Workspace or `assets/` directory is missing |
| `Lint.BrokenImageFile` | Error | Referenced image is missing, escapes workspace, or fails magic-byte check |
| `Lint.MissingLinkCard` | Error | Article references `[text](mby://card/<id>)` with no matching `:::link` card |
| `Lint.UnresolvedArticleLink` | Error | Article references `[text](mby://article/<ref>)` not matching any article in the session |
| `Lint.UnresolvedSessionLink` | Warning | Cross-session `mby://session/<id>[/<ref>]` target is not yet indexed |
| `Lint.InvalidTablePayload` | Error | Table block has missing/invalid JSON, empty headers/rows, mismatched row widths, or invalid alignment |
| `Lint.InvalidTableHideableCol` | Error | Table `hideableCols` contains a duplicate or out-of-bounds (`1..headers.length`) index |
| `Lint.InvalidQuestionPayload` | Error | Payload question (`fill-blank`, `assertion-reason`, `spot-error`, `diff`, `mechanism`, `relabel`) has missing/corrupted/out-of-sync `payloadJson` |
| `Lint.UnbalancedBlankDelimiters` | Error | `fill-blank` template contains unclosed `{{`, nested `{{`, or unmatched `}}` |
| `Lint.NoFillBlanks` | Error | `fill-blank` template contains zero `{{answer}}` blank slots |
| `Lint.EmptyBlankAnswer` | Error | `fill-blank` blank slot or pipe alias is empty |
| `Lint.EmptyAssertionOrReason` | Error | `assertion-reason` has empty Assertion (A) or Reason (R) |
| `Lint.InvalidAssertionReasonAnswer` | Error | `assertion-reason` `answer` is not a supported enum (`both-explain` \| `both-no-explain` \| `a-true-r-false` \| `a-false-r-true` \| `both-false`) |
| `Lint.InvalidAssertionReasonScheme` | Error | `assertion-reason` `scheme` is invalid or `both-false` is paired with `4opt` |
| `Lint.InsufficientSpotErrorSpans` | Error | `spot-error` statement has `< 2` `[span]` candidate segments |
| `Lint.SpotErrorEmptyCorrection` | Error | `spot-error` has empty `[]` span or malformed/empty `[~~wrong~~ -> correction]` |
| `Lint.SpotErrorConflictingNoError` | Error | `spot-error` sets `noError=true` with error spans present or with `allowNoError=false` |
| `Lint.SpotErrorMissingFault` | Error | `spot-error` has zero `[~~wrong~~ -> right]` error spans and `noError=false` |
| `Lint.InvalidDiffMode` | Error | `diff` `mode` is not `fill`, `spot-wrong`, or `rearrange` |
| `Lint.InvalidDiffTable` | Error | `diff` table has `< 2` headers (`< 3` for `rearrange`), invalid alignments, empty rows, or mismatched row width |
| `Lint.DiffMissingBlanks` | Error | `diff` (`mode="fill"`) has zero `{{answer}}` blank cells or malformed cell syntax |
| `Lint.DiffMissingFaults` | Error | `diff` (`mode="spot-wrong"`) has zero fault cells with `allowNoError=false` or malformed fault syntax |
| `Lint.InsufficientMechanismSteps` | Error | `mechanism` has `< 2` ordered non-distractor steps |
| `Lint.EmptyMechanismStep` | Error | `mechanism` contains a step with empty text |
| `Lint.DuplicateMechanismStep` | Error | `mechanism` contains duplicate step text |
| `Lint.ExcessiveMechanismDistractors` | Warning | `mechanism` has more distractor steps than ordered steps |
| `Lint.InvalidRelabelMode` | Error | `relabel` `mode` is not `reveal`, `bank`, or `recall` |
| `Lint.InvalidRelabelStyle` | Error | `relabel` `style` is not `solid`, `blur`, `pixelate`, or `whiteout` |
| `Lint.EmptyRelabelBoxes` | Error | `relabel` defines zero overlay boxes |
| `Lint.EmptyRelabelLabel` | Error | `relabel` overlay box has an empty label |
| `Lint.InvalidRelabelBoxBounds` | Error | `relabel` box coordinates fall outside `[0..1]` or exceed `x+w <= 1` / `y+h <= 1` |
| `Lint.EmptyDiagramHotspots` | Error | `diagram-explore` block defines zero annotated hotspots |
| `Lint.EmptyDiagramHotspotLabel` / `Note` | Error | `diagram-explore` hotspot has an empty label or annotation note |
| `Lint.InvalidDiagramHotspotBounds` | Error | `diagram-explore` hotspot falls outside `[0..1]` or exceeds `x+w <= 1` / `y+h <= 1` |
| `Lint.EmptyRevealSteps` / `Step` | Error | `reveal` block has zero steps or a step with empty heading/body |
| `Lint.InsufficientClassifyBuckets` | Error | `classify` question has `< 2` buckets |
| `Lint.DuplicateClassifyBucket` / `Item` | Error | `classify` has duplicate bucket names or duplicate items |
| `Lint.EmptyClassifyBucket` / `Item` | Error | `classify` has an empty item or a bucket with `0` assigned items |
| `Lint.InsufficientMatchPairs` | Error | `match` question has `< 2` `Left :: Right` pairs |
| `Lint.EmptyMatchItem` / `DuplicateMatchItem` | Error | `match` has empty or duplicate left/right/distractor entries |
| `Lint.ExcessiveMatchDistractors` | Warning | `match` has more distractors than matching pairs |
| `Lint.InvalidNumericAnswer` / `Tolerance` | Error | `numeric` answer is not finite or tolerance is negative/non-finite |
| `Lint.EmptyCaseStem` | Error | `case` vignette stem text is empty |
| `Lint.InsufficientCaseQuestions` | Error | `case` has `< 2` sub-questions |
| `Lint.NestedCaseNotAllowed` | Error | `case` contains a nested `case` sub-question |

## Generated WebApp, Search & Headless Verification

`build` emits `index.html`, `styles.css` (active theme only), `app.js`, tree-shaken `components/{aux,blocks,questions}/` modules for only the block/question specs used by the workspace, `assets/`, `preview-index.{json,js}` (for sessions), and offline `katex/` assets whenever math is detected (including captions, callouts, table titles/headers/cells/footnotes, blank answers, and spot-error corrections).
- **Articles:** Render semantic `<figure>/<figcaption>` pairs, `pearl`/`clinical`/`warning` callout cards, interactive study tables (`stickyFirstCol`, `hideableCols` blur/peek recall toggles), and native `mby://` link cards / cross-article navigation.
- **Interactive Recall & Quizzes:** Supports MCQ, 3D flashcards, `fill-blank` (custom `placeholder`, case/whitespace/Greek normalization, Levenshtein fuzzy tolerance, and `"Override: Mark Correct"`), `assertion-reason`, `spot-error` (optional prompt `image`, tappable chips, optional `requireFix` input with `Enter` submit, and `"No error"` toggle), `diff` (`fill` with optional word bank, `spot-wrong`, `rearrange`), `mechanism` (parallel `orderGroup` sequencing + decoys), and `relabel` (`reveal`, `bank`, `recall` occlusion over an unmasked diagram).
- **Search & Telemetry:** Client-side `Ctrl+K` / `/` full-text search indexes all article blocks, tables, and quiz questions.
- `mby analyze <webDir> --headless` (or `--static-only` without a browser) runs zero-touch DOM/image/overflow/KaTeX/table-toggle/search/console verification and writes `reports/verify.latest.json`.
- `mby analyze <webDir> --live` captures interactive `POST /api/analysis` telemetry into `<webDir>/reports/*.analysis.json`.
