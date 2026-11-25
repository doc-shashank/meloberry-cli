# Detailed Optimization Plan — Meloberry CLI (`mby` v2.1.0)

This plan targets all **9 prioritized optimizations** plus the **layout deduplication & inheritance fix** across the WebApp compiler, browser runtime, SQLite persistence, transaction manager, and CLI startup path.

All phases preserve **100% wire, CLI, SQLite, and `.mbypkg` v1.0/v2.0 compatibility**, satisfy `CleanCliDependencyRuleTests`, and are designed as independent, `dotnet test`-gated commits.

---

## Phase 1 — Payload Serialization & Layout Fix (Fixes #1 & #2)
**Goal:** Eliminate per-instance JSON schema bloat (`null` skeletons, duplicated `layout` dictionaries, repeated `assertion-reason` options, unused `rowsRaw`, redundant `titleHtml`).

### Concrete Changes

1. **Omit `null` fields in WebApp `DATA` (`SlidingWebAppGenerator.cs`)**
   * Update `JsonOptions`:
     ```csharp
     private static readonly JsonSerializerOptions JsonOptions = new()
     {
         PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
         DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
     };
     ```
   * Audit JS checks in `BuildJs()` to ensure all optional property reads use truthiness or `?.` (already true for `slide.mcq`, `b.table`, `opt.correctPairId === pair.pairId`, etc.).

2. **Layout Fix: Root `DATA.profiles` + Sparse Per-Entity `layoutOverrides` (`SlidingWebAppGenerator.cs`, `WebAppSharedStyles.cs`, `LayoutBoundsCatalog.cs`)**
   * Add a helper `LayoutBoundsCatalog.StripGlobalOnlyProperties(...)` (or filter in `SlidingWebAppGenerator`) so deck-global keys (`themeMode`, `slideTransitionMs`) are emitted **only** at the root of `WebBundlePayload` and never inside profile maps or slide objects.
   * Add `Dictionary<string, Dictionary<string, string>> Profiles` to `WebBundlePayload` containing the 3 global profiles (`article`, `mcq`, `flashcard`) minus `themeMode`/`slideTransitionMs`.
   * Replace `SlideRenderModel.Layout` (full 10-key dictionary) with `Dictionary<string, string>? LayoutOverrides` (only emitting keys whose effective value differs from the base profile, or `null` when identical so `WhenWritingNull` drops it completely).
   * Preserve exact 3-tier merge precedence: `globalProfile[target] < componentOverrides < questionOverrides`.
     * For `CaseGroup` sub-questions, pass effective parent overrides filtered by `LayoutBoundsCatalog.IsValidPropertyForTarget(subTargetType, key)` so a `flashcard` inside a `case` inherits `"flashcard"` defaults while an `mcq` inherits `"mcq"` defaults.
     * Keep `BuildSideRenderModel` resolving `defaultTextPosition` against the effective `flashcard` layout on the C# side so `aboveImage`/`belowImage` behavior is unchanged.
   * In `WebAppSharedStyles.BuildCoreJs()` / `SlidingWebAppGenerator.BuildJs()`, add:
     ```js
     function resolveSlideLayout(slide, baseProfileKey) {
       const key = baseProfileKey || (slide.kind === 'article' ? 'article' : (slide.kind === 'flashcard' ? 'flashcard' : 'mcq'));
       return { ...(DATA.profiles?.[key] || {}), ...(slide.layoutOverrides || {}) };
     }
     ```
     Update `applySlideLayoutVars` call sites and `buildFlashcardPanel` (`flipDurationMs`) to read from `resolveSlideLayout(...)`. Verify `LayoutEditorPageBuilder.cs` continues passing direct profile dictionaries to `applySlideLayoutVars` unchanged.

3. **Deduplicate Static/Derived Spec Fields (`SlidingWebAppGenerator.cs`)**
   * **`AssertionReasonRenderModel`:** Remove `List<AssertionReasonOptionRenderModel> Options` from serialized `DATA` (keep `Scheme`: `"4opt"` | `"5opt"`). Define `AR_OPTIONS_4` and `AR_OPTIONS_5` once as JS constants in `BuildJs()`.
   * **`DifferentiationRenderModel`:** Set `RowsRaw = mode == "rearrange" ? rowsRaw : null` (omitted via `WhenWritingNull` for `fill` and `spot-wrong`). Omit `Alignments` when all columns are `"left"`.
   * **`ArticleBlockRenderModel` / `TableRenderModel`:** Omit `TitleHtml` when `TitleHtml` equals HTML-encoded plain `Title` (no Markdown/KaTeX); JS falls back to `title` automatically.

### Files Affected
* `src/Meloberry.Domain/Layouts/LayoutBoundsCatalog.cs` *(optional helper for non-global profile export)*
* `src/Meloberry.Infrastructure/Web/SlidingWebAppGenerator.cs`
* `src/Meloberry.Infrastructure/Web/WebAppSharedStyles.cs`
* `tests/Meloberry.UnitTests/LayoutAndWebAppTests.cs` *(assert root `profiles`, no `":null"` bloat in `DATA`, no repeated `themeMode` per slide, and override precedence)*

---

## Phase 2 — Tree-Shaking, Selective KaTeX Fonts, Minification & Clean Rebuilds (Fix #7)
**Goal:** Stop shipping unused widget code, unused CSS themes, unused KaTeX font families, and orphaned assets on rebuilds.

### Concrete Changes

1. **Single-Pass Deck Feature Scanner (`SlidingWebAppGenerator.cs`)**
   * Add `private sealed class WebBundleFeatures` computed once across all `slides` (recursing into `quiz.Questions` and `case.Questions`) and `linkCards`:
     * `HashSet<string> BlockKinds` (`text`, `image`, `callout`, `table`, `diagram-explore`, `reveal`)
     * `HashSet<string> QuestionKinds` (`mcq`, `flashcard`, `fill-blank`, `assertion-reason`, `spot-error`, `diff`, `mechanism`, `relabel`, `classify`, `match`, `numeric`, `case`)
     * `bool HasImages`, `bool HasLinks`, `bool HasMath`, `bool HasQuizBundle`
     * `HashSet<string> KatexFontFamilies` (scan math HTML/TeX for `\mathfrak`, `\mathcal`, `\mathscr`, `\mathtt`, `\mathsf`, `\mathbb`/`\textbb`/`\amsrm` + AMS symbols)

2. **Conditional CSS & Active-Theme-Only Output (`WebAppSharedStyles.cs`, `SlidingWebAppGenerator.cs`, `LayoutEditorPageBuilder.cs`)**
   * Refactor `WebAppSharedStyles.BuildCoreCss(string? activeTheme = null, IReadOnlySet<string>? usedComponents = null)`:
     * When `activeTheme` is provided (WebApp build), emit **only** the CSS custom properties for that theme into `:root` and omit the other 3 `html[data-theme="..."]` blocks.
     * When `activeTheme` is `null` (`LayoutEditorPageBuilder`), emit all 4 themes and all component styles as today.
     * Split widget-specific CSS (`callout`, `table`, `fill-blank`, `assertion-reason`, `spot-error`, `diff`, `mechanism`, `relabel`, `diagram-explore`, `reveal`, `classify`, `match`, `numeric`, `case`, `flashcard`, `links`, `zoom`) into keyed sections emitted only if present in `usedComponents`.

3. **Conditional JS Widget Bundling (`SlidingWebAppGenerator.cs`)**
   * Modularize `BuildJs(payloadJson, WebBundleFeatures features)` using a `StringBuilder`:
     * Always emit core shell (navigation, telemetry, search, finish screen, `DATA`).
     * Conditionally emit each `build*Card` / `build*Block` function only if that kind is in `features.QuestionKinds` / `features.BlockKinds`.
     * Conditionally emit shared helpers:
       * `GREEK_MAP` / `normalizeRecallAnswer` / `levenshtein` / `evaluateRecallMatch` → only if `fill-blank`, `spot-error`, `diff`, or `relabel` is used.
       * `deterministicShuffle` → only if `diff`, `mechanism`, or `relabel` is used.
       * `bindDropTarget` → only if `diff`, `mechanism`, `relabel`, `classify`, or `match` is used.
       * Link popover runtime + `initLinkPreviewCache` → only if `features.HasLinks`.
       * Zoom lightbox JS/HTML → only if `features.HasImages`.
       * `renderMathIn` → no-op stub if `!features.HasMath`.

4. **Selective KaTeX Font Copying & Stale Output Cleanup (`SlidingWebAppGenerator.cs`)**
   * In `CopyEmbeddedKatexAssetsAsync(outDir, features, ct)`:
     * Always copy `katex.min.css`, `katex.min.js`, `KaTeX_Main-*`, `KaTeX_Math-*`, `KaTeX_Size*`.
     * Conditionally copy `KaTeX_AMS-*`, `KaTeX_Caligraphic-*`, `KaTeX_Fraktur-*`, `KaTeX_SansSerif-*`, `KaTeX_Script-*`, `KaTeX_Typewriter-*` only when requested in `features.KatexFontFamilies`.
   * In `WriteWebAppBundleAsync`:
     * Delete stale files inside `outDir/assets/` not in the current `copiedAssets` set (never touch `outDir/reports/`).
     * If `!features.HasMath` and `outDir/katex` exists from an earlier build, delete `outDir/katex`.
     * If `features.HasMath`, prune any font files in `outDir/katex/fonts` not in the copied font set.

5. **Lightweight Minification & SVG `<symbol>` Sprite (`SlidingWebAppGenerator.cs`, `WebAppSharedStyles.cs`, `CdpHeadlessWebAppVerifier.cs`)**
   * Add a lightweight whitespace/comment minifier for emitted `styles.css` and the static JS shell, **preserving `const DATA = <json>;\n`** on line 1 so `CdpHeadlessWebAppVerifier.RunStaticCheckAsync` parses `DATA` reliably without changes (or harden `RunStaticCheckAsync` to parse via balanced brace/string scanning).
   * Emit reusable SVG icons (`zoom`, `eye`, `eye-off`, `save`, `github`) once as `<svg class="hidden"><defs><symbol ...>` in `index.html` and reference via `<use href="#mby-ico-..."/>`.
   * *(Optional breaking-contract note)*: `preview-index.json` duplicates `preview-index.js` byte-for-byte; either drop `preview-index.json` (updating the 2 assertions in `CliEndToEndWorkflowTests.cs`) or keep it only when external tools need static JSON.

### Files Affected
* `src/Meloberry.Infrastructure/Web/WebAppSharedStyles.cs`
* `src/Meloberry.Infrastructure/Web/SlidingWebAppGenerator.cs`
* `src/Meloberry.Infrastructure/Web/LayoutEditorPageBuilder.cs` *(confirm `BuildCoreCss(null, null)` call)*
* `src/Meloberry.Infrastructure/Services/CdpHeadlessWebAppVerifier.cs` *(keep `DATA` extraction resilient)*
* `tests/Meloberry.UnitTests/LayoutAndWebAppTests.cs` *(verify minimal article omits quiz builders, unused themes, and unused KaTeX fonts; verify stale assets/katex are pruned on rebuild)*
* `tests/Meloberry.CliTests/CliEndToEndWorkflowTests.cs` *(only if `preview-index.json` is removed)*

---

## Phase 3 — Browser Runtime Performance & Memory (Fixes #4 & #8)
**Goal:** Make initial page load near-instant on large multi-chapter decks, cut browser RAM, and eliminate click jank on KaTeX-heavy interactive widgets.

### Concrete Changes

1. **Lazy `SEARCH_INDEX`, Single Scratch Element & Lazy Link Excerpts (`SlidingWebAppGenerator.cs`)**
   * Replace `const SEARCH_INDEX = (() => { ... })();` with lazy `getSearchIndex()` initialized on first `openSearchModal()` (or weak-chip click on finish screen).
   * Reuse a single module-level `const _plainDiv = document.createElement('div');` inside `htmlToPlainText(html)` instead of allocating a new `<div>` on every table cell / block.
   * Compute `inSessionArticleCache` excerpts lazily on first hover in `showLinkPopover` for `kind === 'article'`.

2. **Precompiled Greek Symbol Regex (`SlidingWebAppGenerator.cs`)**
   * Replace the 52-iteration `new RegExp(sym, 'g')` loop in `normalizeRecallAnswer` with a static `GREEK_CHAR_MAP` object and a single precompiled character-class regex `GREEK_CHAR_RE` (~50× faster, zero per-call regex allocations).

3. **Lazy Off-Screen Slide & Question Hydration + Lazy Image Decoding (`SlidingWebAppGenerator.cs`, `WebAppSharedStyles.cs`)**
   * In `createZoomableImageNode` and diagram/relabel `<img>` creation, set `img.decoding = 'async'` and `img.loading = 'lazy'`.
   * In `renderSlides()`:
     * Create lightweight placeholder `<section class="mby-slide">` containers and dots without rendering inner DOM or invoking `katex.render()`.
     * Add `ensureSlideRendered(idx)` called from `goToSlide(idx)` (and optionally pre-render `idx + 1` via `requestIdleCallback`). Inside `ensureSlideRendered(idx)`, switch `img.loading = 'eager'` for images on the newly active slide so browser & CDP verification decode immediately.
   * Inside `buildBundledQuiz(quizSlide)`:
     * Hydrate `qSlides[qIdx]` lazily inside `selectQuestion(qIdx)`.
     * Observe **only** the active question card in `ResizeObserver` (`ro.disconnect(); ro.observe(activeCard);`) instead of observing every question slide simultaneously.

4. **In-Place DOM Updates for Interactive Widgets (`SlidingWebAppGenerator.cs`)**
   * **`buildMechanismCard`:** Cache each step's rendered content node (with KaTeX parsed once) in `stepDomMap`; move nodes between `bankEl` and `seqEl` and update badge numbers in place instead of wiping `innerHTML = ''` and re-running `renderMathIn`.
   * **`buildClassifyCard` & `buildMatchCard`:** Cache each item/option chip element once in a `Map`; on selection/placement/drag-drop, move the existing chip node or toggle `.selected` / `.used` / `.correct` / `.wrong` classes instead of destroying and rebuilding all chips.
   * **`buildDiffCard` (`rearrange` mode):** Swap the two affected button elements' content/order in place rather than rebuilding the entire `<tbody>` and re-parsing KaTeX on unchanged rows.
   * **`buildCaseGroupCard`:** Cache rendered child question widgets in `partNodeCache` (`Map<partIdx, HTMLElement>`) and toggle `.hidden` on tab clicks instead of re-invoking `renderSingleQuestionWidget()` from scratch on every tab switch.

### Files Affected
* `src/Meloberry.Infrastructure/Web/SlidingWebAppGenerator.cs`
* `src/Meloberry.Infrastructure/Web/WebAppSharedStyles.cs`
* `src/Meloberry.Infrastructure/Services/CdpHeadlessWebAppVerifier.cs` *(ensure CDP `checkSlide` works cleanly with lazy slide/question hydration)*
* `tests/Meloberry.UnitTests/LayoutAndWebAppTests.cs` *(verify CDP master showcase passes with 0 errors across lazy slides + interactive clicks + search)*

---

## Phase 4 — CLI I/O, Asset Inspection & Pipeline Deduplication (Fixes #3, #5 & #6)
**Goal:** Eliminate unnecessary disk locks, full-file image reads, redundant lint passes, and repeated DB scans during `apply`, `check`, `ls`, and `build`.

### Concrete Changes

1. **Lease-Based Timestamp Reservation (`MonotonicTimestampIdGenerator.cs`)**
   * Add `_leasedUntilMs` field guarded by `_gate` with `private const int LeaseWindowMs = 64;`.
   * **Fast path:** If `nowMs <= _leasedUntilMs && _lastEpochMs < _leasedUntilMs`, allocate `nextMs = Math.Max(nowMs, _lastEpochMs + 1)` purely in memory (0 filesystem calls).
   * **Refill path:** Only when the lease is exhausted (or wall-clock moves past `_leasedUntilMs`), acquire `.mby_id_clock.lock`, read persisted high-water mark, reserve `[startMs .. startMs + LeaseWindowMs - 1]`, write `startMs + LeaseWindowMs - 1` to `.mby_id_clock`, and release lock.
   * Reduces 300+ OS lock/read/write cycles during bulk `apply` to ~5 while maintaining strict cross-process monotonicity.

2. **Header-Only Image Verification & Streaming Hash/Copy (`FileContentInspector.cs`, `DiagnosticsUseCases.cs`)**
   * In `FileContentInspector.VerifyStoredAssetAsync`:
     * Open a `FileStream` (`FileOptions.Asynchronous | FileOptions.SequentialScan`), check `Length == 0`, and read only the first **16 bytes** into a small buffer.
     * Extract `TryDetectRasterImageExtension(ReadOnlySpan<byte> header, out string ext)` (PNG/JPEG/GIF/WebP/BMP) — return `Result.Success()` immediately with **zero full-file allocation**.
     * Only if the header does not match a raster format and starts with UTF-8 BOM/whitespace/`<`, read full text to validate sanitized `<svg>` via `DangerousSvgRegex`.
   * In `FileContentInspector.ResolveInputAsync` / `CopyImageBytesToAssetsAsync`:
     * For raster images, compute `SHA256.HashDataAsync(stream, ct)` directly from the `FileStream` (rewinding to 0) and stream-copy to `assets/<stem>_<hash8>.<ext>` only if the destination file does not already exist — eliminating LOH `byte[]` buffers for large diagrams.
   * In `LintHandler` (`DiagnosticsUseCases.cs`):
     * Thread a per-invocation `Dictionary<string, Result> verifiedAssets` cache through `LintSessionAsync` / `LintComponentAsync` / `LintQuizAsync` / `CheckImageAssetAsync` so shared images referenced across multiple blocks/questions are verified once per lint pass.

3. **Eliminate Double-Linting & Redundant SQLite Reload on One-Shot `build` / `apply --build` (`ApplyDeclarativeSpecUseCase.cs`, `PackagingAndWebUseCases.cs`, `DiagnosticsUseCases.cs`, `MbypkgExporter.cs`)**
   * In `ApplySpecHandler`:
     * When `command.BuildOutDir` or `command.ExportSpec` is non-empty, skip `LintSessionInMemoryAsync` / `LintQuizInMemoryAsync` right after persisting (since `BuildWebAppHandler` / `ExportPackageHandler` runs mandatory lint immediately afterward).
     * Expose internal overloads on `BuildWebAppHandler` (`BuildResolvedWorkspaceAsync(StudySession?, Quiz?, string storageDir, ...)`) and `ExportPackageHandler` so `ApplySpecHandler` passes the already-constructed in-memory aggregate directly without reloading it from SQLite.
   * In `MbypkgExporter.CollectSessionAssets`:
     * Replace the double `TryDeserialize<DiagramExplorePayload>` LINQ chain (`.Where(...TryDeserialize...).Select(...TryDeserialize...)`) with a single `foreach` loop deserializing each `DiagramExplore` block once.

4. **SQLite `PRAGMA user_version` & `mby ls` Metadata Caching (`SqliteLayoutRepository.cs`, `SqliteWorkspaceRepository.cs`)**
   * **Schema versioning:** Add `private const int CurrentWorkspaceSchemaVersion = 2;` in `SqliteWorkspaceRepository`. On load (`LoadSessionFromDbAsync`, `LoadStandaloneQuizFromDbAsync`), read `PRAGMA user_version;` (1 fast integer query). If `>= 2`, skip all 4 `EnsureTableColumnsAsync` (`PRAGMA table_info(...)`) queries. On save (`EnsureWorkspaceSchemaAsync`), set `PRAGMA user_version = 2;`.
   * **`mby ls` caching:**
     * Add `ItemCount INTEGER NULL` and `DbLastWriteUtc TEXT NULL` columns to `WorkspaceRegistry` in `SqliteLayoutRepository.EnsureInitializedAsync` (auto-migrated via `ALTER TABLE` if absent on existing `app_settings.db`).
     * Populate `ItemCount` and `File.GetLastWriteTimeUtc(dbPath).ToString("O")` in `RegisterWorkspaceAsync`.
     * In `ScanDirectoryForWorkspacesAsync`, load the existing `WorkspaceRegistry` `(DbPath -> DbLastWriteUtc)` map once; if a workspace `.db` file's mtime matches the cached value, **skip opening that `.db` completely**.
     * In `ListWorkspacesAsync`, reuse cached `ItemCount` when `DbLastWriteUtc` matches instead of re-opening every `.db` a second time for `CountWorkspaceItemsAsync`.

### Files Affected
* `src/Meloberry.Infrastructure/Services/MonotonicTimestampIdGenerator.cs`
* `src/Meloberry.Infrastructure/Services/FileContentInspector.cs`
* `src/Meloberry.Application/UseCases/Diagnostics/DiagnosticsUseCases.cs`
* `src/Meloberry.Application/UseCases/Json/ApplyDeclarativeSpecUseCase.cs`
* `src/Meloberry.Application/UseCases/Packaging/PackagingAndWebUseCases.cs`
* `src/Meloberry.Infrastructure/Packaging/MbypkgExporter.cs`
* `src/Meloberry.Infrastructure/Persistence/SqliteLayoutRepository.cs`
* `src/Meloberry.Infrastructure/Persistence/SqliteWorkspaceRepository.cs`
* `tests/Meloberry.UnitTests/MonotonicIdGeneratorTests.cs` *(add concurrent multi-instance lease contention test)*
* `tests/Meloberry.UnitTests/FileContentInspectorTests.cs` *(add header-only verification & streaming copy coverage)*
* `tests/Meloberry.UnitTests/PersistenceAndPackagingTests.cs` *(verify legacy v1.3 DB upgrade sets `user_version` and `ls` refreshes when `.db` mtime changes)*

---

## Phase 5 — Persistence Scalability, Fast Transactions, Cold-Start & HTTP/CDP Efficiency (Fix #9)
**Goal:** Optimize surgical `patch` writes, `batch` rollback snapshots, stateless CLI startup latency, preview server memory, and headless CDP verification runtime.

### Concrete Changes

1. **Prepared Commands & Surgical SQLite Updates (`SqliteWorkspaceRepository.cs`, `Ports.cs`, `PatchEntityUseCase.cs`)**
   * Inside `SaveWorkspaceCoreAsync` and `RegisterWorkspaceAsync`, reuse a single parameterized `SqliteCommand` per table inside the transaction (matching `SqliteLayoutRepository.SaveProfilesAsync`) and set `PRAGMA synchronous = NORMAL;`.
   * Add optional targeted update methods on `IWorkspaceRepository` (implemented in `SqliteWorkspaceRepository`) for non-structural patches (no `--move`, no `ref` index change):
     * `UpdateSessionMetadataAsync(StudySession)`
     * `UpdateQuizMetadataAsync(Quiz)`
     * `UpdateArticleBlockAsync(StudySession, ArticleBlock)`
     * `UpdateQuestionAsync(QuizOwnerContext, Question)`
     * `UpdateComponentAsync(StudySession, SessionComponent)`
     Use them from `PatchEntityHandler` when `string.IsNullOrWhiteSpace(command.MoveSpec)` to avoid full 5-table wipe-and-reinsert + `EntityIndex` rewrite on single-field edits.
   * Keep `Pooling=False` (or call `SqliteConnection.ClearAllPools()` explicitly before `Directory.Delete`/`Directory.Move`) so Windows file-handle locking never flakes `rm` or `batch` rollbacks.

2. **`.db`-Only & Atomic-Move Snapshots in `ChainTransactionManager.cs`**
   * Add `NotifyWorkspaceDeleting(string storageDirectory)` and update `NotifyWorkspaceMutating(string storageDirectory)`:
     * **Workspace edits (`SaveSessionAsync`/`SaveQuizAsync`/`CopyImageBytesToAssetsAsync`):** Instead of recursively copying `assets/` (megabytes/gigabytes of immutable content-hashed images) into `%TEMP%`, snapshot **only** `<wsId>.db` (plus `-wal`/`-shm`/`-journal`) and record the `HashSet<string>` of pre-existing filenames in `assets/`. On rollback: restore `<wsId>.db` and delete only newly created files in `assets/`.
     * **Workspace deletes (`DeleteSessionAsync`/`DeleteQuizAsync`):** Inside an active transaction, move `storageDirectory` into `txRoot` via `Directory.Move` (O(1) instant on same volume; fallback to copy if cross-volume) and move it back on rollback.

3. **Cold-Start Optimizations: Static Command Registration & `[GeneratedRegex]` (`CliApplicationRunner.cs`, `SlidingWebAppGenerator.cs`, `FileContentInspector.cs`)**
   * In `CliServiceRegistration.AddCli()`, replace `Assembly.GetTypes()` reflection + `Activator.CreateInstance` with an explicit static array of the 14 `CliCommand` instances (eliminates assembly scanning on every CLI invocation and unblocks NativeAOT).
   * Mark `SlidingWebAppGenerator` and `FileContentInspector` as `partial` and migrate `MarkdownUrlAttributeRegex`, `HtmlTagStripRegex`, `WhitespaceCollapseRegex`, and `DangerousSvgRegex` from runtime `new Regex(..., RegexOptions.Compiled)` to compile-time `[GeneratedRegex]`.

4. **Streaming Static HTTP & Bounded Analysis Uploads (`LocalPreviewHost.cs`, `CdpHeadlessWebAppVerifier.cs`)**
   * Replace `File.ReadAllBytesAsync(...)` in both `LocalPreviewHost.RunWebAppServerLoopAsync` and `CdpHeadlessWebAppVerifier.ServeStaticDirectoryAsync` with streaming `await using var fs = File.OpenRead(safePath); response.ContentLength64 = fs.Length; await fs.CopyToAsync(response.OutputStream, ct);`.
   * Add `Cache-Control: public, max-age=31536000, immutable` for content-hashed `assets/*` and `katex/fonts/*`.
   * Bound `LocalPreviewHost.HandleAnalysisPostAsync` body reads with `ReadRequestBodyBoundedAsync(ctx.Request, 5 * 1024 * 1024, ct)` to prevent unbounded RAM allocation.

5. **Faster Event-Driven CDP Verification (`CdpHeadlessWebAppVerifier.cs`)**
   * In `verifyScript`, replace fixed `sleep(60)` / `sleep(45)` / `sleep(30)` / `sleep(15)` calls with `const settle = () => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));` (CSS transitions are already forced to `0ms !important` by `verifyStyle`).
   * Decode WebSocket receive buffers directly from `ms.GetBuffer().AsSpan(0, (int)ms.Length)` instead of allocating via `ms.ToArray()` on every CDP event frame.

### Files Affected
* `src/Meloberry.Application/Abstractions/Ports.cs` *(optional targeted update methods on `IWorkspaceRepository`)*
* `src/Meloberry.Application/UseCases/Json/PatchEntityUseCase.cs`
* `src/Meloberry.Infrastructure/Persistence/SqliteWorkspaceRepository.cs`
* `src/Meloberry.Infrastructure/Persistence/ChainTransactionManager.cs`
* `src/Meloberry.Infrastructure/Services/FileContentInspector.cs`
* `src/Meloberry.Infrastructure/Web/SlidingWebAppGenerator.cs`
* `src/Meloberry.Infrastructure/Services/CdpHeadlessWebAppVerifier.cs`
* `src/Meloberry.Cli/Hosting/CliApplicationRunner.cs`
* `src/Meloberry.Cli/GuiEditor/LocalPreviewHost.cs`
* `tests/Meloberry.UnitTests/AddressPatchAndLintTests.cs` *(verify surgical patch persistence + `batch` rollback with pre-existing/new assets & `rm` rollback)*
* `tests/Meloberry.CliTests/LayoutEditorHostAndCliTests.cs` *(verify streaming/oversized analysis POST behavior)*

---

## Master Manifest of All Files Affected

### Production Files (`src/`) — 14 files
| Layer | File | Phases |
| :--- | :--- | :---: |
| **Domain** | `src/Meloberry.Domain/Layouts/LayoutBoundsCatalog.cs` | 1 |
| **Application** | `src/Meloberry.Application/Abstractions/Ports.cs` | 5 |
| **Application** | `src/Meloberry.Application/UseCases/Diagnostics/DiagnosticsUseCases.cs` | 4 |
| **Application** | `src/Meloberry.Application/UseCases/Json/ApplyDeclarativeSpecUseCase.cs` | 4 |
| **Application** | `src/Meloberry.Application/UseCases/Json/PatchEntityUseCase.cs` | 5 |
| **Application** | `src/Meloberry.Application/UseCases/Packaging/PackagingAndWebUseCases.cs` | 4 |
| **Infrastructure** | `src/Meloberry.Infrastructure/Web/SlidingWebAppGenerator.cs` | 1, 2, 3, 5 |
| **Infrastructure** | `src/Meloberry.Infrastructure/Web/WebAppSharedStyles.cs` | 1, 2, 3 |
| **Infrastructure** | `src/Meloberry.Infrastructure/Web/LayoutEditorPageBuilder.cs` | 2 *(signature alignment)* |
| **Infrastructure** | `src/Meloberry.Infrastructure/Services/MonotonicTimestampIdGenerator.cs` | 4 |
| **Infrastructure** | `src/Meloberry.Infrastructure/Services/FileContentInspector.cs` | 4, 5 |
| **Infrastructure** | `src/Meloberry.Infrastructure/Services/CdpHeadlessWebAppVerifier.cs` | 2, 3, 5 |
| **Infrastructure** | `src/Meloberry.Infrastructure/Packaging/MbypkgExporter.cs` | 4 |
| **Infrastructure** | `src/Meloberry.Infrastructure/Persistence/SqliteLayoutRepository.cs` | 4 |
| **Infrastructure** | `src/Meloberry.Infrastructure/Persistence/SqliteWorkspaceRepository.cs` | 4, 5 |
| **Infrastructure** | `src/Meloberry.Infrastructure/Persistence/ChainTransactionManager.cs` | 5 |
| **CLI** | `src/Meloberry.Cli/Hosting/CliApplicationRunner.cs` | 5 |
| **CLI** | `src/Meloberry.Cli/GuiEditor/LocalPreviewHost.cs` | 5 |

*(18 source files total; zero new projects, zero new NuGet packages, zero changes to `Meloberry.Domain` dependency purity.)*

### Test Files (`tests/`) — 7 files
| Project | File | Coverage Added / Updated |
| :--- | :--- | :--- |
| **UnitTests** | `tests/Meloberry.UnitTests/LayoutAndWebAppTests.cs` | `WhenWritingNull` check, root `DATA.profiles` + override precedence, widget/theme/KaTeX tree-shaking, stale asset pruning, full CDP showcase on lazy slides |
| **UnitTests** | `tests/Meloberry.UnitTests/MonotonicIdGeneratorTests.cs` | Cross-instance lease contention & zero collisions |
| **UnitTests** | `tests/Meloberry.UnitTests/FileContentInspectorTests.cs` | Header-only raster verification & streaming copy |
| **UnitTests** | `tests/Meloberry.UnitTests/AddressPatchAndLintTests.cs` | Surgical patch DB updates, asset lint caching, `.db`-only & `rm` batch rollback |
| **UnitTests** | `tests/Meloberry.UnitTests/PersistenceAndPackagingTests.cs` | `PRAGMA user_version` legacy migration & `ls` mtime cache invalidation |
| **CliTests** | `tests/Meloberry.CliTests/CliEndToEndWorkflowTests.cs` | End-to-end verification (plus `preview-index.json` adjustment if dropped) |
| **CliTests** | `tests/Meloberry.CliTests/LayoutEditorHostAndCliTests.cs` | Editor full-CSS/all-themes parity + streaming/bounded POST safety |

---

## Verification Gate After Every Phase
1. `dotnet build -c Release` — 0 warnings (`TreatWarningsAsErrors` enabled).
2. `dotnet test` — 100% pass across `Meloberry.ArchitectureTests`, `Meloberry.UnitTests` (including live headless Chromium CDP test), and `Meloberry.CliTests`.
3. **Before/After Benchmarks to Record:**
   * Single-article WebApp output size (`index.html` + `styles.css` + `app.js` + `katex/`) vs. 18-component showcase size.
   * Bulk `mby apply` latency on a 200-question spec (verifying lease ID generator + prepared SQLite commands).
   * `mby ls` latency across 20 cached workspaces (verifying `DbLastWriteUtc` skip).
   * CDP verification wall-clock time on the 18-component master showcase.