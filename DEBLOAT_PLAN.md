# Meloberry CLI (`mby` v2.0.0) — Concrete Deduplication & Debloating Plan

**Goal:** Reduce non-vendor codebase size by **~35% (~6,400 LOC)** across `tests/` (-50%–55%), `src/` (-24%–28%), and obsolete root docs, while maintaining **100% feature parity**, identical CLI/JSON/Markdown/`.mbypkg`/SQLite wire contracts, and passing all unit, CLI, architecture, and headless CDP tests at every step.

---

## Non-Negotiable Safety Rails (Must Hold After Every Phase)

1. **Zero Wire/Contract Breakage:** All 14 CLI verbs, flags, exit codes (`0`–`5`), error codes (`Lint.*`, `Markdown.*`, `Json.*`, `Patch.*`, `Batch.*`, `Package.*`, `Address.*`, `Layout.*`), minified JSON envelopes, and `mby prime` char budget (`< 1550` CRLF chars) remain byte-for-byte compatible.
2. **Storage & Package Compatibility:** Existing SQLite `.db` schemas (`WorkspaceMeta`, `Components`, `ArticleBlocks`, `Questions`, `McqOptions`) and `.mbypkg` `v1.0`/`v2.0` import/export remain untouched.
3. **Clean Architecture Invariants (`CleanCliDependencyRuleTests`):**
   - `Meloberry.Domain` remains pure .NET BCL (no SQLite, Markdig, DI, or `Console`; no public entity constructors or setters).
   - `Meloberry.Application` does not reference `Infrastructure`, `Cli`, SQLite, or Markdig.
   - `Meloberry.Cli.Commands` never injects Infrastructure types or uses `System.Console`.
4. **Atomic Commits:** Each phase below is an independent, green-build PR/commit gated by `dotnet test`.

---

## Phase 0 — Fix Latent Runtime Bugs & Establish a Clean Baseline
*Estimated delta: ~0 LOC | Effort: Small*

Before refactoring anything, fix the 3 latent defects exposed during the audit so the test suite is a trustworthy safety net:

1. **`src/Meloberry.Infrastructure/Web/SlidingWebAppGenerator.cs`**
   - Define `function createExplanationBox(html, initiallyVisible = false)` in `BuildJs()` (called by `buildClassifyCard`, `buildMatchCard`, `buildNumericCard`, `buildCaseGroupCard`—currently undefined).
   - Fix `openLightbox(diag.imagePath, ...)` → `openZoomLightbox(diag.imagePath, ...)` inside `buildDiagramExploreBlock`.
2. **`tests/Meloberry.UnitTests/Phase3ComponentTests.cs`**
   - Fix `Assert.Contains("\"componentIndex\":2", indexHtml)` → `Assert.Contains("\"componentIndex\":2", appJs)` (since `DATA` lives in `app.js`).
3. **Gate:** Run `dotnet test` (including headless CDP verification with Edge/Chrome) and confirm 100% pass.

---

## Phase 1 — Shared Test Infrastructure & Test Suite Consolidation
*Estimated delta: **-3,200 to -3,400 LOC** (24 files → 10 files) | Effort: Medium*

Refactor tests *first* so every subsequent `src/` refactor runs against a fast, deduplicated, non-brittle test suite.

### 1.1 Create Shared Test Support
Add `tests/TestSupport/` (linked into `Meloberry.UnitTests.csproj` and `Meloberry.CliTests.csproj` via `<Compile Include="..\TestSupport\*.cs" />`, or a small `Meloberry.TestSupport` project):
- **`TestAssets.cs`**: Single `ValidPngBytes` constant (replaces 5 Base64 copies + 10+ raw header arrays), `WritePngAsync(path)`, `WriteMasksSidecarAsync(...)`, `WriteMelomgMetaAsync(...)`.
- **`WorkspaceTestFixture.cs`** (`IDisposable`):
  - Owns `_tempRoot`, `_appDataDir`, `_workDir`, automatic `SqliteConnection.ClearAllPools()` + directory cleanup.
  - `CreateProvider(subDir)` & `CreateScope()`.
  - `PushCwd(dir)` returning an `IDisposable` CWD guard (replaces 12+ `try/finally SetCurrentDirectory` blocks).
  - `MutateZipAsync(srcPkg, dstPkg, ...)` helper for `.mbypkg` tampering/BOM/ZipSlip tests (replaces 8 manual `ZipFile.Open(..., Update)` loops).
  - Single `StubSpecLoader` implementation (replaces 2 identical copies).

### 1.2 Consolidate `Meloberry.UnitTests` (14 files → 7 files)
Keep untouched (already lean):
- `DomainAndOrderingTests.cs`
- `FileContentInspectorTests.cs`
- `MonotonicIdGeneratorTests.cs`

Merge the remaining 11 chronological files into **4 cohesive feature suites**, converting sequential error-case blocks into `[Theory]` / `[MemberData]` tables:

| New File | Absorbs & Deletes | Deduplication Focus |
| :--- | :--- | :--- |
| **`PayloadAndMarkdownTests.cs`** | Parser/payload tests from `V12FeatureTests`, `Phase0FoundationTests`, `Phase1ComponentTests`, `Phase2HardeningTests`, `Phase2DiffAndMechanismTests`, `Phase2RelabelAndIntegrationTests`, `Phase3ComponentTests`, `WebAppEnhancementsUnitTests` | Single `[Theory]` table for KaTeX/pipe/escape/fence/tolerance/URI parsing + Markdown directive validation & typo hints + directory manifest/sidecar ignore rules. |
| **`AddressPatchAndLintTests.cs`** | `AddressAndPatchTests`, lint/patch sections of `LintAndExportGuardTests`, `Phase1ComponentTests`, `Phase2HardeningTests`, `Phase2DiffAndMechanismTests`, `Phase2RelabelAndIntegrationTests`, `Phase3ComponentTests` | Single workspace setup covering `#path`/`#ref`/reorder/move, parameterized invalid patch checks, and a unified lint matrix covering all 6 block types + 12 question types + `mby://` links. |
| **`PersistenceAndPackagingTests.cs`** | SQLite migration + `.mbypkg` tests from `Phase0FoundationTests`, `LintAndExportGuardTests`, `V12FeatureTests`, `Phase1ComponentTests`, `Phase2HardeningTests`, `Phase2DiffAndMechanismTests`, `Phase2RelabelAndIntegrationTests`, `Phase3ComponentTests` | Single legacy DB migration test, single v1.0/v2.0/BOM round-trip covering all component types & assets, and parameterized `[Theory]` for corrupt/tampered/ZipSlip/unsupported-version archives. |
| **`LayoutAndWebAppTests.cs`** | `LayoutEditorAndProfilesTests`, WebApp/CDP/Report tests from `LintAndExportGuardTests`, `V12FeatureTests`, `Phase1ComponentTests`, `Phase2HardeningTests`, `Phase2DiffAndMechanismTests`, `Phase2RelabelAndIntegrationTests`, `Phase3ComponentTests` | Replace 6 separate Chromium CDP launches with **2 runs**: (1) one master showcase session exercising *all* 6 block types, *all* 12 question types, KaTeX in titles/answers/tables, images, and native links; (2) one negative test injecting overflow into Q2 to prove CDP catches it. Drop brittle JS/CSS identifier string greps. |

### 1.3 Consolidate `Meloberry.CliTests` (10 files → 3 files)
Keep untouched:
- `LayoutEditorHostAndCliTests.cs` *(already focused HTTP API/CSRF/atomicity suite)*

Merge the remaining 9 files into **2 focused CLI-boundary suites**:

| New File | Absorbs & Deletes | Deduplication Focus |
| :--- | :--- | :--- |
| **`CliContractAndHelpTests.cs`** | `V12CliFeatureTests`, `SpecAuditRegressionTests`, `Phase2HardeningCliTests`, plus all `prime`/`spec`/`--help`/`--timeout`/dispatcher-envelope checks from `AgentCliV2Tests`, `Phase1CliTests`, `Phase2BDiffAndMechanismCliTests`, `Phase2CliTests`, `Phase3CliTests` | **One** test for `prime` budget + topics; **one** `[Theory]` for all 14 `--help` verbs; **one** test for `spec` topics & `@groups`; **one** test for exit codes, unknown flags, BOM stdin, `--ephemeral`, `ls --all`, `batch` rollback & `$prev.id`, and `pkg` trailing-slash resolution. **Delete `Phase2BDiffAndMechanismCliTests.cs` completely** (100% superseded). |
| **`CliEndToEndWorkflowTests.cs`** | E2E workflows from `AgentCliV2Tests`, `Phase1CliTests`, `Phase2CliTests`, `Phase3CliTests`, `WebAppEnhancementsCliTests` | **One** golden modular-directory E2E session (combining articles, tables, diagram-explore, reveal, native links, and all quiz types) running `apply → check → get (outline/full/ids) → patch → build --static-only → analyze --static-only → pkg export/import → rm`, plus **one** CLI lint-failure exit-`2` envelope test and **one** corrupted-asset `analyze --static-only` failure test. |

- **Gate:** `dotnet test` passes, coverage of every error code and feature branch verified, total test runtime significantly faster.

---

## Phase 2 — Extract Shared Domain & Cross-Cutting Helpers
*Estimated delta: **-400 LOC** (foundation for Phases 3–5) | Effort: Small*

Create single-source-of-truth utilities in the proper Clean Architecture layers before trimming the 5 monoliths:

### 2.1 Domain Layer (`src/Meloberry.Domain/`)
1. **`Common/TextSimilarity.cs`** *(pure BCL)*:
   - `FindClosest(string input, IEnumerable<string> candidates, int maxDistance = 2)` + `Levenshtein(string a, string b)`.
   - Removes verbatim duplicates in `PatchEntityUseCase.cs` and `DeclarativeSpecLoader.cs`.
2. **`Common/MarkdownFenceHelper.cs`** *(pure BCL)*:
   - `TryOpenCodeFence(string trimmedLine, out string token)` and `IsCloseCodeFence(string trimmedLine, string openToken)`.
   - Removes 3 separate copies in `ArticleLinkRules`, `Phase3PayloadRules`, and `MarkdownSpecParser`.
3. **`Payloads/PayloadCodec.cs` & Normalized Box Helper**:
   - Unify `HasValidNormalizedBox(double x, double y, double w, double h)` (standardizing tolerance to `1.0001` or `1.001` consistently—use `1.001` so no previously valid relabel box breaks!).
   - Replace duplicate `Phase2PayloadRules.HasValidRelabelBoxBounds` and `Phase3PayloadRules.HasValidHotspotBounds` with calls to `HasValidNormalizedBox`.
   - Add **`Payloads/CaseSubQuestionExtensions.cs`**: single `RehydrateToDomainQuestion(CaseSubQuestion sub, string subId, string containerId)` method used by both `LintHandler` (`DiagnosticsUseCases.cs`) and `SlidingWebAppGenerator.cs` (eliminates duplicate 30-line rehydration switch).
   - *(Optional clean rename)* Merge `Phase1Payloads.cs` / `Phase2Payloads.cs` / `Phase3Payloads.cs` into `ArticlePayloads.cs` + `QuizPayloads.cs` + `PayloadCodec.cs`.

### 2.2 Application Layer (`src/Meloberry.Application/`)
1. **`UseCases/Json/JsonNodeReaders.cs`** *(internal static)*:
   - `TryGetProp(JsonObject obj, string name, out JsonNode? value)`
   - `TryReadNullableString(JsonObject obj, string key, out string? value, out Error? err)`
   - `TryReadRequiredString(JsonObject obj, string key, out string value, out Error? err)`
   - `TryReadBool(JsonObject obj, string key, out bool value, out Error? err)`
   - `TryReadStringArray(JsonObject obj, string key, bool allowNull, bool allowEmpty, out List<string> values, out Error? err)`
   - `TryReadNormalizedRect(JsonObject obj, string path, out double x, out double y, out double w, out double h, out Error? err)` *(handles `rect: [x,y,w,h]` and `x/y/w/h` uniformly)*.
2. **`Abstractions/Ports.cs` (`PathConfinement`)**:
   - Add `TryResolveConfinedInputFile(string rawPath, string componentBaseDir, string? specRootDir, string? cwdDir, out string resolvedPath)` so `JsonQuestionMaterializer.CopyConfinedImageAsync` and `ResolveConfinedSidecarFile` share the exact same 35-line multi-root confinement routine.

### 2.3 CLI & Infrastructure Micro-Utilities
1. **`src/Meloberry.Cli/Abstractions/CliCommandHelpers.cs`**:
   - Add `TryParseTimeoutOption(CliInvocationContext ctx, out int timeout, out int? errorExit)` (replaces 3 copies in `ApplyCliCommand`, `BuildCliCommand`, `AnalyzeCliCommand`).
2. **`src/Meloberry.Infrastructure/Web/WebHostUtilities.cs`**:
   - Single `GetMimeType(string ext)` dictionary and `FindFreeLoopbackPort()` / `FindFreeLoopbackPortPair()`.
   - Reused by both `CdpHeadlessWebAppVerifier.cs` and `LocalPreviewHost.cs`.

- **Gate:** `dotnet test` passes (including `CleanCliDependencyRuleTests`).

---

## Phase 3 — Slim Down the Parsing & Patching Monoliths
*Estimated delta: **-1,350 LOC** | Effort: Medium–High*

### 3.1 `src/Meloberry.Infrastructure/Services/MarkdownSpecParser.cs` (~1,180 → ~620 LOC, **-560 LOC**)
1. **Shared directive parsing helpers (eliminates ~250 LOC):**
   - `TryParseBoolAttr(attrs, key, defaultVal, openLine, sourceLabel, out bool val, out Error? err)` — replaces 15+ inline `bool.TryParse` blocks.
   - `TryResolveMutuallyExclusiveAttr(attrs, primaryKey, aliasKey, directiveName, openLine, sourceLabel, out string? val, out Error? err)` — replaces 7 copies of `image`/`imagePath` and `masks`/`masksPath` checks.
   - `ExtractSingleStandaloneImage(string body, string? existingImagePath, string directiveName, int openLine, string sourceLabel, out string cleanedText, out string? imagePath, out string? caption, out Error? err)` — replaces **6 copy-pasted code-fence + standalone-image loops** (`mcq`, `flashcard`, `spot-error`, `link`, `numeric`, `case`).
   - `TryParseLayoutOverrides(body, openLine, sourceLabel, out Dictionary<string, string>? overrides, out Error? err)` — replaces 10 identical `if (body.SubSections.TryGetValue("layout", ...))` blocks.
2. **Unified inline YAML `boxes:` parser (eliminates ~110 LOC):**
   - Single `ParseYamlBoxes(...)` helper supporting `label`, `rect`/`x,y,w,h`, optional `aliases` (for `relabel`), and optional `note` (for `diagram-explore`) with an allowed-key set per caller.
3. **Table-driven directive dispatch in `ParseBody` (eliminates ~200 LOC):**
   - Define `private sealed record DirectiveRule(string CanonicalName, DirectiveTarget Target, HashSet<string> AllowedAttrs, HashSet<string> AllowedSubSections, ...)` and a `Dictionary<string, DirectiveRule>` mapping aliases (`fillblank` → `fill-blank`, `differentiation` → `diff`, `relabel-image` → `relabel`, `bucket-sort` → `classify`, etc.).
   - One ~35-line generic dispatch path handles `expectQuiz`/`expectArticle` checks, `posArg` rejection, attribute validation, `ReadDirectiveBody`, and `FlushText()` for all 14 standard directives (leaving only `callout` shorthands and `case` quad-colon reader as small special cases).

### 3.2 `src/Meloberry.Application/UseCases/Json/PatchEntityUseCase.cs` (~835 → ~420 LOC, **-415 LOC**)
1. Rewrite `ApplyTableBlockPatch`, `ApplyDiagramExploreBlockPatchAsync`, `ApplyRevealBlockPatch`, and every branch of `ApplyPayloadQuestionPatchAsync` to use Phase 2's `JsonNodeReaders` helpers (`TryReadNullableString`, `TryReadRequiredString`, `TryReadBool`, `TryReadStringArray`, `TryReadNormalizedRect`).
2. Extract `TryCopyPatchedImageAsync(...)` for `articleBlock:image`, `diagram-explore`, `mcq`, `spot-error`, `relabel`, `numeric`, `case`.
3. Replace inline `Levenshtein`/`FindClosest` with `TextSimilarity.FindClosest`.

### 3.3 `src/Meloberry.Infrastructure/Services/DeclarativeSpecLoader.cs` (~615 → ~380 LOC, **-235 LOC**)
1. **Eliminate the 18-boolean-flag matrix in `MapQuestions`:**
   - Parse each question object with a static `Dictionary<QuestionType, HashSet<string>> AllowedQuestionKeys` (identical pattern to `PatchEntityUseCase.AllowedKeysByKind`), validating disallowed keys in a 5-line loop instead of maintaining 18 booleans × 12 `if (...)` matrices.
2. **Use shared `TextSimilarity.FindClosestKey` and `TryReadNormalizedRect`** (eliminating duplicate rect parsing in `diagram-explore` and `relabel`).
3. Keep exact error codes (`Json.ParseError` + typo `Hint`, `Json.InvalidFieldForType`, `Json.InvalidQuestion`, `Json.InvalidSpec`).

### 3.4 `src/Meloberry.Application/UseCases/Json/ApplyDeclarativeSpecUseCase.cs` & `JsonQuestionMaterializer.cs` (~925 → ~785 LOC, **-140 LOC**)
1. Extract a shared `ExecuteWithStagingAndAssetRollbackAsync(...)` helper in `ApplyDeclarativeSpecUseCase.cs` used across `AppendToSessionAsync`, `AppendToComponentAsync`, and `AppendToQuizAsync` (removes 3 copies of dry-run staging + `CaptureExistingAssets` + `finally` cleanup).
2. Remove duplicated confinement lookup in `JsonQuestionMaterializer.cs` via Phase 2's `PathConfinement.TryResolveConfinedInputFile`.

- **Gate:** `dotnet test` passes 100%; verify every `Markdown.*`, `Json.*`, `Patch.*`, and `Asset.*` error code matches pre-refactor tests.

---

## Phase 4 — Deduplicate WebApp Generator, Schema Metadata & Packaging
*Estimated delta: **-1,000 LOC** | Effort: Medium*

### 4.1 `src/Meloberry.Infrastructure/Web/SlidingWebAppGenerator.cs` & `WebAppSharedStyles.cs` (~1,760 → ~1,300 LOC, **-460 LOC**)
1. **JS Widget Deduplication (in `SlidingWebAppGenerator.BuildJs()`):**
   - Standardize and reuse 4 small JS helpers across *all 10* graded question builders:
     - `createExplanationBox(html, visible)` *(added in Phase 0 — now replace the 7 inline copies in `mcq`, `fill-blank`, `assertion-reason`, `spot-error`, `diff`, `mechanism`, `relabel`!)*
     - `recordGradeAttempt(metric, isCorrect)` *(replaces 10 copies of `if (m) { m.checkAttempts = ...; if (m.firstTryCorrect === null) ...; m.isCorrect = ok; }`)*
     - `setFeedbackMessage(el, text, isOk)` *(replaces 10 copies of inline `feedback.style.color` / `fontWeight`)*
     - `bindDropTarget(el, mimeType, onDropData)` *(replaces 7 repetitive `dragover`/`dragleave`/`drop` blocks across `diff`, `mechanism`, `relabel`, `classify`, `match`)*
2. **CSS State Consolidation (`WebAppSharedStyles.BuildCoreCss()`):**
   - Merge repeated `.selected` / `.correct` / `.wrong` / `.used` border & background declarations into shared grouped selectors across `.mby-option-btn`, `.mby-error-chip`, `.mby-diff-cell-btn`, `.mby-classify-chip`, `.mby-match-slot`, `.mby-case-tab`, `.mby-relabel-assign-row`, `.mby-mech-slot`.
3. **C# Polymorphic Tree Walk Cleanup:**
   - Replace duplicate `CaseSubQuestion` rehydration in `BuildQuestionSlide` with Phase 2's `CaseSubQuestionExtensions.RehydrateToDomainQuestion`.
   - Reuse a small `EnumerateSlideHtmlStrings(SlideRenderModel)` helper for `SlideContainsMath` (and reuse `Phase3PayloadRules.EnumerateArticleBlockMarkdownStrings` where appropriate).

### 4.2 `src/Meloberry.Application/UseCases/Diagnostics/GetSchemaUseCase.cs` (~640 → ~380 LOC, **-260 LOC**)
1. Extract shared `SchemaFieldSpec` constants (`RefField`, `ExplanationField`, `McqLayoutOverridesField`, `ArticleLayoutOverridesField`, `OptionalTitleField`) instead of re-instantiating identical field descriptors 15+ times.
2. Convert the 27 `Build*Schema()` methods into a compact declarative table of topic definitions, preserving **100% identical** JSON output (`Topic`, `CompactSignature`, `Command`, `Summary`, `DisallowUnmappedProperties`, `Fields`, `Example`, `Scope`, `Category`).

### 4.3 `src/Meloberry.Infrastructure/Packaging/MbypkgExporter.cs` & `DiagnosticsUseCases.cs` (~1,040 → ~860 LOC, **-180 LOC**)
1. Extract a shared domain/application payload structural check (or helper methods per payload type) so `MbypkgExporter.IsValidPackagePayloadQuestion` and `LintHandler.LintPayloadQuestionAsync` stop maintaining parallel deserialization + null-guard logic.
2. Deduplicate `CollectQuizAssets` / `CaseGroup` nested asset traversal by reusing `CaseSubQuestionExtensions.RehydrateToDomainQuestion` + single-question asset collector `CollectQuestionAssets(Question)`.

### 4.4 `src/Meloberry.Infrastructure/Web/LayoutEditorPageBuilder.cs` & `LocalPreviewHost.cs` (~930 → ~830 LOC, **-100 LOC**)
- Reuse `WebHostUtilities` MIME map & port finder in `LocalPreviewHost.cs`.
- Move `.mby-zoom-modal` shared CSS into `WebAppSharedStyles.BuildCoreCss()` so `LayoutEditorPageBuilder.cs` and `SlidingWebAppGenerator.cs` don't duplicate the lightbox stylesheet.

- **Gate:** `dotnet test` passes 100%, including `LayoutEditorHostAndCliTests`, `PersistenceAndPackagingTests`, and full headless Chromium CDP verification.

---

## Phase 5 — Repository Hygiene & Final Verification
*Estimated delta: **-300 LOC** | Effort: Trivial*

1. **Remove obsolete planning doc:** Delete `PROPOSED_COMPONENT_SPECS.md` (outdated v1.3 roadmap—all 13 features are shipped in v2.0 and documented in `docs/`).
2. **Final Verification Checklist:**
   - `dotnet build -c Release` succeeds with **0 warnings** (`TreatWarningsAsErrors` enabled).
   - `dotnet test` passes **100%** across `Meloberry.ArchitectureTests`, `Meloberry.UnitTests`, and `Meloberry.CliTests`.
   - `mby prime` output remains `< 1550` CRLF chars and lists all 28 topics.
   - `mby spec --json` returns all 28 topics with unchanged `scope`, `category`, and field limits.
   - Headless CDP verification (`mby build <showcase> -o <out> --verify --json`) passes with 0 console errors, 0 broken assets, 0 overflow issues, and 0 KaTeX errors across all 6 article blocks and all 12 question types.

---

## Summary of Expected Savings

| Phase | Scope | Est. LOC Removed | Risk |
| :---: | :--- | ---: | :--- |
| **0** | Fix `createExplanationBox`, `openZoomLightbox`, `Phase3ComponentTests` bug | ~0 | Very Low |
| **1** | Shared test fixture + consolidate 24 test files → 10 | **~3,300** | Low |
| **2** | Shared Domain/App/Infra utilities (box bounds, fences, Levenshtein, readers, MIME) | **~400** | Low |
| **3** | Refactor `MarkdownSpecParser`, `PatchEntityUseCase`, `DeclarativeSpecLoader`, `Apply`/`Materializer` | **~1,350** | Medium |
| **4** | Refactor `SlidingWebAppGenerator`, `WebAppSharedStyles`, `GetSchemaUseCase`, `MbypkgExporter`, `LayoutEditor` | **~1,000** | Medium |
| **5** | Remove stale `PROPOSED_COMPONENT_SPECS.md` + final QA gates | **~300** | Very Low |
| **Total** | **100% Feature Parity Preserved** | **~6,350 LOC (~35%)** | **Controlled** |