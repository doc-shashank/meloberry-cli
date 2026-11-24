# Layouts, Lint & WebApp

## Layout System & Bounds

Three global profiles (`article`, `mcq`, `flashcard`) persist in `<app-data>/app_settings.db`. Per-component and per-question `layoutOverrides` store in the workspace SQLite DB. `themeMode` and `slideTransitionMs` synchronize across all three profiles.
- **Profile inheritance:** `table` article blocks inherit the `article` profile; `fill-blank`, `assertion-reason`, and `spot-error` questions inherit the `mcq` profile.

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
| `Lint.EmptyQuestionPrompt` | Error | MCQ prompt text is missing |
| `Lint.InsufficientOptions` | Error | MCQ has `< 2` populated options |
| `Lint.UnfilledOptionSlot` | Warning | MCQ has fewer populated options than `maxOptions` |
| `Lint.NoCorrectOption` | Error | MCQ has zero options marked `isCorrect` |
| `Lint.MultipleCorrectInSingle` | Error | Single-select MCQ has `> 1` correct options |
| `Lint.EmptyOption` | Error | MCQ option has neither text nor image |
| `Lint.EmptyFlashcardSide` | Error | Flashcard front or back face has neither text nor image |
| `Lint.BrokenAssetDirectory` | Error | Workspace or `assets/` directory is missing |
| `Lint.BrokenImageFile` | Error | Referenced image is missing, escapes workspace, or fails magic-byte check |
| `Lint.InvalidTablePayload` | Error | Table block has missing/invalid JSON, empty headers/rows, mismatched row widths, or invalid alignment |
| `Lint.InvalidTableHideableCol` | Error | Table `hideableCols` contains a duplicate or out-of-bounds (`1..headers.length`) index |
| `Lint.InvalidQuestionPayload` | Error | Payload question (`fill-blank`, `assertion-reason`, `spot-error`) has missing/corrupted/out-of-sync `payloadJson` |
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

## Generated WebApp, Search & Headless Verification

`build` emits `index.html`, `styles.css`, `app.js`, `assets/`, and offline `katex/` assets whenever math is detected (including captions, callouts, table titles/headers/cells/footnotes, blank answers, and spot-error corrections).
- **Articles:** Render semantic `<figure>/<figcaption>` pairs, `pearl`/`clinical`/`warning` callout cards, and interactive study tables (`stickyFirstCol`, `hideableCols` blur/peek recall toggles).
- **Interactive Recall & Quizzes:** Supports MCQ, 3D flashcards, `fill-blank` (case/whitespace/Greek normalization, Levenshtein fuzzy tolerance, and `"Override: Mark Correct"`), `assertion-reason`, and `spot-error` (tappable chips, optional `requireFix` input with `Enter` submit, and `"No error"` toggle).
- **Search & Telemetry:** Client-side `Ctrl+K` / `/` full-text search indexes all article blocks, tables, and quiz questions.
- `mby analyze <webDir> --headless` (or `--static-only` without a browser) runs zero-touch DOM/image/overflow/KaTeX/table-toggle/search/console verification and writes `reports/verify.latest.json`.
- `mby analyze <webDir> --live` captures interactive `POST /api/analysis` telemetry into `<webDir>/reports/*.analysis.json`.
