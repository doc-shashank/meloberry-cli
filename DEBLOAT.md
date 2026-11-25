# Meloberry CLI (`mby` v2.0.0) — Codebase Bloat & Reduction Audit

## Executive Summary

Your intuition is accurate: **the codebase has accumulated meaningful "phase-layering" bloat**, and **the test suite is the single largest source of waste**.

Excluding vendored KaTeX assets (`src/Meloberry.Infrastructure/Web/Assets/katex/`), the repository contains **~18,300 lines** of authored code, tests, and documentation. Approximately **34%–38% (~6,400 lines) can be removed with 100% feature parity** and zero loss in behavioral test coverage:

| Area | Current Size (approx. LOC) | Target Size (approx. LOC) | Removable | Bloat Severity |
| :--- | ---: | ---: | :---: | :--- |
| **`tests/`** (24 files) | ~6,530 | ~3,100 | **50%–55%** | **Critical** |
| **`src/Meloberry.Application`** | ~3,000 | ~2,050 | **30%–35%** | **High** |
| **`src/Meloberry.Infrastructure`** *(excl. KaTeX)* | ~5,400 | ~3,900 | **25%–30%** | **High** |
| **`src/Meloberry.Domain`** | ~1,600 | ~1,400 | **10%–15%** | **Low** |
| **`src/Meloberry.Cli`** | ~1,150 | ~1,050 | **8%–10%** | **Very Low** |
| **Docs & Root Specs** | ~680 | ~380 | **~44%** | **Moderate** *(stale roadmap)* |
| **Total (Non-Vendor)** | **~18,360** | **~11,880** | **~35%** | **High (Test-Skewed)** |

> **What is already lean:** `Meloberry.Domain` core entities (`StudySession`, `Quiz`, `McqQuestion`, `IndexedListReorderer`), `Meloberry.Cli` command dispatch/handlers, `SqliteWorkspaceRepository`, `ChainTransactionManager`, and `CleanCliDependencyRuleTests` are well-structured and cleanly layered. Almost all bloat comes from **incremental Phase 0 → 1 → 2A → 2B → 2C → 3 feature accretion** without a consolidation pass.

---

## 1. Test Suite Bloat Audit (~50%–55% Removable)

The test suite (~6,530 lines across 24 files) accounts for **~36% of the entire repository**—and over half of it is structural duplication.

### Root Causes of Test Bloat

#### 1. Chronological "Phase Archaeology" Instead of Feature Suites
Every development sprint added new unit and CLI test files rather than extending existing domain suites:
- `V12FeatureTests` + `V12CliFeatureTests`
- `Phase0FoundationTests`
- `Phase1ComponentTests` + `Phase1CliTests`
- `Phase2HardeningTests` + `Phase2HardeningCliTests`
- `Phase2DiffAndMechanismTests` + `Phase2BDiffAndMechanismCliTests`
- `Phase2RelabelAndIntegrationTests` + `Phase2CliTests`
- `Phase3ComponentTests` + `Phase3CliTests`
- `SpecAuditRegressionTests`
- `WebAppEnhancementsUnitTests` + `WebAppEnhancementsCliTests`

Because each phase file re-establishes full-pipeline confidence from scratch, the same workflows are asserted repeatedly across files.

#### 2. Direct Cross-File Supersession
- **`Phase2BDiffAndMechanismCliTests.cs` (~170 lines) is almost 100% superseded by `Phase2CliTests.cs` (~215 lines).** Both test `prime` char budget, `spec --help`, `spec diff mechanism ... --json`, `apply → check → get → patch → build --static-only → pkg export → pkg import`, and CLI lint failure exit code `2` on `diff` + `mechanism` (with `Phase2CliTests` simply adding `relabel` to the exact same flow).
- **`mby prime` and `mby spec` assertions are duplicated across 10 different files** (`AgentCliV2Tests`, `V12CliFeatureTests`, `SpecAuditRegressionTests`, `Phase1ComponentTests`, `Phase1CliTests`, `Phase2DiffAndMechanismTests`, `Phase2BDiffAndMechanismCliTests`, `Phase2RelabelAndIntegrationTests`, `Phase2CliTests`, `Phase3ComponentTests`, `Phase3CliTests`). One dedicated `PrimeAndSpecTests.cs` replaces all of them.
- **Headless CDP browser verification runs in 6 separate tests** (`V12FeatureTests`, `Phase1ComponentTests`, `Phase2HardeningTests`, `Phase2DiffAndMechanismTests`, `Phase2RelabelAndIntegrationTests`, `Phase3ComponentTests`), launching Chromium up to 6 times for overlapping widget subsets. One or two comprehensive showcase fixtures would verify all 6 block types + all 12 question types + KaTeX + images + links + overflow detection in a fraction of the code and runtime.

#### 3. "UnitTests" Mirroring "CliTests" End-to-End
Most files in `Meloberry.UnitTests` are actually **full-stack integration tests** (DI container + SQLite + filesystem + Markdig + ZIP + HTML + CDP) rather than isolated unit tests. Then `Meloberry.CliTests` repeats the exact same `apply → check → get → patch → build --static-only → pkg export → pkg import` pipeline for each phase:
- `Phase1ComponentTests` ↔ `Phase1CliTests`
- `Phase2DiffAndMechanismTests` + `Phase2RelabelAndIntegrationTests` ↔ `Phase2BDiffAndMechanismCliTests` + `Phase2CliTests`
- `Phase3ComponentTests` ↔ `Phase3CliTests`
- `WebAppEnhancementsUnitTests` ↔ `WebAppEnhancementsCliTests`

CLI tests should only test the CLI boundary (argument/flag parsing, exit codes, minified JSON envelopes, stdin/BOM, CWD resolution, and 1–2 golden E2E smoke flows), not re-test every component type's lint/patch/packaging matrix.

#### 4. Zero Shared Test Infrastructure (Massive Copy-Paste Boilerplate)
There is no `TestHelpers` / `WorkspaceTestFixture` class anywhere in `tests/`:
- **Base64 `ValidPngBytes`** (~700 chars) is copy-pasted verbatim in **5 files** (`Phase2CliTests`, `Phase3CliTests`, `WebAppEnhancementsCliTests`, `Phase2RelabelAndIntegrationTests`, `Phase3ComponentTests`), plus raw PNG byte arrays are inlined another 10+ times.
- **`_tempRoot` / `_appDataDir` + `IDisposable` cleanup** is repeated in ~18 test classes.
- **`CreateProvider(...)`** DI setup is repeated in 9+ files.
- **`StubSpecLoader`** inner class is copy-pasted verbatim in both `Phase2HardeningTests.cs` and `Phase2RelabelAndIntegrationTests.cs`.
- **`Directory.SetCurrentDirectory` try/finally guards** are repeated 12+ times.
- **Manual ZIP tampering boilerplate** (`ZipFile.Open(..., Update)` + rewriting `manifest.json`/`workspace.json`) is repeated ~8 times across 5 files.
- **Repetitive inline error-case blocks** (e.g., testing 6 invalid Markdown/JSON variants sequentially inside giant 200-line `[Fact]` methods) should be concise `[Theory]` / `[MemberData]` tables.

#### 5. Brittle Implementation-Detail Assertions on Generated JS/CSS
Tests like `WebAppEnhancementsCliTests.cs`, `LintAndExportGuardTests.cs`, `Phase2HardeningTests.cs`, and `LayoutEditorAndProfilesTests.cs` contain dozens of brittle string greps against generated JS/CSS internals (e.g., `Assert.Contains("animateSeqUpdate", js)`, `Assert.Contains("itemMatchesNorm", js)`, `Assert.Contains("box-shadow: none;", css)`, `Assert.Contains("triggerFlip.cancel = () =>", html)`). These inflate test LOC without verifying runtime behavior.

### Recommended Test Consolidation Map (24 Files → 10 Files)
- **Keep as-is (lean):** `CleanCliDependencyRuleTests.cs`, `DomainAndOrderingTests.cs`, `FileContentInspectorTests.cs`, `MonotonicIdGeneratorTests.cs`.
- **Add shared fixture:** `TestSupport/WorkspaceTestFixture.cs` (temp dirs, DI builder, `ValidPngBytes`, ZIP mutator, CWD scope, `StubSpecLoader`).
- **Consolidate `Meloberry.UnitTests` (14 → 7 files):**
  - `DomainAndOrderingTests.cs` + `MonotonicIdGeneratorTests.cs` + `FileContentInspectorTests.cs`
  - `PayloadRulesAndParserTests.cs` *(merge all Markdown/JSON/payload parsing + Phase 0–3 parser edge cases as `[Theory]`)*
  - `AddressPatchAndLintTests.cs` *(merge `AddressAndPatchTests`, `LintAndExportGuardTests`, and all Phase 1–3 lint/patch cases)*
  - `PackagingAndPersistenceTests.cs` *(merge SQLite migration + `.mbypkg` v1/v2 round-trip + tamper/ZipSlip guards)*
  - `LayoutAndWebAppTests.cs` *(merge `LayoutEditorAndProfilesTests`, `WebAppEnhancementsUnitTests`, and 1–2 unified headless CDP showcase tests)*
- **Consolidate `Meloberry.CliTests` (10 → 3 files):**
  - `CliContractAndHelpTests.cs` *(all `prime`, `spec`, `@groups`, `--help`, flags, exit codes, JSON envelopes, `ls`, `version`, `batch`, `SpecAuditRegressionTests`)*
  - `CliEndToEndWorkflowTests.cs` *(single comprehensive multi-phase session covering apply/get/patch/check/build/pkg/rm/analyze)*
  - `LayoutEditorHostAndCliTests.cs` *(already focused HTTP API + CSRF + atomicity suite)*

---

## 2. Production Code Bloat Audit (~24%–28% Removable)

The production codebase (~11,150 non-vendor lines) is clean architecturally, but **5 monolithic files** account for **~5,100 lines (~46% of all `src/`)** and contain heavy copy-paste boilerplate:

### Hotspot 1: `src/Meloberry.Infrastructure/Services/MarkdownSpecParser.cs` (~1,180 lines → ~620 lines, **-47%**)
- **`ParseBody` has 16 nearly identical `if (dirLower is ...)` blocks** (~300 lines) repeating `expectArticle`/`expectQuiz` checks, `posArg` rejection, `ValidateDirectiveAttributes`, `ReadDirectiveBody`, `FlushText()`, and list insertion. A static directive-descriptor table eliminates ~200 lines immediately.
- **Standalone image extraction + code-fence tracking** is copy-pasted **6 times** (`ParseMcqDirective`, `ParseFlashcardFace`, `ParseSpotErrorDirective`, `ParseLinkDirective`, `ParseNumericDirective`, `ParseCaseDirective`).
- **`bool.TryParse` attribute parsing** with identical error construction is repeated **15+ times** (only `ParseDiffDirective` bothered to define a local `ParseBoolAttr` helper!).
- **`image` vs `imagePath` mutual exclusion** is repeated 5 times; **`masks` vs `masksPath`** is repeated 2 times.
- **`:::layout` subsection parsing** (`if (body.SubSections.TryGetValue("layout", ...))`) is copy-pasted **10 times** across every question directive.
- **Inline YAML `boxes:` parsing** is duplicated between `ParseRelabelDirective` (~185 lines) and `ParseInlineDiagramHotspotsYaml` (~105 lines).

### Hotspot 2: `src/Meloberry.Application/UseCases/Json/PatchEntityUseCase.cs` (~835 lines → ~420 lines, **-50%**)
- `ApplyPayloadQuestionPatchAsync` + `ApplyTableBlockPatch` + `ApplyDiagramExploreBlockPatchAsync` + `ApplyRevealBlockPatch` manually inspect `JsonObject` nodes using verbose repetitive guards (`if (node is not null && node.GetValueKind() != JsonValueKind.String) return new Error("Patch.InvalidValue", ...)` appears **50+ times**).
- Introducing ~6 tiny helpers (`TryReadNullableString`, `TryReadNonEmptyString`, `TryReadBool`, `TryReadStringArray`, `TryReadBoxCoords`, `TryApplyConfinedImageAsync`) cuts this file roughly in half with zero behavioral change.

### Hotspot 3: `src/Meloberry.Application/UseCases/Diagnostics/GetSchemaUseCase.cs` (~640 lines → ~380 lines, **-40%**)
- 27 separate `Build*Schema()` factory methods repeat verbose `SchemaTopicDefinition` + `SchemaFieldSpec` + anonymous `Example` object scaffolding.
- Storing static schema metadata declaratively (or using concise helper builders for shared fields like `ref`, `explanation`, `layoutOverrides`) trims ~250 lines and makes adding/updating specs much less error-prone.

### Hotspot 4: `src/Meloberry.Infrastructure/Services/DeclarativeSpecLoader.cs` (~615 lines → ~380 lines, **-38%**)
- `ApplyQuestionDto` is a flat 50-property mega-DTO paired with **18 boolean presence flags** (`hasMcqOnly`, `hasImagePath`, `hasFlashcardOnly`, `hasRecallFlags`, `hasPlaceholder`, `hasArOnly`, `hasAnswer`, `hasSpotOnly`, `hasTableShape`, ...) and a giant 12-branch combinatorial `if (a || b || c || ...)` validation matrix.
- Replacing this with `JsonObject` key-set validation (mirroring `PatchEntityUseCase.AllowedKeysByKind`) removes ~180 lines of fragile flag soup and stops new fields from requiring edits to 11 other `if` statements.

### Hotspot 5: `src/Meloberry.Infrastructure/Web/SlidingWebAppGenerator.cs` & `WebAppSharedStyles.cs` (~1,760 lines → ~1,300 lines, **-26%**)
- **Embedded JS (`~950 lines`):** Every graded widget (`buildMcqCard`, `buildFillBlankCard`, `buildAssertionReasonCard`, `buildSpotErrorCard`, `buildDiffCard`, `buildMechanismCard`, `buildRelabelCard`, `buildClassifyCard`, `buildMatchCard`, `buildNumericCard`) duplicates:
  - telemetry mutation (`checkAttempts`, `firstTryCorrect`, `isCorrect`),
  - feedback text coloring (`feedback.style.color = ...; feedback.style.fontWeight = '600'`),
  - explanation box creation/toggling,
  - retry reset wiring, and
  - HTML5 drag-and-drop boilerplate.
  Extracting 3–4 shared JS helpers (`recordGrade(id, ok)`, `setFeedback(el, text, ok)`, `createExplanationBox(html)`, `bindDropZone(...)`) removes ~250–300 lines of repeated JS.
- **Shared CSS:** `.selected`, `.correct`, `.wrong`, and `.used` styles are duplicated across 8+ widget classes (`.mby-option-btn`, `.mby-error-chip`, `.mby-diff-cell-btn`, `.mby-classify-chip`, `.mby-match-slot`, `.mby-case-tab`, `.mby-relabel-assign-row`, `.mby-mech-slot`).
- **Layout Editor vs WebApp:** `LayoutEditorPageBuilder.cs` duplicates the zoom lightbox HTML/CSS/JS that already exists in `SlidingWebAppGenerator.cs`.

### Hotspot 6: Cross-Cutting Micro-Duplications Across Layers
Several small utilities were copy-pasted across files instead of placed in a single shared helper:

| Duplicated Logic | Copies | Locations |
| :--- | :---: | :--- |
| Normalized `[0..1]` box coord parsing (`rect` vs `x/y/w/h`) | **7** | `PatchEntityUseCase` (×2), `DeclarativeSpecLoader` (×2), `MarkdownSpecParser` (×2), `Phase2Payloads` |
| Normalized box bounds check (`0..1`, `x+w<=1`) | **2** | `Phase2PayloadRules.HasValidRelabelBoxBounds` (`1.001`) vs `Phase3PayloadRules.HasValidHotspotBounds` (`1.0001`) |
| Markdown code-fence open/close tracking | **3** | `ArticleLinkRules.TryGetFenceToken`, `Phase3PayloadRules.TryUpdateFenceState`, `MarkdownSpecParser.TryOpenCodeFence` |
| `Levenshtein` + `FindClosest` typo suggestion | **2** | `PatchEntityUseCase.cs` AND `DeclarativeSpecLoader.cs` (verbatim duplicate) |
| `CaseSubQuestion` → domain `Question` rehydration | **2** | `DiagnosticsUseCases.cs` AND `SlidingWebAppGenerator.cs` (verbatim 30-line switch) |
| Multi-root path confinement resolution | **2** | `JsonQuestionMaterializer.CopyConfinedImageAsync` AND `ResolveConfinedSidecarFile` |
| Dry-run staging + asset snapshot/cleanup | **4** | `ApplyDeclarativeSpecUseCase.cs` (`CreateNewWorkspace`, `AppendToSession`, `AppendToComponent`, `AppendToQuiz`) |
| MIME type dictionary & free TCP port finder | **2** | `LocalPreviewHost.cs` AND `CdpHeadlessWebAppVerifier.cs` |
| `--timeout` 5..180 parsing | **3** | `ApplyCliCommand`, `BuildCliCommand`, `AnalyzeCliCommand` |
| Polymorphic payload validation | **4–5** | `MarkdownSpecParser`, `DeclarativeSpecLoader`, `PatchEntityUseCase`, `DiagnosticsUseCases`, `MbypkgExporter` |

---

## 3. Stale Documentation / Repo Noise

- **`PROPOSED_COMPONENT_SPECS.md` (~300 lines):** Describes v1.3.0 and proposes Phases 0–3 as future work—all 13 components are already implemented and documented in `docs/`. Removing or archiving it out of the repo immediately trims obsolete context (especially helpful for AI agents scanning the repo).
- **`Phase1Payloads.cs` / `Phase2Payloads.cs` / `Phase3Payloads.cs`** and **`Phase1PayloadCodec`:** Chronological naming leaks build history into the Domain layer. Renaming/grouping by concept (`ArticlePayloads.cs`, `QuizPayloads.cs`, `PayloadCodec.cs`) improves clarity even beyond LOC savings.

---

## 4. Bonus Findings: Latent Bugs Exposed by Bloat

While auditing the duplicated JS and test suites, three concrete defects surfaced that directly stem from copy-paste/bloat:

1. **Undefined JS function `createExplanationBox` in `SlidingWebAppGenerator.cs`:**
   - `buildClassifyCard`, `buildMatchCard`, `buildNumericCard`, and `buildCaseGroupCard` all call `createExplanationBox(...)`, whereas earlier Phase 1/2 widgets built `mby-explanation-box` inline. **`createExplanationBox` is never defined** in `SlidingWebAppGenerator.cs` or `WebAppSharedStyles.cs`, which will throw a `ReferenceError` at runtime in the browser whenever any Phase 3 question slide renders.
2. **Undefined JS function `openLightbox` in `SlidingWebAppGenerator.cs`:**
   - `buildDiagramExploreBlock` wires its Zoom button to `openLightbox(...)`, but the actual function is named `openZoomLightbox(...)`.
3. **Mismatched assertion target in `Phase3ComponentTests.cs`:**
   - `Phase3_BugRegressions_B1_B2_B4_B5_B6_AreResolved` checks `Assert.Contains("\"componentIndex\":2", indexHtml);` against `index.html`, whereas `DATA` (which contains `componentIndex`) is emitted into `app.js`.
4. **Slightly divergent bounds tolerance:**
   - `Phase2PayloadRules.HasValidRelabelBoxBounds` uses `<= 1.001` while `Phase3PayloadRules.HasValidHotspotBounds` uses `<= 1.0001`.

---

## 5. Prioritized Reduction Plan (Highest ROI First)

If you want to trim the codebase safely while keeping 100% of functionality:

1. **Step 1 — Fix the 2 undefined JS helpers in `SlidingWebAppGenerator.cs`** (`createExplanationBox` and `openZoomLightbox`) and reuse `createExplanationBox` + `recordGrade` across all 10 graded JS widgets (**-300 LOC**, fixes live Phase 3 bug).
2. **Step 2 — Consolidate `tests/` (24 → ~10 files) + add `WorkspaceTestFixture`** (**-3,200 to -3,400 LOC**, cuts test suite in half, removes `Phase2BDiffAndMechanismCliTests.cs` duplication, speeds up `dotnet test` significantly by merging 6 redundant Chromium launches into 1–2).
3. **Step 3 — Extract shared helpers in `PatchEntityUseCase.cs` & `MarkdownSpecParser.cs`** (**-950 LOC** across just 2 files, eliminates the worst C# copy-paste).
4. **Step 4 — Replace `DeclarativeSpecLoader.cs` boolean flag matrix + deduplicate cross-cutting helpers** (box parser, code-fence helper, Levenshtein, `CaseSubQuestion` rehydration, MIME/timeout helpers) and remove stale `PROPOSED_COMPONENT_SPECS.md` (**-900 LOC**).

**Net result:** ~35% smaller codebase (**~6,400 fewer lines**), faster test runs, single source of truth for validation/parsing, and zero removed features.