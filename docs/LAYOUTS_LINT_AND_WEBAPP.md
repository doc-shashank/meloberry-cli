# Layouts, Lint & WebApp

## Layout System & Bounds

Three global profiles (`article`, `mcq`, `flashcard`) persist in `<app-data>/app_settings.db`. Per-component and per-question `layoutOverrides` store in the workspace SQLite DB. `themeMode` and `slideTransitionMs` synchronize across all three profiles.

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

## Generated WebApp, Search & Headless Verification

`build` emits `index.html`, `styles.css`, `app.js`, `assets/`, and offline `katex/` assets when math is detected (including captions & callouts). Articles render semantic `<figure>/<figcaption>` pairs and `pearl`/`clinical`/`warning` callout cards, plus client-side `Ctrl+K` / `/` full-text search across all slides and quiz questions.
- `mby analyze <webDir> --headless` (or `--static-only` without a browser) runs zero-touch DOM/image/overflow/KaTeX/search/console verification and writes `reports/verify.latest.json`.
- `mby analyze <webDir> --live` captures interactive `POST /api/analysis` telemetry into `<webDir>/reports/*.analysis.json`.
