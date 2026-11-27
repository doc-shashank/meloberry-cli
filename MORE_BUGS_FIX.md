# Implementation Plan — Phases 1, 2 & 3 (`mby` v2.1.2)

This plan executes priorities **1 (Quick Wins & Bug Fixes)**, **2 (Code De-duplication)**, and **3 (Runtime Optimizations, Spec Taxonomy & UX Polish)** as three independently testable, `dotnet test`-gated milestones.

All changes strictly preserve:
- Clean Architecture rules enforced by `Meloberry.ArchitectureTests` (`Domain` remains dependency-free; entities keep private constructors/setters).
- 100% wire, SQLite, `.mbypkg` v1.0/v2.0, and CLI backward compatibility (`mby prime` stays `< 1550` CRLF chars; existing `@group` selectors and component file structure stay intact).

---

## Phase 1 — Quick Wins & Bug Fixes
**Goal:** Eliminate silent runtime bugs, hot-loop allocations, missing DB indexes, and dead/drifted properties with minimal blast radius.

### 1.1 Deterministic Shuffling in `classify` and `match`
- **File:** `src/Meloberry.Infrastructure/Web/Components/ContentSpecs/QuestionSpecs.Set3.cs`
- **Changes:**
  - In `ClassifyJs`, replace:
    ```js
    order: cl.items.map(it => it.itemId).sort(() => Math.random() - 0.5)
    ```
    with:
    ```js
    order: MBY.helpers.deterministicShuffle(cl.items, `${slide.id}:cls`, it => String(it.itemId)).map(it => it.itemId)
    ```
  - In `MatchJs`, replace:
    ```js
    poolOrder: mt.rightPool.map(o => o.optionId).sort(() => Math.random() - 0.5)
    ```
    with:
    ```js
    poolOrder: MBY.helpers.deterministicShuffle(mt.rightPool, `${slide.id}:mth`, o => String(o.optionId)).map(o => o.optionId)
    ```
  - *(Note: `WebAppFeatureSet.HasDragAndDrop` already includes `HasClassify || HasMatch`, so `dnd-shuffle.js` is already guaranteed to be loaded.)*

### 1.2 Add Missing SQLite Secondary Index on `EntityIndex(WorkspaceId)`
- **File:** `src/Meloberry.Infrastructure/Persistence/SqliteLayoutRepository.cs`
- **Changes:**
  - In `EnsureInitializedAsync`, append to the `CREATE TABLE IF NOT EXISTS` batch:
    ```sql
    CREATE INDEX IF NOT EXISTS IX_EntityIndex_WorkspaceId ON EntityIndex(WorkspaceId);
    ```
  - Eliminates full table scans during `DELETE FROM EntityIndex WHERE WorkspaceId = $id;` in `SqliteWorkspaceRepository.RegisterWorkspaceAsync` and `RemoveWorkspaceFilesAndIndexAsync`.

### 1.3 Hoist `JsonSerializerOptions` Out of Hot Loop in `PatchEntityHandler`
- **File:** `src/Meloberry.Application/UseCases/Json/PatchEntityUseCase.cs`
- **Changes:**
  - Add a static readonly field alongside `ReadOptions`:
    ```csharp
    private static readonly JsonSerializerOptions OptionReadOptions = new(ReadOptions)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    ```
  - Replace the per-iteration `new JsonSerializerOptions(ReadOptions) { ... }` inside `ApplyMcqPatchAsync`'s `for` loop with `OptionReadOptions`.

### 1.4 Fix `NumericJs` Range Formatting & Unit Suffix Stripping
- **File:** `src/Meloberry.Infrastructure/Web/Components/ContentSpecs/QuestionSpecs.Set1.cs` (`NumericJs`)
- **Changes:**
  - Add a clean number formatter helper inside `NumericJs`:
    ```js
    const fmtNum = v => {
      if (!Number.isFinite(v)) return String(v);
      const rounded = Number(v.toPrecision(6));
      return Math.abs(rounded) < 1e-12 ? '0' : String(rounded);
    };
    ```
    Use `fmtNum(num.answer - tolRadius)` and `fmtNum(num.answer + tolRadius)` instead of `.toPrecision(4)` (avoids `1.250e+3` for `1250`).
  - In `gradeNumeric()`, strip a trailing unit string (case-insensitive) if `num.unit` is defined before parsing `Number(cleaned)`:
    ```js
    let cleaned = (st.value || '').trim().replace(/,/g, '');
    if (num.unit) {
      const escUnit = num.unit.trim().replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      cleaned = cleaned.replace(new RegExp('\\s*' + escUnit + '$', 'i'), '').trim();
    }
    ```
  - On wrong finite answers where `num.answer !== 0`, include percentage delta in feedback (e.g., `Out of accepted range (off by 8.4%).`).

### 1.5 Wire Up `Placeholder` on `diff` (Resolving Latent `d.placeholder` in `DiffJs`)
- **Files:**
  - `src/Meloberry.Domain/Payloads/Phase2Payloads.cs` — add optional `string? Placeholder = null` parameter to `DifferentiationPayload` (trailing default keeps 100% JSON/DB backward compatibility).
  - `src/Meloberry.Infrastructure/Services/MarkdownSpecParser.cs` — add `"placeholder"` to `AllowedDiffAttrs` and pass it into `DifferentiationPayload`.
  - `src/Meloberry.Infrastructure/Services/DeclarativeSpecLoader.cs` — include `QuestionFieldMask.Placeholder` in `QuestionFieldPolicies[QuestionType.Differentiation]` and pass `q.Placeholder`.
  - `src/Meloberry.Application/UseCases/Json/JsonQuestionMaterializer.cs` — preserve `parsed.Placeholder` (trimmed/null) during `Differentiation` canonicalization.
  - `src/Meloberry.Application/UseCases/Json/PatchEntityUseCase.cs` — add `"placeholder"` to `AllowedKeysByKind["question:diff"]` and patch it via `TryReadNullableString`.
  - `src/Meloberry.Infrastructure/Web/SlidingWebAppGenerator.cs` — add `string? Placeholder = null` to `DifferentiationRenderModel` and pass `dp.Placeholder`.
  - `src/Meloberry.Application/UseCases/Diagnostics/GetSchemaUseCase.cs` — document `placeholder?:string` in `BuildDiffSchema()`.

### 1.6 Enforce `Phase3PayloadRules.Max*` Constants as Lint Warnings
- **File:** `src/Meloberry.Application/UseCases/Diagnostics/DiagnosticsUseCases.cs`
- **Changes:**
  - Emit **Warning**-severity lint diagnostics (non-blocking for `IsClean`, visible in `mby check` / `data.lint.warnings`) when Phase 3 density limits are exceeded:
    - `Lint.ExcessiveDiagramHotspots` when `p.Hotspots.Count > Phase3PayloadRules.MaxHotspots` (`20`)
    - `Lint.ExcessiveRevealSteps` when `p.Steps.Count > Phase3PayloadRules.MaxRevealSteps` (`20`)
    - `Lint.ExcessiveClassifyBuckets` / `Lint.ExcessiveClassifyItems` when exceeding `MaxClassifyBuckets` (`4`) / `MaxClassifyItems` (`24`)
    - `Lint.ExcessiveMatchPairs` / `Lint.ExcessiveMatchDistractors` when exceeding `MaxMatchPairs` (`12`) / `MaxMatchDistractors` (`6`)
    - `Lint.ExcessiveCaseQuestions` when `p.Questions.Count > Phase3PayloadRules.MaxCaseQuestions` (`10`)

### 1.7 Small Dead-Code & Doc Cleanups
- **Files:**
  - `src/Meloberry.Cli/GuiEditor/LocalPreviewHost.cs` — remove redundant `CompactJsonOptions` alias (use `JsonOptions` directly) and update the `TryResolveComponentTarget` invalid-target error message to mention Phase 3 alias targets cleanly.
  - `src/Meloberry.Domain/Layouts/LayoutBoundsCatalog.cs` — remove unused `ValidateAndNormalize`.
  - `docs/README.md`, `docs/JSON_AND_PACKAGES.md`, `docs/LAYOUTS_LINT_AND_WEBAPP.md` — sync Entity IDs table, JSON/patch examples, and WebApp overview with Phase 3 types (`diagram-explore`, `reveal`, `case`, `classify`, `match`, `numeric`).
  - Remove completed `WEB_OPTIMIZATIONS_PLAN.md` from repo root (or archive it under `docs/` if desired).

### Phase 1 Tests
- `tests/Meloberry.UnitTests/AddressPatchAndLintTests.cs`: verify `diff` `placeholder` create/patch/render and Phase 3 `Max*` warning emission.
- `tests/Meloberry.UnitTests/LayoutAndWebAppTests.cs`: verify `classify`/`match` emit `deterministicShuffle` (no `Math.random()`) and `NumericJs` strips units & formats large bounds cleanly.
- `tests/Meloberry.UnitTests/PersistenceAndPackagingTests.cs`: verify `IX_EntityIndex_WorkspaceId` exists in `app_settings.db`.

---

## Phase 2 — Code De-duplication
**Goal:** Collapse copy-pasted parsers, payload switches, spec catalogs, DB connection churn, and JS widget boilerplate into single sources of truth.

### 2.1 Single Source of Truth for Spec Topic Catalog
- **Files:**
  - `src/Meloberry.Application/UseCases/Diagnostics/GetSchemaUseCase.cs`
  - `src/Meloberry.Application/UseCases/Diagnostics/GetPrimeGuideUseCase.cs`
- **Changes:**
  - Replace hardcoded `GetSchemaHandler.SupportedTopics` array with:
    ```csharp
    public static readonly IReadOnlyList<string> SupportedTopics =
        TopicCatalog.Select(t => t.Topic).ToArray();
    ```
  - In `GetPrimeGuideHandler`, replace `ImplementedSpecTopics` with `GetSchemaHandler.SupportedTopics` (preserving exact ordering in `TopicCatalog` or defining display order once so `mby prime` output stays compact and consistent).

### 2.2 Centralize `Question.IsPayloadType(QuestionType)`
- **Files:**
  - `src/Meloberry.Domain/Questions/Question.cs`
  - `src/Meloberry.Application/UseCases/Json/JsonQuestionMaterializer.cs`
  - `src/Meloberry.Infrastructure/Packaging/MbypkgExporter.cs`
  - `src/Meloberry.Infrastructure/Persistence/SqliteWorkspaceRepository.cs`
- **Changes:**
  - Add on `Question`:
    ```csharp
    public static bool IsPayloadType(QuestionType type) =>
        type is not (QuestionType.Mcq or QuestionType.Flashcard);
    ```
  - Replace the three duplicated 10-case `qType is QuestionType.FillBlank or ... or QuestionType.CaseGroup` patterns with `Question.IsPayloadType(...)`.

### 2.3 Unify Normalized Box / Rect Parsing Across Domain, JSON, and YAML
- **Files:**
  - `src/Meloberry.Domain/Payloads/Phase2Payloads.cs`
  - `src/Meloberry.Application/UseCases/Json/JsonNodeReaders.cs`
  - `src/Meloberry.Infrastructure/Services/DeclarativeSpecLoader.cs`
  - `src/Meloberry.Infrastructure/Services/MarkdownSpecParser.cs`
- **Changes:**
  - Add a canonical `public readonly record struct NormalizedRect(double X, double Y, double W, double H)` with `IsValid => Phase2PayloadRules.HasValidNormalizedBox(X, Y, W, H)` and `Phase2PayloadRules.TryParseRectValues(IReadOnlyList<double>? rect, double? x, double? y, double? w, double? h, out NormalizedRect result, out string errorReason)`.
  - Route `DeclarativeSpecLoader.TryReadNormalizedRect` and `JsonNodeReaders.ReadNormalizedRect` through this single validator, and have `Phase3PayloadRules.HasValidHotspotBounds` / `Phase2PayloadRules.HasValidRelabelBoxBounds` delegate directly to `HasValidNormalizedBox`.

### 2.4 Unify YAML `boxes:` Parser in `MarkdownSpecParser`
- **File:** `src/Meloberry.Infrastructure/Services/MarkdownSpecParser.cs`
- **Changes:**
  - Extract a single shared helper:
    ```csharp
    private sealed record ParsedYamlBox(string? Label, List<string> Aliases, string? Note, double? X, double? Y, double? W, double? H);
    ```
    with `TryParseYamlBoxes(...)` supporting `label`, `rect`, `x`, `y`, `w`, `h`, and caller-configured flags `bool allowAliases`, `bool allowNote`, `string directiveTag`, `string errorCode`.
  - Refactor both `ParseRelabelDirective` (for its `boxes:` section) and `ParseInlineDiagramHotspotsYaml` to call `TryParseYamlBoxes(...)`.
  - Unifies `- label:` vs `-label:`, `#` comment handling, and error reporting while removing ~100 lines of duplicated state-machine code.

### 2.5 Deduplicate Frontend JS Helpers & Eliminate Redundant `MBY` Trailer
- **Files:**
  - `src/Meloberry.Infrastructure/Web/WebAppSharedStyles.cs`
  - `src/Meloberry.Infrastructure/Web/Components/AuxiliarySpecs/AuxiliarySpecs.cs`
  - `src/Meloberry.Infrastructure/Web/Components/ContentSpecs/QuestionSpecs.Set1.cs`
  - `src/Meloberry.Infrastructure/Web/Components/ContentSpecs/QuestionSpecs.Set2.cs`
  - `src/Meloberry.Infrastructure/Web/Components/ContentSpecs/QuestionSpecs.Set3.cs`
  - `src/Meloberry.Infrastructure/Web/Components/ContentSpecs/BlockSpecs.cs`
  - `src/Meloberry.Infrastructure/Web/SlidingWebAppGenerator.cs`
- **Changes:**
  - Move `createRetryBtn(onRetry, visible, label = 'Retry', compact = false)` into `WebAppSharedStyles.BuildBaseCoreJs()`.
  - Remove the duplicated `createRetryBtn` from `AuxiliarySpecsCatalog.BuildGradedCoreJs()` and the fallback copy from `QuestionSpecsSet1.BuildFlashcardJs()`.
  - Parameterize `BuildCreateZoomableImageNodeJs(string iconSvgExpr)` in `AuxiliarySpecsCatalog` so `EditorMediaZoomJs` and `BuildMediaZoomJs()` share one single definition of `createZoomableImageNode`.
  - In `SlidingWebAppGenerator.BuildHtml`, emit a tiny 1-line inline bootstrap `<script>window.MBY={blocks:{},questions:{},helpers:{},search:{},blockSearch:{},gradedKinds:{},kindLabels:{}};</script>` immediately before component `<script>` tags so component modules can simply pass `(window.MBY)` instead of repeating the bootstrap object literal 16 times.

### 2.6 Consolidate `app_settings.db` Connections in `ListWorkspacesAsync` & Reuse `EnumerateArticleBlockMarkdownStrings`
- **Files:**
  - `src/Meloberry.Infrastructure/Persistence/SqliteWorkspaceRepository.cs`
  - `src/Meloberry.Infrastructure/Web/SlidingWebAppGenerator.cs`
- **Changes:**
  - In `SqliteWorkspaceRepository.ListWorkspacesAsync`, keep a single open `SqliteConnection` to `app_settings.db` across the scan-cache read, registry listing, and stale-entry pruning (opening once instead of 3 times).
  - In `SlidingWebAppGenerator.BuildSessionPreviewSidecar`, reuse a shared `ExtractBlockPlainTextParts(ArticleBlockRenderModel b)` helper aligned with `WebAppFeatureSet.Analyze` so adding a new block kind only needs one traversal point.

### 2.7 Zero-Allocation 1D Levenshtein
- **File:** `src/Meloberry.Domain/Common/TextSimilarity.cs`
- **Changes:**
  - Replace `new int[a.Length + 1, b.Length + 1]` with a 1D `Span<int>` (`stackalloc int[b.Length + 1]` when `b.Length <= 128`, fallback `new int[b.Length + 1]`).

### Phase 2 Tests
- Run full `Meloberry.ArchitectureTests`, `Meloberry.UnitTests`, and `Meloberry.CliTests`.
- Add unit tests in `PayloadAndMarkdownTests.cs` confirming `diagram-explore` inline YAML now handles `#` comments and `-label:` consistently with `relabel`, and `TextSimilarity.Levenshtein` matches exact distances across short and >128-char strings.

---

## Phase 3 — Runtime Optimizations, Spec Taxonomy & UX Polish
**Goal:** Speed up builds/dry-runs/previews, give AI agents fine-grained spec selectors, and ship high-ROI UX improvements across existing specs.

### 3.1 Memoize Static Minification, Prune Unused SVG Symbols & Prune Unused KaTeX `@font-face` Rules
- **Files:**
  - `src/Meloberry.Infrastructure/Web/Components/WebComponentModule.cs`
  - `src/Meloberry.Infrastructure/Web/SlidingWebAppGenerator.cs`
  - `src/Meloberry.Infrastructure/Web/WebAppSharedStyles.cs`
- **Changes:**
  - **Cached Minification:** Add static `ConcurrentDictionary<string, string>` (or `Lazy<string>` properties on `WebComponentModule`) for `MinifiedCss` and `MinifiedJs` so static CSS/JS modules are minified once per process instead of re-lexed on every `mby build`.
  - **Conditional SVG `<symbol>` Emission:** In `SlidingWebAppGenerator.BuildHtml`, emit:
    - `mby-ico-zoom` only when `features.HasImages`
    - `mby-ico-eye` and `mby-ico-eye-off` only when `features.HasTable`
    - Always emit `mby-ico-save` and `mby-ico-github` (preserves `LayoutAndWebAppTests` assertion).
  - **Filtered `katex.min.css`:** When copying `katex.min.css` in `CopyEmbeddedKatexAssetsAsync`, filter out `@font-face{...}` blocks whose `font-family` was not copied by `ShouldCopyKatexFont`, and strip nonexistent `.woff`/`.ttf` fallback URLs so browsers only ever see bundled `.woff2` fonts.
  - **Parallel Asset & Module Writes:** In `WriteWebAppBundleAsync`, write emitted component files and copy referenced assets concurrently using `Task.WhenAll` / `Parallel.ForEachAsync` (with bounded degree of parallelism, e.g., `4`).

### 3.2 Zero-Copy `--dry-run` Asset Verification
- **Files:**
  - `src/Meloberry.Application/Abstractions/Ports.cs`
  - `src/Meloberry.Application/UseCases/Diagnostics/DiagnosticsUseCases.cs`
  - `src/Meloberry.Application/UseCases/Json/ApplyDeclarativeSpecUseCase.cs`
- **Changes:**
  - Add a default interface method on `IContentInspector`:
    ```csharp
    async Task<Result> VerifyStoredAssetAsync(
        string workspaceStorageDirectory, string relativeAssetPath, string? fallbackStorageDirectory, CancellationToken cancellationToken = default)
    {
        var primary = await VerifyStoredAssetAsync(workspaceStorageDirectory, relativeAssetPath, cancellationToken);
        if (primary.IsSuccess || string.IsNullOrWhiteSpace(fallbackStorageDirectory))
            return primary;
        return await VerifyStoredAssetAsync(fallbackStorageDirectory, relativeAssetPath, cancellationToken);
    }
    ```
  - Thread optional `string? fallbackAssetRoot = null` through `LintHandler.LintSessionInMemoryAsync` / `LintQuizInMemoryAsync` → `CheckImageAssetAsync`.
  - In `ApplySpecHandler.CreateDryRunStagingCopy`, create only an empty staging directory + `assets/` folder (no longer copying existing workspace assets into `%TEMP%`!), and pass `realStorageDir` as `fallbackAssetRoot` during dry-run linting.

### 3.3 Concurrent Static `GET` Dispatch in Preview & CDP Servers
- **Files:**
  - `src/Meloberry.Cli/GuiEditor/LocalPreviewHost.cs`
  - `src/Meloberry.Infrastructure/Services/CdpHeadlessWebAppVerifier.cs`
- **Changes:**
  - In `CdpHeadlessWebAppVerifier.ServeStaticDirectoryAsync` and `LocalPreviewHost.RunWebAppServerLoopAsync`, dispatch read-only static file `GET` responses onto background tasks guarded by a `SemaphoreSlim(8, 8)` (keeping `POST /api/analysis` and layout mutations serialized), tracking active tasks cleanly before closing the listener on shutdown. Cuts multi-file asset waterfall latency during browser load and CDP verification.

### 3.4 Fine-Grained Tag-Based Spec Selectors (`mby spec`)
- **File:** `src/Meloberry.Application/UseCases/Diagnostics/GetSchemaUseCase.cs`
- **Changes:**
  - Extend `TopicClassification` with optional `IReadOnlyList<string> Tags`:
    ```csharp
    public sealed record TopicClassification(string Topic, string Scope, string Category, IReadOnlyList<string> Tags);
    ```
  - Preserve all 7 existing groups (`@content`, `@containers`, `@blocks`, `@questions`, `@other`, `@formats`, `@workflows`) untouched and add targeted tag selectors to `ExpandGroupToken` and `SupportedGroups`:
    - `@blocks:static` (`text`, `image`, `callout`)
    - `@blocks:interactive` (`table`, `diagram-explore`, `reveal`)
    - `@q:choice` (`mcq`, `assertion-reason`, `spot-error`)
    - `@q:recall` (`flashcard`, `fill-blank`, `numeric`)
    - `@q:relational` (`diff`, `classify`, `match`, `mechanism`)
    - `@q:visual` (`relabel`)
    - `@q:composite` (`case`)
    - `@masks` (`image`, `diagram-explore`, `relabel`)
    - `@tabular` (`table`, `diff`)
    - `@authoring` (`markdown`, `patch`, `batch`)
    - `@pipeline` (`build`, `pkg`, `analyze`)
    - `@styling` (`layouts`)
  - Expose `Tags` on `SchemaTopicDefinition` (optional, omitted if empty) and update `docs/COMMANDS.md`.

### 3.5 High-ROI Spec & Runtime UX Polish
1. **Cloze Flashcard Shorthand (`flashcard` + `fill-blank` fusion)**
   - **Files:** `MarkdownSpecParser.cs`, `DeclarativeSpecLoader.cs`, `QuestionSpecs.Set1.cs`, `GetSchemaUseCase.cs`
   - **Design:** Support `:::flashcard{cloze=true}` (and JSON `"cloze": true` + `"text": "..."`) with no `:::back` section required. At parse time, compile via `Phase1PayloadRules.ParseFillBlankTemplate`:
     - **Front text**: replaces each `{{ans | alias}}` with `[...]` (or `[hint]` if `{{ans :: hint}}` syntax is later added, keeping initial scope to `[...]`).
     - **Back text**: replaces each `{{ans | alias}}` with `**ans**` (highlighted in context).
     - Zero DB schema change, zero `.mbypkg` change, zero new `QuestionType` — compiles cleanly to a standard `FlashcardQuestion`!
   - Also add keyboard shortcuts on active flashcards (`Space` flips, `1` marks Got It, `2` marks Needs Review when back is visible and not typing in an input).

2. **`case` Navigation & Mobile Stem Collapse**
   - **File:** `src/Meloberry.Infrastructure/Web/Components/ContentSpecs/QuestionSpecs.Set3.cs` (`CaseCss`, `CaseJs`)
   - **Changes:**
     - Add a compact `← Prev Part` / `Part X of Y` / `Next Part →` bar directly beneath `.mby-case-subhost` inside `buildCaseGroupCard`.
     - Add a compact `"Hide Stem" / "Show Stem"` toggle button inside `.mby-case-stem-head` (auto-visible on viewports `< 900px` or when stem text > 220 chars) so learners don't have to scroll past long clinical vignettes on every sub-question.

3. **`relabel` Hover Sync & Click-to-Unassign**
   - **File:** `src/Meloberry.Infrastructure/Web/Components/ContentSpecs/QuestionSpecs.Set2.cs` (`RelabelCss`, `RelabelJs`)
   - **Changes:**
     - Add `.is-hover-linked` styling; hovering `.mby-relabel-assign-row` or `.mby-relabel-recall-row` highlights the matching `.mby-mask-box` on the diagram (and hovering a `.mby-mask-box` highlights its corresponding row).
     - In `bank` mode, add a compact `✕` button on assigned `.mby-relabel-assign-row` items to unassign a single box back to the bank without resetting the entire card.

4. **`match` Bidirectional Click Pairing**
   - **File:** `src/Meloberry.Infrastructure/Web/Components/ContentSpecs/QuestionSpecs.Set3.cs` (`MatchJs`, `MatchCss`)
   - **Changes:**
     - Track `st.selectedPairId` alongside `st.selectedOptId`:
       - Clicking an empty left slot selects that slot (`target-ready` highlight on bank chips); clicking a bank chip then assigns it immediately.
       - Clicking a bank chip first continues to work identically (selecting the chip and highlighting slots).

5. **`classify` Visible Unassign & `1..4` Keyboard Sorting**
   - **File:** `src/Meloberry.Infrastructure/Web/Components/ContentSpecs/QuestionSpecs.Set3.cs` (`ClassifyJs`, `ClassifyCss`)
   - **Changes:**
     - Show a small `✕` return icon on chips currently placed inside a bucket so clicking `✕` returns the chip directly to the pool.
     - When a chip is selected (`st.selectedItemId !== null`) and no text input is focused, pressing `1`–`4` assigns it to Bucket `1`–`4`, and `Backspace`/`Escape` deselects or returns it to the pool.

### Phase 3 Tests
- `tests/Meloberry.UnitTests/LayoutAndWebAppTests.cs`:
  - Verify `katex.min.css` excludes pruned font families and `.woff`/`.ttf` references.
  - Verify minimal text-only `index.html` omits `mby-ico-zoom`, `mby-ico-eye`, `mby-ico-eye-off` while keeping `mby-ico-save`.
  - Verify headless CDP master showcase passes with 0 errors across the updated `case`, `relabel`, `match`, `classify`, `numeric`, and `flashcard` widgets.
- `tests/Meloberry.UnitTests/AddressPatchAndLintTests.cs`:
  - Verify `--dry-run` on an existing workspace with images validates cleanly without duplicating existing `assets/*` into temp staging.
- `tests/Meloberry.UnitTests/PayloadAndMarkdownTests.cs`:
  - Verify `:::flashcard{cloze=true}` (Markdown + JSON) compiles front `[...]` and back `**answer**` accurately and rejects `cloze=true` when no `{{...}}` blanks exist.
- `tests/Meloberry.CliTests/CliContractAndHelpTests.cs`:
  - Verify new `@q:recall`, `@q:choice`, `@q:relational`, `@masks`, `@tabular`, `@authoring`, `@pipeline`, `@styling` selectors return exact subsets while `@content`, `@questions`, `@other` remain 100% unchanged and `mby prime` stays `< 1550` CRLF chars.

---

## Master File Manifest

| Phase | Layer | Files Touched |
| :---: | :--- | :--- |
| **1** | Domain | `Phase2Payloads.cs`, `LayoutBoundsCatalog.cs` |
| **1** | Application | `PatchEntityUseCase.cs`, `JsonQuestionMaterializer.cs`, `DiagnosticsUseCases.cs`, `GetSchemaUseCase.cs` |
| **1** | Infrastructure | `SqliteLayoutRepository.cs`, `MarkdownSpecParser.cs`, `DeclarativeSpecLoader.cs`, `SlidingWebAppGenerator.cs`, `QuestionSpecs.Set1.cs`, `QuestionSpecs.Set2.cs`, `QuestionSpecs.Set3.cs` |
| **1** | CLI & Docs | `LocalPreviewHost.cs`, `docs/README.md`, `docs/JSON_AND_PACKAGES.md`, `docs/LAYOUTS_LINT_AND_WEBAPP.md`, `WEB_OPTIMIZATIONS_PLAN.md` |
| **2** | Domain | `Question.cs`, `Phase2Payloads.cs`, `Phase3Payloads.cs`, `TextSimilarity.cs` |
| **2** | Application | `GetSchemaUseCase.cs`, `GetPrimeGuideUseCase.cs`, `JsonNodeReaders.cs`, `JsonQuestionMaterializer.cs` |
| **2** | Infrastructure | `MarkdownSpecParser.cs`, `DeclarativeSpecLoader.cs`, `MbypkgExporter.cs`, `SqliteWorkspaceRepository.cs`, `WebAppSharedStyles.cs`, `AuxiliarySpecs.cs`, `BlockSpecs.cs`, `QuestionSpecs.Set1.cs`, `QuestionSpecs.Set2.cs`, `QuestionSpecs.Set3.cs`, `SlidingWebAppGenerator.cs` |
| **3** | Application | `Ports.cs`, `DiagnosticsUseCases.cs`, `ApplyDeclarativeSpecUseCase.cs`, `GetSchemaUseCase.cs` |
| **3** | Infrastructure | `WebComponentModule.cs`, `WebAppSharedStyles.cs`, `SlidingWebAppGenerator.cs`, `MarkdownSpecParser.cs`, `DeclarativeSpecLoader.cs`, `CdpHeadlessWebAppVerifier.cs`, `QuestionSpecs.Set1.cs`, `QuestionSpecs.Set2.cs`, `QuestionSpecs.Set3.cs` |
| **3** | CLI & Docs | `LocalPreviewHost.cs`, `docs/COMMANDS.md`, `docs/JSON_AND_PACKAGES.md` |
| **1–3** | Tests | `AddressPatchAndLintTests.cs`, `LayoutAndWebAppTests.cs`, `PayloadAndMarkdownTests.cs`, `PersistenceAndPackagingTests.cs`, `CliContractAndHelpTests.cs` |

---

## Verification Gate After Each Phase
1. `dotnet build -c Release` — 0 warnings (`TreatWarningsAsErrors=true`).
2. `dotnet test` — 100% pass across `Meloberry.ArchitectureTests`, `Meloberry.UnitTests` (including live CDP verification), and `Meloberry.CliTests`.
3. Spot-check `mby prime` CRLF length (`< 1550` chars) and `mby spec @q:recall @masks --json` token footprint.