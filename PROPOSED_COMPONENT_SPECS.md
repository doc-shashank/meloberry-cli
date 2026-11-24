# Meloberry CLI (`mby`) — Proposed Component Specs & Roadmap

> **Scope:** Planned expansion of `mby` (currently v1.3.0) beyond Articles (`text`, `image`, `callout`), MCQs (single/multi), Flashcards, and Native Images.  
> **Architecture Principle:** **Markdown (`:::` directives) as the primary authoring format**, compiled strictly into **canonical JSON** for validation, patching, SQLite persistence (`PayloadJson` column), `.mbypkg` v2.0 packaging, WebApp rendering, and Android sync.

---

## 1. Architectural Strategy: Markdown Authoring + Strict JSON Core

Rather than replacing strict JSON with loose Markdown, `mby` uses a two-layer contract:

1. **Authoring Layer (Markdown + `:::` Directives):** Human- and AI-agent-friendly syntax for `.md` files (`kind: article`, `kind: quiz`, or modular session directories). Eliminates JSON string escaping for prose, KaTeX, and tables while retaining strict fail-fast directive validation (`Markdown.UnknownFence`, `Markdown.InvalidFrontmatter`).
2. **Canonical Interchange & Storage Layer (Strict JSON):** `MarkdownSpecParser` compiles every `:::` directive into the exact same strongly-typed `DeclarativeApplySpec` as JSON input.
   - **Persistence:** Keep shared indexed columns (`Id`, `ContainerId`, `SortIndex`, `Kind`/`QuestionType`, `RefKey`, `LayoutOverridesJson`) in SQLite and store polymorphic type-specific fields in a `PayloadJson TEXT` column.
   - **Packaging:** Bump `.mbypkg` to `packageVersion: "2.0"` while maintaining backward-compatible import for `v1.0` archives.
   - **Coordinate-heavy specs (e.g., Relabel the Image):** Reference sidecar JSON emitted by `melomg` (e.g., `masks="assets/fig_2.1.masks.json"`) with an optional inline YAML fallback.

---

## 2. Master Overview & Ratings

### A. User-Proposed Features (7)

| # | Component / Spec | Placement | Rating | Complexity | Key Design Unlock |
| :---: | :--- | :--- | :---: | :---: | :--- |
| 1 | **Native Relabel the Image** | `Question` | **10 / 10** | High | Live SVG/DOM overlays from `melomg` boxes; eliminates duplicate masked PNGs |
| 2 | **Native Differentiation Questions** | `Question` (3 modes) | **9.5 / 10** | Medium–High | Shares the exact tabular schema with Native Tables |
| 3 | **Native Mechanism Arrangement** | `Question` | **9 / 10** | Medium | Distractor blocks + parallel/commutative step groups |
| 4 | **Fill in the Blanks (Free Recall)** | `Question` | **9 / 10** | Low–Medium | Synonym pipes, fuzzy typo tolerance, Greek normalization & self-grade override |
| 5 | **Spot the Error in Statement** | `Question` | **9 / 10** | Medium | Explicit tappable spans + optional correction stage + "No error" trap |
| 6 | **Native Tables** | `ArticleBlock` | **8.5 / 10** | Medium | Study-mode column masking, sticky first column, captions & footnotes |
| 7 | **Assertion–Reason Questions** | `Question` | **7.5 / 10** | Low | Zero boilerplate; auto-generates standard 4/5-option choices from enum |

### B. Additional Suggested Components (6)

| # | Component / Spec | Placement | Rating | Complexity | Priority |
| :---: | :--- | :--- | :---: | :---: | :--- |
| 8 | **Multi-Question Vignette / Case Stem** | `Quiz` / `Component` | **9.5 / 10** | Medium | High |
| 9 | **Interactive Diagram Explorer** | `ArticleBlock` | **9 / 10** | Medium | High |
| 10 | **Classification / Bucket Sort** | `Question` | **8.5 / 10** | Medium | Medium–High |
| 11 | **Match the Following** | `Question` | **8 / 10** | Low–Medium | Medium |
| 12 | **Progressive Step-Through / Socratic Reveal** | `ArticleBlock` | **8 / 10** | Low | Medium |
| 13 | **Numeric Tolerance Question** | `Question` | **7.5 / 10** | Low | Medium |

---

## 3. Detailed Specifications — User-Proposed Features

### 3.1 Native Tables
- **Rating:** `8.5 / 10`
- **Placement:** `ArticleBlockKind.Table`
- **Description:** Promotes GFM tables from passive text inside `ArticleBlockKind.Text` into a first-class interactive study block. Adds features raw Markdown text tables cannot offer: study-mode column hiding/blurring for inline self-testing, sticky first column on narrow screens/Android, structured table title/caption, and footnotes. Uses the exact same table syntax as Differentiation Questions so tables can be reused directly.
- **Suggested Lint Rules:** Must have header row + ≥1 data row; consistent column count per row; `hideableCols` indices within `1..columnCount`.

**Markdown Spec:**
```markdown
:::table{title="Loop vs Thiazide Diuretics" stickyFirstCol=true hideableCols="2,3" caption="Comparative renal pharmacology"}
| Feature | Loop Diuretics | Thiazide Diuretics |
| :--- | :--- | :--- |
| Primary Site | Thick ascending limb | Early distal tubule |
| Mechanism | Inhibits $\text{Na}^+/\text{K}^+/2\text{Cl}^-$ | Inhibits $\text{Na}^+/\text{Cl}^-$ |
| Serum $\text{Ca}^{2+}$ | Decreased | Increased |
:::footnote
Both classes can cause hypokalemia and metabolic alkalosis.
:::
```

---

### 3.2 Native Differentiation Questions
- **Rating:** `9.5 / 10`
- **Placement:** `QuestionType.Differentiation`
- **Description:** Unified comparative-table question supporting three interaction modes via a single `mode` attribute:
  - **`fill`:** Cells containing `{{answer | alias}}` become blanks (supports pure free recall or optional shuffled word bank via `bank=true`).
  - **`spot-wrong`:** Erroneous cells are marked with `~~Wrong~~ -> Correct`; the learner taps the incorrect cell(s) to identify errors.
  - **`rearrange`:** Values are scrambled **within each row** (across columns for the same attribute) so the learner reassigns features to the correct entities without creating a chaotic full-grid soup.
- **Suggested Lint Rules:** Valid `mode` (`fill` | `spot-wrong` | `rearrange`); ≥2 columns and ≥2 rows; `fill` must contain ≥1 `{{...}}` blank; `spot-wrong` must contain ≥1 `~~...~~ -> ...` cell (unless `allowNoError=true`); `rearrange` must have ≥2 non-header columns to swap.

**Markdown Spec (`spot-wrong` example):**
```markdown
:::diff{mode="spot-wrong" title="Identify the incorrect cell(s)"}
| Property | Gram-Positive | Gram-Negative |
| :--- | :--- | :--- |
| Peptidoglycan | Thick | Thin |
| Outer membrane | ~~Present~~ -> Absent | Present |
| Endotoxin (LPS) | Absent | Present |
:::explanation
Gram-positive bacteria lack an outer lipid membrane and do not contain LPS.
:::
```

**Markdown Spec (`fill` example):**
```markdown
:::diff{mode="fill" bank=false fuzzy=true title="Complete the comparison"}
| Parameter | Respiratory Acidosis | Metabolic Acidosis |
| :--- | :--- | :--- |
| Primary change | {{Increased PaCO2 | High PaCO2}} | {{Decreased HCO3- | Low HCO3-}} |
| Compensation | Renal $\text{HCO}_3^-$ retention | {{Hyperventilation | Respiratory}} |
:::
```

**Markdown Spec (`rearrange` example):**
```markdown
:::diff{mode="rearrange" title="Match each feature to the correct column"}
| Feature | Type 1 DM | Type 2 DM |
| :--- | :--- | :--- |
| Pathogenesis | Autoimmune $\beta$-cell destruction | Insulin resistance |
| Typical onset | Childhood / adolescence | Adulthood |
| Ketoacidosis risk | High (DKA) | Low (HHS more common) |
:::
```

---

### 3.3 Native Mechanism Arrangement Questions
- **Rating:** `9 / 10`
- **Placement:** `QuestionType.Mechanism`
- **Description:** Ordering/sequencing widget for physiological cascades, biochemical pathways, pharmacological mechanisms, or algorithms. Shuffles blocks at runtime and supports drag-and-drop plus accessible keyboard/button up-down reordering.
- **Key Enhancements:**
  - **Distractor steps (`! ...`):** Optional decoy steps that must be excluded from the final sequence to prevent pure process-of-elimination guessing.
  - **Parallel / commutative steps (`3a.` / `3b.`):** Allows steps that happen concurrently to be accepted in any mutual order.
  - **Partial-credit visual feedback:** Highlights correctly placed slots or valid transitions on check.
- **Suggested Lint Rules:** ≥2 ordered steps; non-empty step text; unique step texts; distractor steps cannot exceed ordered step count (warning if excessive).

**Markdown Spec:**
```markdown
:::mechanism{title="Renin-Angiotensin-Aldosterone Activation"}
1. Decreased renal perfusion triggers juxtaglomerular renin release
2. Renin cleaves angiotensinogen into Angiotensin I
3. ACE converts Angiotensin I to Angiotensin II in pulmonary endothelium
4a. Angiotensin II causes efferent arteriolar vasoconstriction
4b. Angiotensin II stimulates adrenal zona glomerulosa aldosterone secretion
! ANP directly converts Angiotensin I into Angiotensin II
:::explanation
ANP inhibits renin and aldosterone release; it does not catalyze Angiotensin I conversion.
:::
```

---

### 3.4 Native Relabel the Image Questions
- **Rating:** `10 / 10` *(Flagship Feature)*
- **Placement:** `QuestionType.RelabelImage`
- **Description:** Interactive image occlusion and diagram relabeling using **one unmasked source image** plus normalized bounding boxes (`0.0..1.0` relative coordinates for responsive scaling across Desktop WebApp and Android). Replaces the static dual-image (`q/` + `ans/`) workaround.
- **Three Interaction Modes from One Spec:**
  - **`reveal`:** Tap-to-reveal native occlusion masks (interactive image-occlusion flashcards).
  - **`bank`:** Numbered/lettered obfuscation boxes paired with a shuffled label bank.
  - **`recall`:** Click a masked box and type the hidden label (with fuzzy matching + self-grade override).
- **`melomg` Integration:** Extend `melomg` (`extract --manifest` or `mask export`) to emit a `.masks.json` sidecar containing normalized `[x, y, w, h]` bounding boxes and OCR/PDF text labels.
- **Suggested Lint Rules:** `image` must exist inside workspace and pass magic-byte verification; ≥1 valid box with `0 <= x, y, w, h <= 1` and `x+w <= 1`, `y+h <= 1`; non-empty label per box; valid `mode` (`reveal` | `bank` | `recall`) and `style` (`solid` | `blur` | `pixelate` | `whiteout`).

**Markdown Spec (using `melomg` sidecar):**
```markdown
:::relabel{image="assets/fig_2.1.png" masks="assets/fig_2.1.masks.json" mode="bank" style="blur"}
prompt: Identify the labeled structures of the nephron.
include: ["Proximal tubule", "Loop of Henle", "Collecting duct"]
:::explanation
The loop of Henle establishes the medullary osmotic gradient via countercurrent multiplication.
:::
```

**Markdown Spec (inline YAML fallback without sidecar):**
```markdown
:::relabel{image="assets/fig_2.1.png" mode="recall" style="solid"}
prompt: Type the hidden anatomical labels.
boxes:
  - label: "Proximal convoluted tubule"
    aliases: ["PCT"]
    rect: [0.12, 0.24, 0.18, 0.06]
  - label: "Glomerulus"
    rect: [0.45, 0.15, 0.14, 0.05]
:::
```

---

### 3.5 Assertion–Reason Questions
- **Rating:** `7.5 / 10`
- **Placement:** `QuestionType.AssertionReason`
- **Description:** Eliminates repetitive 4- or 5-option MCQ boilerplate for standard Assertion–Reason items. The author supplies only `A:`, `R:`, the canonical `answer` enum, and an optional `:::explanation`. `mby` deterministically generates the standardized options at render time (supporting both 4-option and 5-option schemes via `scheme="4opt" | "5opt"`).
- **Answer Enum:**
  - `both-explain` — Both A and R are true, and R is the correct explanation of A.
  - `both-no-explain` — Both A and R are true, but R is NOT the correct explanation of A.
  - `a-true-r-false` — A is true, but R is false.
  - `a-false-r-true` — A is false, but R is true.
  - `both-false` — Both A and R are false *(5-option scheme)*.
- **Suggested Lint Rules:** Non-empty `A:` and `R:`; valid `answer` enum; `both-false` requires `scheme="5opt"` (or auto-selects 5-option scheme).

**Markdown Spec:**
```markdown
:::assertion-reason{answer="both-no-explain" scheme="4opt"}
A: Aspirin is avoided in children with viral infections.
R: Aspirin irreversibly inhibits COX-1 and COX-2 via acetylation.
:::explanation
Both statements are true, but pediatric avoidance is due to Reye syndrome risk, not merely irreversible COX acetylation.
:::
```

---

### 3.6 Fill in the Blanks (Free Recall)
- **Rating:** `9 / 10`
- **Placement:** `QuestionType.FillBlank`
- **Description:** Pure free-recall text completion complementing single-correct MCQs (cued recall). Supports multiple blanks in one prompt, pipe-delimited accepted aliases (`{{primary | alias1 | alias2}}`), case/whitespace/punctuation normalization, Greek letter equivalence (`alpha` ↔ `α`, `beta` ↔ `β`), configurable typo tolerance (`fuzzy=true`, e.g., Levenshtein distance ≤ 1 for answers ≥ 6 chars), and an **"Override: Mark Correct"** button on reveal so legitimate phrasings never feel punitive.
- **Suggested Lint Rules:** ≥1 `{{...}}` blank token; no empty primary answer inside `{{...}}`; balanced `{{` and `}}` delimiters.

**Markdown Spec:**
```markdown
:::fill-blank{fuzzy=true caseSensitive=false}
The rate-limiting enzyme of glycolysis is {{phosphofructokinase-1 | PFK-1 | PFK1}}, which is potently allosterically activated by {{fructose-2,6-bisphosphate | F-2,6-BP | F2,6BP}}.
:::explanation
PFK-1 catalyzes the irreversible committed step converting fructose-6-phosphate to fructose-1,6-bisphosphate.
:::
```

---

### 3.7 Identifying What Is Incorrect in This Statement (Spot the Error)
- **Rating:** `9 / 10`
- **Placement:** `QuestionType.SpotError`
- **Description:** High-yield misconception-buster targeting reversed arrows, swapped exceptions, wrong receptor subtypes, or subtle factual errors. Candidate segments are explicitly delimited in `[brackets]` so Desktop and Android render crisp tappable chips rather than brittle cursor text selection.
- **Key Enhancements:**
  - **Two-stage mode (`requireFix=true`):** First tap the faulty segment, then type (or pick) the corrected value.
  - **True-statement trap (`allowNoError=true`):** Displays a "No error in statement" button so learners must evaluate truth rather than blindly hunting for a trick word.
- **Suggested Lint Rules:** ≥2 delimited `[segment]` spans; ≥1 error span `~~wrong~~ -> right` unless `noError=true`; cannot have both `noError=true` and error spans.

**Markdown Spec:**
```markdown
:::spot-error{requireFix=false allowNoError=true}
In [primary hyperparathyroidism], serum calcium is [elevated], serum phosphate is [~~elevated~~ -> decreased], and PTH is [high].
:::explanation
PTH inhibits proximal tubular phosphate reabsorption via downregulation of NaPi-IIa/IIc cotransporters, causing hypophosphatemia.
:::
```

---

## 4. Detailed Specifications — Additional Suggested Components

### 4.1 Multi-Question Vignette / Case Stem
- **Rating:** `9.5 / 10` *(High Priority)*
- **Placement:** `QuestionType.CaseGroup` (or `ComponentKind` container inside a Quiz)
- **Description:** Pins a shared context block (clinical vignette, lab value table, or diagnostic image) above or beside 2–5 linked sub-questions of any supported type (MCQ, Fill-in-the-Blank, Spot-the-Error, etc.). Eliminates repeating the same stem across multiple slides and preserves clean slide navigation and search indexing.

**Markdown Spec:**
```markdown
:::case{title="Acute Chest Pain Workup" image="assets/ecg_01.png"}
A 58-year-old male presents with 2 hours of substernal chest pain radiating to the left jaw, diaphoresis, and ST elevations in leads II, III, and aVF.

::::mcq{explanation="Leads II, III, and aVF reflect the inferior wall, most commonly supplied by the RCA."}
Which coronary artery is most likely occluded?
- [x] Right coronary artery
- [ ] Left anterior descending artery
- [ ] Left circumflex artery
::::

::::fill-blank{fuzzy=true}
Before administering nitrates, obtain a right-sided ECG lead {{V4R}} to rule out right ventricular infarction.
::::
:::
```

---

### 4.2 Interactive Diagram Explorer
- **Rating:** `9 / 10` *(High Priority)*
- **Placement:** `ArticleBlockKind.DiagramExplore`
- **Description:** The *learning-mode twin* of **Relabel the Image**. Consumes the exact same image and `melomg` normalized bounding boxes/pins, but inside an Article: clicking a hotspot highlights the region and opens a concise annotation card instead of hiding the label for testing. One coordinate asset powers both active reading and active recall.

**Markdown Spec:**
```markdown
:::diagram-explore{image="assets/nephron.png" masks="assets/nephron.masks.json" title="Explore Nephron Transport"}
- Proximal tubule: Reabsorbs ~65% of filtered $\text{Na}^+$ and water; site of carbonic anhydrase inhibitors.
- Thick ascending limb: Impermeable to water; $\text{Na}^+/\text{K}^+/2\text{Cl}^-$ symporter targeted by loop diuretics.
- Early distal tubule: $\text{Na}^+/\text{Cl}^-$ symporter targeted by thiazides; PTH-regulated $\text{Ca}^{2+}$ reabsorption.
:::
```

---

### 4.3 Classification / Bucket Sort
- **Rating:** `8.5 / 10` *(Medium–High Priority)*
- **Placement:** `QuestionType.Classify`
- **Description:** Tactile categorization drill where 6–12 shuffled chips are sorted into 2–4 named buckets (e.g., *Gs vs Gi vs Gq*, *DNA vs RNA viruses*, *Bactericidal vs Bacteriostatic*). Faster to author and answer than a full Differentiation Table when testing group membership rather than multi-attribute comparison.

**Markdown Spec:**
```markdown
:::classify{title="Sort G-Protein Coupled Receptors"}
[Gs]
- Beta-1 adrenergic
- Beta-2 adrenergic
- D1 dopaminergic

[Gi]
- Alpha-2 adrenergic
- M2 muscarinic
- D2 dopaminergic

[Gq]
- Alpha-1 adrenergic
- M1 muscarinic
- H1 histaminergic
:::explanation
Mnemonic: QISS QIQ (Alpha1=Gq, Alpha2=Gi, Beta1/2=Gs; M1/3=Gq, M2=Gi).
:::
```

---

### 4.4 Match the Following
- **Rating:** `8 / 10` *(Medium Priority)*
- **Placement:** `QuestionType.Match`
- **Description:** Classic two-column pairing exercise with optional right-column distractors (`! Decoy item`). Clean, compact on mobile viewports, and trivial to author in Markdown using `Left :: Right` syntax.

**Markdown Spec:**
```markdown
:::match{title="Match Antibiotic to Primary Adverse Effect"}
Aminoglycosides :: Ototoxicity and nephrotoxicity
Vancomycin :: Red man syndrome (histamine release)
Linezolid :: Serotonin syndrome and thrombocytopenia
Chloramphenicol :: Gray baby syndrome
! Tendon rupture and QT prolongation
:::explanation
Tendon rupture and QT prolongation are classic adverse effects of fluoroquinolones.
:::
```

---

### 4.5 Progressive Step-Through / Socratic Reveal
- **Rating:** `8 / 10` *(Medium Priority)*
- **Placement:** `ArticleBlockKind.Reveal`
- **Description:** Sequential locked cards embedded inside an Article (e.g., *Clinical Clue → Differential → Diagnostic Test → First-Line Management*, or step-by-step math derivations). Unlocks one card at a time on click/spacebar to prevent passive skimming during initial reading.

**Markdown Spec:**
```markdown
:::reveal{title="Work Through the Acid-Base Case"}
> Step 1: Primary Disorder
pH is 7.28 and $\text{HCO}_3^-$ is 14 mEq/L → Primary metabolic acidosis.

> Step 2: Anion Gap
$\text{Na}^+ 140 - (\text{Cl}^- 102 + \text{HCO}_3^- 14) = 24$ → High anion gap metabolic acidosis (HAGMA).

> Step 3: Winter's Formula Check
Expected $\text{PaCO}_2 = 1.5(14) + 8 \pm 2 = 29 \pm 2\text{ mmHg}$. Measured $\text{PaCO}_2$ is 28 mmHg → Appropriate respiratory compensation.
:::
```

---

### 4.6 Numeric Tolerance Question
- **Rating:** `7.5 / 10` *(Medium Priority)*
- **Placement:** `QuestionType.Numeric`
- **Description:** Dedicated quantitative free-recall question with exact or range tolerance (`±` absolute or `%` relative), optional unit display, and KaTeX worked solution. Avoids awkward string-alias hacks when testing calculations (pharmacokinetics, clearance, Henderson–Hasselbalch, physics).

**Markdown Spec:**
```markdown
:::numeric{answer="120" tolerance="5" unit="mL/min"}
A patient has urine creatinine $U = 120\text{ mg/dL}$, plasma creatinine $P = 1.0\text{ mg/dL}$, and urine flow rate $V = 1.0\text{ mL/min}$. Calculate the creatinine clearance.
:::explanation
$$C_{Cr} = \frac{U \times V}{P} = \frac{120 \times 1.0}{1.0} = 120\text{ mL/min}$$
:::
```

---

## 5. Phased Implementation Rollout

1. **Phase 0 — Architecture Foundation (`mby` v2.0 prep)**
   - Add `PayloadJson TEXT` to `ArticleBlocks` and `Questions` in `SqliteWorkspaceRepository.cs`.
   - Upgrade `MbypkgExporter.cs` to emit `packageVersion: "2.0"` while keeping `1.0` import compatibility.
   - Extend `MarkdownSpecParser.cs` to parse `kind: quiz` files (including `:::mcq` and `:::flashcard` first so existing quizzes also gain Markdown authoring).
2. **Phase 1 — High-ROI Core Specs**
   - **Native Tables**, **Fill in the Blanks**, **Assertion–Reason**, and **Spot the Error**.
   - Add corresponding lint rules in `mby check`, CSS/JS renderers in `SlidingWebAppGenerator.cs`, and DOM checks in `CdpHeadlessWebAppVerifier.cs`.
3. **Phase 2 — Signature Interactive Components**
   - **Native Differentiation Questions** (reusing Native Table parsing), **Native Mechanism Arrangement**, and **Native Relabel the Image** (paired with `melomg` normalized `.masks.json` sidecar export).
4. **Phase 3 — Ecosystem Multipliers**
   - **Interactive Diagram Explorer** (reusing Relabel coordinates), **Multi-Question Case Stems**, **Classification / Bucket Sort**, **Match the Following**, **Progressive Reveal**, and **Numeric Tolerance**.
