using System.Net;
using System.Text.Json;
using Meloberry.Application.Abstractions;

namespace Meloberry.Infrastructure.Web;

/// <summary>
/// Generates the component-based GUI Layout Editor served by <c>mby layout edit</c>.
/// Reuses <see cref="WebAppSharedStyles"/> so previewed cards match <c>mby build</c> 1:1.
/// </summary>
public sealed class LayoutEditorPageBuilder : ILayoutEditorPageBuilder
{
    public string BuildHtml(string initialComponent, CliToolMetadata toolMetadata)
    {
        var encodedInitial = JsonSerializer.Serialize(initialComponent);
        var encodedTitleVersion = WebUtility.HtmlEncode($"{toolMetadata.ExecutableName} v{toolMetadata.Version}");

        return $$"""
<!DOCTYPE html>
<html lang="en" data-theme="berry">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <meta name="generator" content="{{WebUtility.HtmlEncode($"{toolMetadata.ProductName} ({toolMetadata.ExecutableName}) {toolMetadata.Version}")}}" />
  <title>Meloberry Component Layout Editor — {{encodedTitleVersion}}</title>
  <style>
{{WebAppSharedStyles.BuildCoreCss()}}
html, body { height: 100%; overflow: hidden; }
.ed-shell { display: flex; flex-direction: column; height: 100vh; overflow: hidden; }
.ed-topbar { flex-shrink: 0; display: flex; align-items: center; justify-content: space-between; gap: 16px; flex-wrap: wrap; padding: 12px 20px; background: var(--surface); border-bottom: 1px solid var(--border); }
.ed-brand-group, .ed-top-controls, .ed-actions { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
.ed-title { font-size: 16px; font-weight: 800; color: var(--accent); letter-spacing: -0.02em; }
.ed-ver-pill { padding: 3px 9px; border-radius: 999px; background: var(--accent-soft); color: var(--accent); border: 1px solid var(--border); font-size: 11px; font-weight: 700; font-family: ui-monospace, Consolas, monospace; }
.ed-dirty-pill { padding: 3px 9px; border-radius: 999px; background: rgba(245, 158, 11, 0.14); color: #b45309; font-size: 11px; font-weight: 700; }
.ed-inline-ctrl { display: flex; align-items: center; gap: 8px; font-size: 12px; font-weight: 700; color: var(--muted); }
.ed-seg-group { display: inline-flex; border: 1px solid var(--border); border-radius: 9px; overflow: hidden; background: var(--bg); }
.ed-seg-btn { padding: 6px 11px; border: none; background: transparent; color: var(--muted); font-size: 12px; font-weight: 600; cursor: pointer; }
.ed-seg-btn.active { background: var(--accent); color: #ffffff; }
.ed-speed-range { width: 96px; accent-color: var(--accent); }
.ed-mini-btn { padding: 6px 11px; border-radius: 8px; border: 1px solid var(--border); background: var(--bg); color: var(--text); font-size: 12px; font-weight: 600; cursor: pointer; }
.ed-mini-btn:hover { border-color: var(--accent); color: var(--accent); }
.ed-workspace { flex: 1; min-height: 0; display: grid; grid-template-columns: 390px 1fr; }
@media (max-width: 960px) { .ed-workspace { grid-template-columns: 1fr; grid-template-rows: auto 1fr; } }
.ed-sidebar { background: var(--surface); border-right: 1px solid var(--border); display: flex; flex-direction: column; min-height: 0; }
.ed-comp-tabs { display: flex; flex-wrap: wrap; gap: 6px; padding: 14px 16px; border-bottom: 1px solid var(--border); background: var(--bg); }
.ed-comp-tab { padding: 7px 11px; border-radius: 10px; border: 1px solid var(--border); background: var(--surface); color: var(--text); font-size: 12px; font-weight: 700; cursor: pointer; transition: border-color 140ms ease, background 140ms ease, color 140ms ease; }
.ed-comp-tab.active { background: var(--accent); border-color: var(--accent); color: #ffffff; }
.ed-inspector-scroll { flex: 1; min-height: 0; overflow-y: auto; padding: 16px; display: flex; flex-direction: column; gap: 14px; }
.ed-comp-header { padding-bottom: 10px; border-bottom: 1px solid var(--border); }
.ed-comp-title { margin: 0 0 4px; font-size: 16px; font-weight: 800; }
.ed-comp-sub { margin: 0; font-size: 12px; color: var(--muted); line-height: 1.45; }
.ed-scope-bar { display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 10px 12px; border-radius: 10px; border: 1px solid var(--border); background: var(--bg); font-size: 12px; font-weight: 700; }
.ed-field { border: 1px solid var(--border); border-radius: 12px; padding: 11px 12px; background: var(--bg); display: flex; flex-direction: column; gap: 8px; }
.ed-field-top { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
.ed-field-label { font-size: 13px; font-weight: 700; color: var(--text); }
.ed-field-key { font-family: ui-monospace, Consolas, monospace; font-size: 11px; color: var(--muted); }
.ed-field-desc { font-size: 11px; color: var(--muted); line-height: 1.35; }
.ed-slider-row { display: flex; align-items: center; gap: 8px; }
.ed-slider { flex: 1; accent-color: var(--accent); }
.ed-num-input { width: 68px; padding: 5px 7px; border-radius: 8px; border: 1px solid var(--border); background: var(--surface); color: var(--text); font-size: 13px; font-weight: 700; text-align: right; }
.ed-unit { font-size: 11px; font-weight: 700; color: var(--muted); min-width: 24px; }
.ed-field-reset { padding: 3px 7px; border-radius: 6px; border: 1px solid var(--border); background: var(--surface); color: var(--muted); font-size: 11px; font-weight: 600; cursor: pointer; }
.ed-field-reset:hover { color: var(--accent); border-color: var(--accent); }
.ed-sidebar-footer { padding: 11px 16px; border-top: 1px solid var(--border); font-size: 12px; color: var(--muted); display: flex; justify-content: space-between; align-items: center; }
.ed-status.ok { color: var(--ok); font-weight: 700; }
.ed-status.err { color: var(--bad); font-weight: 700; }
.ed-stage { min-height: 0; display: flex; flex-direction: column; overflow: hidden; background: var(--bg); }
.ed-stage-bar { flex-shrink: 0; display: flex; align-items: center; justify-content: space-between; gap: 12px; flex-wrap: wrap; padding: 10px 20px; border-bottom: 1px solid var(--border); background: var(--surface); font-size: 12px; }
.ed-spec-chips { display: flex; flex-wrap: wrap; gap: 6px; }
.ed-spec-chip { padding: 3px 9px; border-radius: 999px; background: var(--accent-soft); color: var(--accent); font-family: ui-monospace, Consolas, monospace; font-size: 11px; font-weight: 700; }
.ed-preview-scroll { flex: 1; min-height: 0; overflow: auto; padding: 28px 24px 40px; display: flex; flex-direction: column; align-items: center; }
.ed-slide-demo-wrap { width: 100%; display: flex; justify-content: center; transition: transform var(--slide-ms) cubic-bezier(0.22, 1, 0.36, 1), opacity 160ms ease; }
.ed-slide-demo-wrap.is-sliding { transform: translateX(36px); opacity: 0.35; }
.ed-mcq-question-note { margin-top: 12px; padding-top: 12px; border-top: 1px dashed var(--border); font-size: 12px; color: var(--muted); }
  </style>
</head>
<body>
  <div class="ed-shell">
    <header class="ed-topbar">
      <div class="ed-brand-group">
        <span class="ed-title">Meloberry Layout Editor</span>
        <span class="ed-ver-pill">{{encodedTitleVersion}}</span>
        <span id="dirtyBadge" class="ed-dirty-pill hidden">Unsaved edits</span>
      </div>
      <div class="ed-top-controls">
        <div class="ed-inline-ctrl">
          <span>Theme:</span>
          <div id="themeSeg" class="ed-seg-group" role="group" aria-label="Theme mode">
            <button type="button" class="ed-seg-btn" data-theme-val="berry">Berry</button>
            <button type="button" class="ed-seg-btn" data-theme-val="light">Light</button>
            <button type="button" class="ed-seg-btn" data-theme-val="dark">Dark</button>
            <button type="button" class="ed-seg-btn" data-theme-val="paper">Paper</button>
          </div>
        </div>
        <div class="ed-inline-ctrl">
          <span>Slide:</span>
          <input id="globalSlideRange" class="ed-speed-range" type="range" min="100" max="1200" step="20" value="380" />
          <span id="globalSlideVal">380ms</span>
          <button id="testSlideBtn" type="button" class="ed-mini-btn">Test Slide ↔</button>
        </div>
        <div class="ed-actions">
          <button id="resetCompBtn" type="button" class="mby-nav-btn">Reset Component</button>
          <button id="saveAllBtn" type="button" class="mby-nav-btn primary">Save Layout</button>
        </div>
      </div>
    </header>
    <div class="ed-workspace">
      <aside class="ed-sidebar">
        <nav id="componentTabs" class="ed-comp-tabs" role="tablist" aria-label="Layout components"></nav>
        <div id="inspectorBody" class="ed-inspector-scroll"></div>
        <div class="ed-sidebar-footer">
          <span id="saveStatus" class="ed-status">Loaded persistent storage.</span>
          <span id="activeProfileHint" class="ed-field-key"></span>
        </div>
      </aside>
      <main class="ed-stage">
        <div class="ed-stage-bar"><div id="specChips" class="ed-spec-chips"></div><div id="stageQuickActions"></div></div>
        <div class="ed-preview-scroll"><div id="previewStage" class="ed-slide-demo-wrap"></div></div>
      </main>
    </div>
  </div>
  <script>
{{WebAppSharedStyles.BuildCoreJs()}}
    const INITIAL_COMPONENT = {{encodedInitial}};
    const COMPONENTS = [
      { id: 'article-text', label: 'Article Text Block', profile: 'article', description: 'Controls vertical Markdown headings, body copy, inline code pills, fenced code blocks, and inter-block spacing inside Article slides.',
        fields: [{ key: 'cardMaxWidthPx', label: 'Card Max Width', unit: 'px' }, { key: 'cardPaddingPx', label: 'Inner Card Padding', unit: 'px' }, { key: 'promptFontSizePx', label: 'Heading Font Size', unit: 'px' }, { key: 'bodyFontSizePx', label: 'Body Text Size', unit: 'px' }, { key: 'blockSpacingPx', label: 'Block Vertical Spacing', unit: 'px' }, { key: 'contentAlign', label: 'Content Alignment', unit: '' }] },
      { id: 'image', label: 'Image Block', profile: 'scoped', description: 'Controls image max height, card framing, and alignment for media blocks. Apply uniformly across all card types or tune one profile individually.',
        fields: [{ key: 'imageMaxHeightPx', label: 'Image Max Height', unit: 'px' }, { key: 'cardMaxWidthPx', label: 'Card Max Width', unit: 'px' }, { key: 'cardPaddingPx', label: 'Inner Card Padding', unit: 'px' }, { key: 'contentAlign', label: 'Media Alignment', unit: '' }] },
      { id: 'mcq-question', label: 'MCQ Question', profile: 'mcq', description: 'Controls the MCQ card frame, padding, alignment, and prompt heading size above the answer choices.',
        fields: [{ key: 'cardMaxWidthPx', label: 'MCQ Card Max Width', unit: 'px' }, { key: 'cardPaddingPx', label: 'Inner Card Padding', unit: 'px' }, { key: 'promptFontSizePx', label: 'Question Prompt Size', unit: 'px' }, { key: 'contentAlign', label: 'Prompt Alignment', unit: '' }] },
      { id: 'mcq-options', label: 'MCQ Options', profile: 'mcq', description: 'Controls answer option grid columns (1 vertical stack vs. 2 columns), option spacing, option text size, and embedded option image cap.',
        fields: [{ key: 'optionColumns', label: 'Option Grid Columns', unit: 'cols' }, { key: 'optionGapPx', label: 'Option Gap', unit: 'px' }, { key: 'bodyFontSizePx', label: 'Option Text Size', unit: 'px' }, { key: 'imageMaxHeightPx', label: 'Option Image Max Height', unit: 'px' }, { key: 'contentAlign', label: 'Option Text Alignment', unit: '' }] },
      { id: 'flashcard', label: 'Flashcard', profile: 'flashcard', description: 'Controls front/back panel dimensions, typography, media height, default text vs. image order, and 3D flip duration.',
        fields: [{ key: 'cardMaxWidthPx', label: 'Flashcard Max Width', unit: 'px' }, { key: 'cardPaddingPx', label: 'Inner Card Padding', unit: 'px' }, { key: 'promptFontSizePx', label: 'Heading Font Size', unit: 'px' }, { key: 'bodyFontSizePx', label: 'Face Text Size', unit: 'px' }, { key: 'imageMaxHeightPx', label: 'Media Max Height', unit: 'px' }, { key: 'contentAlign', label: 'Face Alignment', unit: '' }, { key: 'defaultTextPosition', label: 'Text vs Image Placement', unit: '' }, { key: 'flipDurationMs', label: 'Flip Animation Duration', unit: 'ms' }] }
    ];
    const SAMPLE_DIAGRAM_SVG = 'data:image/svg+xml;utf8,' + encodeURIComponent(
      '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 640 260"><rect width="640" height="260" rx="18" fill="#fce8ef"/>' +
      '<rect x="34" y="65" width="160" height="130" rx="14" fill="#ffffff" stroke="#b8295b" stroke-width="3"/>' +
      '<text x="114" y="122" font-family="Segoe UI,sans-serif" font-size="18" font-weight="700" fill="#1f1722" text-anchor="middle">Domain</text>' +
      '<text x="114" y="148" font-family="Segoe UI,sans-serif" font-size="13" fill="#6c5b70" text-anchor="middle">LayoutBounds</text>' +
      '<path d="M194 130 H238" stroke="#b8295b" stroke-width="4"/><polygon points="234,122 250,130 234,138" fill="#b8295b"/>' +
      '<rect x="250" y="65" width="150" height="130" rx="14" fill="#b8295b"/>' +
      '<text x="325" y="122" font-family="Segoe UI,sans-serif" font-size="18" font-weight="700" fill="#ffffff" text-anchor="middle">mby CLI</text>' +
      '<text x="325" y="148" font-family="Segoe UI,sans-serif" font-size="13" fill="#fce8ef" text-anchor="middle">Layout Editor</text>' +
      '<path d="M400 130 H444" stroke="#b8295b" stroke-width="4"/><polygon points="440,122 456,130 440,138" fill="#b8295b"/>' +
      '<rect x="456" y="65" width="150" height="130" rx="14" fill="#ffffff" stroke="#b8295b" stroke-width="3"/>' +
      '<text x="531" y="122" font-family="Segoe UI,sans-serif" font-size="18" font-weight="700" fill="#1f1722" text-anchor="middle">WebApp</text>' +
      '<text x="531" y="148" font-family="Segoe UI,sans-serif" font-size="13" fill="#6c5b70" text-anchor="middle">Sliding HTML</text></svg>');

    let activeComponentId = COMPONENTS.some(c => c.id === INITIAL_COMPONENT) ? INITIAL_COMPONENT : 'article-text';
    let imageScope = 'all', previewMcqSelected = new Set([1]), previewMcqChecked = false, previewFcFlipped = false;
    let triggerPreviewFlashcardFlip = () => {};
    const specsByProfile = { article: {}, mcq: {}, flashcard: {} }, valuesByProfile = { article: {}, mcq: {}, flashcard: {} };
    const $ = id => document.getElementById(id);
    const componentTabsEl = $('componentTabs'), inspectorBodyEl = $('inspectorBody'), previewStageEl = $('previewStage');
    const specChipsEl = $('specChips'), stageQuickActionsEl = $('stageQuickActions'), activeProfileHintEl = $('activeProfileHint');
    const saveStatusEl = $('saveStatus'), dirtyBadgeEl = $('dirtyBadge'), globalSlideRange = $('globalSlideRange'), globalSlideVal = $('globalSlideVal');

    const setDirty = flag => dirtyBadgeEl.classList.toggle('hidden', !flag);
    const setStatus = (text, cls) => { saveStatusEl.textContent = text; saveStatusEl.className = 'ed-status' + (cls ? ' ' + cls : ''); };
    const getActiveComponent = () => COMPONENTS.find(c => c.id === activeComponentId) || COMPONENTS[0];
    const resolveTargetProfiles = comp => comp.id === 'image' ? (imageScope === 'all' ? ['article', 'mcq', 'flashcard'] : [imageScope]) : [comp.profile];
    const getFieldValue = (comp, key) => valuesByProfile[resolveTargetProfiles(comp)[0]][key];
    function setFieldValue(comp, key, val) {
      resolveTargetProfiles(comp).forEach(p => { if (specsByProfile[p][key]) valuesByProfile[p][key] = val; });
      setDirty(true);
    }
    function syncGlobalControlsUi() {
      const theme = valuesByProfile.mcq.themeMode || 'berry', slideMs = valuesByProfile.mcq.slideTransitionMs || '380';
      document.documentElement.setAttribute('data-theme', theme);
      document.querySelectorAll('#themeSeg button').forEach(b => b.classList.toggle('active', b.dataset.themeVal === theme));
      globalSlideRange.value = slideMs; globalSlideVal.textContent = slideMs + 'ms';
      document.documentElement.style.setProperty('--slide-ms', slideMs + 'ms');
    }
    function makeSegButtons(items, isActive, onClick) {
      const seg = mk('div', 'ed-seg-group');
      items.forEach(([val, label]) => {
        const b = mk('button', 'ed-seg-btn' + (isActive(val) ? ' active' : ''), label);
        b.type = 'button';
        b.addEventListener('click', () => onClick(val, b, seg));
        seg.appendChild(b);
      });
      return seg;
    }
    function renderTabs() {
      componentTabsEl.innerHTML = '';
      COMPONENTS.forEach(comp => {
        const btn = mk('button', 'ed-comp-tab' + (comp.id === activeComponentId ? ' active' : ''), comp.label);
        btn.type = 'button'; btn.role = 'tab'; btn.dataset.component = comp.id;
        btn.addEventListener('click', () => { activeComponentId = comp.id; renderTabs(); renderInspector(); renderPreview(); });
        componentTabsEl.appendChild(btn);
      });
    }
    function renderInspector() {
      const comp = getActiveComponent(), primaryProfile = resolveTargetProfiles(comp)[0];
      activeProfileHintEl.textContent = comp.id === 'image' ? 'scope: ' + imageScope : 'profile: ' + comp.profile;
      inspectorBodyEl.innerHTML = '';
      inspectorBodyEl.appendChild(mk('div', 'ed-comp-header', null, [mk('h2', 'ed-comp-title', comp.label), mk('p', 'ed-comp-sub', comp.description)]));

      if (comp.id === 'image') {
        inspectorBodyEl.appendChild(mk('div', 'ed-scope-bar', null, [
          mk('span', null, 'Target Profile'),
          makeSegButtons([['all', 'All'], ['article', 'Article'], ['mcq', 'MCQ'], ['flashcard', 'Flashcard']], v => imageScope === v, v => { imageScope = v; renderInspector(); renderPreview(); })
        ]));
      }

      comp.fields.forEach(field => {
        const spec = specsByProfile[primaryProfile][field.key];
        if (!spec) return;
        const currentVal = String(getFieldValue(comp, field.key) ?? spec.defaultValue);
        const resetBtn = mk('button', 'ed-field-reset', 'Default (' + spec.defaultValue + ')');
        resetBtn.type = 'button';
        resetBtn.addEventListener('click', () => { setFieldValue(comp, field.key, spec.defaultValue); renderInspector(); renderPreview(); });

        const box = mk('div', 'ed-field', null, [
          mk('div', 'ed-field-top', null, [mk('div', null, null, [mk('div', 'ed-field-label', field.label), mk('div', 'ed-field-key', spec.property + ' (' + spec.allowedBounds + ')')]), resetBtn]),
          mk('div', 'ed-field-desc', spec.description)
        ]);

        if (spec.valueType === 'enum') {
          const opts = (spec.allowedBounds || '').split('|').map(s => s.trim()).filter(Boolean).map(v => [v, v]);
          box.appendChild(makeSegButtons(opts, v => currentVal === v, (v, b, seg) => {
            setFieldValue(comp, field.key, v);
            seg.querySelectorAll('button').forEach(x => x.classList.remove('active')); b.classList.add('active'); renderPreview();
          }));
        } else {
          const [min, max] = spec.allowedBounds.split('..').map(Number);
          if (max - min <= 4) {
            const opts = Array.from({ length: max - min + 1 }, (_, i) => [String(min + i), String(min + i) + (field.unit ? ' ' + field.unit : '')]);
            box.appendChild(makeSegButtons(opts, v => currentVal === v, (v, b, seg) => {
              setFieldValue(comp, field.key, v);
              seg.querySelectorAll('button').forEach(x => x.classList.remove('active')); b.classList.add('active'); renderPreview();
            }));
          } else {
            const range = mk('input', 'ed-slider'), num = mk('input', 'ed-num-input');
            Object.assign(range, { type: 'range', min: String(min), max: String(max), value: currentVal });
            Object.assign(num, { type: 'number', min: String(min), max: String(max), value: currentVal });
            const onValChange = raw => {
              let p = parseInt(raw, 10); if (Number.isNaN(p)) return;
              const norm = String(Math.max(min, Math.min(max, p)));
              range.value = norm; num.value = norm; setFieldValue(comp, field.key, norm); renderPreview();
            };
            range.addEventListener('input', () => onValChange(range.value));
            num.addEventListener('change', () => onValChange(num.value));
            box.appendChild(mk('div', 'ed-slider-row', null, [range, num, mk('span', 'ed-unit', field.unit || '')]));
          }
        }
        inspectorBodyEl.appendChild(box);
      });
    }
    const createDiagramNode = () => createZoomableImageNode(SAMPLE_DIAGRAM_SVG, null, null, 'Sample diagram');

    function renderStageHeader(comp, vals) {
      specChipsEl.innerHTML = ''; stageQuickActionsEl.innerHTML = '';
      const chips = [['width', (vals.cardMaxWidthPx || '840') + 'px'], ['padding', (vals.cardPaddingPx || '32') + 'px'], ['align', vals.contentAlign || 'left']];
      if (comp.id === 'image') chips.push(['imgMaxH', (vals.imageMaxHeightPx || '360') + 'px']);
      if (comp.id === 'article-text') chips.push(['spacing', (vals.blockSpacingPx || '20') + 'px']);
      if (comp.id === 'mcq-options') chips.push(['cols', vals.optionColumns || '1'], ['gap', (vals.optionGapPx || '12') + 'px']);
      if (comp.id === 'flashcard') chips.push(['pos', vals.defaultTextPosition || 'belowImage'], ['flip', (vals.flipDurationMs || '450') + 'ms']);
      chips.forEach(([k, v]) => specChipsEl.appendChild(mk('span', 'ed-spec-chip', k + ': ' + v)));
      if (comp.id === 'flashcard') {
        const flipBtn = mk('button', 'ed-mini-btn', previewFcFlipped ? 'Show Front ↻' : 'Flip to Back ↻');
        flipBtn.type = 'button'; flipBtn.addEventListener('click', () => triggerPreviewFlashcardFlip());
        stageQuickActionsEl.appendChild(flipBtn);
      }
    }

    function renderPreview() {
      const comp = getActiveComponent(), vals = valuesByProfile[resolveTargetProfiles(comp)[0]];
      renderStageHeader(comp, vals);
      previewStageEl.innerHTML = '';

      if (comp.id === 'flashcard') {
        const scene = mk('div', 'mby-flashcard-scene'); applySlideLayoutVars(scene, vals);
        const flipMs = Math.max(50, parseInt(vals.flipDurationMs || '450', 10) || 450), textPos = vals.defaultTextPosition || 'belowImage';
        const inner = mk('div', 'mby-flashcard-inner');
        const buildFace = isBack => {
          const body = mk('div', 'mby-flashcard-body'), txt = mk('div');
          txt.innerHTML = isBack
            ? '<h3>Dual Export Architecture</h3><p>Desktop bundles ship <code>layouts.json</code> and per-item overrides; Android receives content-only <code>.mbypkg</code> archives.</p>'
            : '<h3>Front Prompt — Click Card to Flip</h3><p>Observe text vs. image placement (<code>' + textPos + '</code>) and flip animation duration (<code>' + flipMs + 'ms</code>).</p>';
          if (!isBack) {
            const img = createDiagramNode();
            if (textPos === 'aboveImage') { body.appendChild(txt); body.appendChild(img); } else { body.appendChild(img); body.appendChild(txt); }
          } else {
            const rRow = mk('div', 'mby-rating-row');
            rRow.innerHTML = '<button type="button" class="mby-rate-btn active-known">Got It</button><button type="button" class="mby-rate-btn">Needs Review</button>';
            body.appendChild(txt); body.appendChild(rRow);
          }
          return mk('div', 'mby-flashcard-face' + (isBack ? ' mby-flashcard-back' : ''), null, [createUpperTab('FLASHCARD'), body, mk('div', 'mby-flashcard-side-label', isBack ? 'Back' : 'Front')]);
        };
        const frontFace = buildFace(false), backFace = buildFace(true);
        frontFace.classList.toggle('is-active', !previewFcFlipped); backFace.classList.toggle('is-active', previewFcFlipped);
        inner.appendChild(frontFace); inner.appendChild(backFace); scene.appendChild(inner);
        triggerPreviewFlashcardFlip = attachFlashcardFlip(scene, inner, frontFace, backFace, flipMs, () => previewFcFlipped, v => { previewFcFlipped = v; }, null, () => renderStageHeader(comp, vals));
        scene.addEventListener('click', e => { if (!e.target.closest('button')) triggerPreviewFlashcardFlip(); });
        previewStageEl.appendChild(scene);
        return;
      }

      const card = mk('div', 'mby-card'); applySlideLayoutVars(card, vals);
      const body = mk('div', 'mby-card-body');
      if (comp.id === 'article-text') {
        const stack = mk('div', 'mby-article-blocks');
        stack.innerHTML = '<div><h2>Clean Architecture Overview</h2><p>Inline code pills like <code>mby build</code> stay crisp while fenced blocks keep syntax alignment left-anchored across themes.</p></div><div><pre><code>mby layout set article blockSpacingPx ' + (vals.blockSpacingPx || '20') + '</code></pre></div><div><p>Second article paragraph demonstrating vertical spacing between blocks and maximum readable card line width.</p></div>';
        card.appendChild(createUpperTab('ARTICLE')); body.appendChild(stack);
      } else if (comp.id === 'image') {
        card.appendChild(createUpperTab(imageScope === 'all' ? 'IMAGE BLOCK (ALL PROFILES)' : ('IMAGE BLOCK (' + imageScope.toUpperCase() + ')')));
        body.appendChild(mk('div', 'mby-prompt', 'Diagram & Media Scaling Preview'));
        body.appendChild(createDiagramNode());
        body.appendChild(mk('div', 'ed-mcq-question-note', 'Constrained to max-height: ' + (vals.imageMaxHeightPx || '360') + 'px with white inline SVG zoom indicator.'));
      } else if (comp.id === 'mcq-question') {
        const prompt = mk('div', 'mby-prompt');
        prompt.innerHTML = '<p>Which layer in Clean CLI defines abstract ports such as <code>IWebAppGenerator</code> while remaining free of SQLite or Markdig dependencies?</p>';
        card.appendChild(createUpperTab('MCQ')); body.appendChild(prompt);
        body.appendChild(mk('div', 'ed-mcq-question-note', 'Previewing Question Prompt framing (switch to "MCQ Options" tab to tune option grid & spacing).'));
      } else if (comp.id === 'mcq-options') {
        card.appendChild(createUpperTab('MCQ (OPTIONS)'));
        body.appendChild(mk('div', 'mby-prompt', 'Click options and test 1-column vs 2-column grid layout:'));
        const grid = mk('div', 'mby-options-grid');
        [
          { idx: 1, text: '<strong>Option 1:</strong> Unified via <code>Directory.Build.props</code>', correct: true, hasImg: false },
          { idx: 2, text: '<strong>Option 2:</strong> Image-backed architectural choice', correct: false, hasImg: true },
          { idx: 3, text: '<strong>Option 3:</strong> Horizontal slider responsiveness', correct: false, hasImg: false },
          { idx: 4, text: '<strong>Option 4:</strong> Interactive Check Answer state', correct: false, hasImg: false }
        ].forEach(opt => {
          const btn = mk('button', 'mby-option-btn'); btn.type = 'button';
          if (previewMcqSelected.has(opt.idx)) btn.classList.add('selected');
          if (previewMcqChecked) btn.classList.add(opt.correct ? 'correct' : (previewMcqSelected.has(opt.idx) ? 'wrong' : 'x'));
          btn.classList.remove('x');
          if (opt.hasImg) btn.appendChild(createDiagramNode());
          const t = mk('div'); t.innerHTML = opt.text; btn.appendChild(t);
          btn.addEventListener('click', () => { previewMcqSelected.clear(); previewMcqSelected.add(opt.idx); renderPreview(); });
          grid.appendChild(btn);
        });
        const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
        checkBtn.addEventListener('click', () => { previewMcqChecked = !previewMcqChecked; renderPreview(); });
        const fb = mk('span'); if (previewMcqChecked) renderMcqFeedback(fb, previewMcqSelected.has(1));
        body.appendChild(grid); body.appendChild(mk('div', 'mby-action-row', null, [checkBtn, fb]));
      }
      card.appendChild(body); previewStageEl.appendChild(card);
    }

    document.querySelectorAll('#themeSeg button').forEach(btn => btn.addEventListener('click', () => {
      ['article', 'mcq', 'flashcard'].forEach(p => { valuesByProfile[p].themeMode = btn.dataset.themeVal; });
      setDirty(true); syncGlobalControlsUi();
    }));
    globalSlideRange.addEventListener('input', () => {
      ['article', 'mcq', 'flashcard'].forEach(p => { valuesByProfile[p].slideTransitionMs = globalSlideRange.value; });
      setDirty(true); syncGlobalControlsUi();
    });
    $('testSlideBtn').addEventListener('click', () => { previewStageEl.classList.add('is-sliding'); setTimeout(() => previewStageEl.classList.remove('is-sliding'), 160); });
    $('saveAllBtn').addEventListener('click', async () => {
      setStatus('Saving...', '');
      const data = await (await fetch('/api/layout', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ profiles: valuesByProfile }) })).json();
      if (data.saved) { setDirty(false); setStatus('All component layouts saved.', 'ok'); }
      else setStatus('Save error: ' + (data.errors || []).join(', '), 'err');
    });
    $('resetCompBtn').addEventListener('click', async () => {
      const comp = getActiveComponent();
      const targets = resolveTargetProfiles(comp);
      const keys = (comp.fields || []).map(k => k.key);
      setStatus('Resetting component defaults...', '');
      for (const t of targets) await fetch('/api/layout/reset', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ targetType: t, keys }) });
      await loadAllLayouts(); setDirty(false); setStatus('Reset ' + comp.label + ' to defaults.', 'ok');
    });
    async function loadAllLayouts() {
      const data = await (await fetch('/api/layout')).json();
      ['article', 'mcq', 'flashcard'].forEach(k => {
        specsByProfile[k] = {}; valuesByProfile[k] = {};
        (data.profiles?.[k] || []).forEach(p => { specsByProfile[k][p.property] = p; valuesByProfile[k][p.property] = p.currentValue; });
      });
      syncGlobalControlsUi(); renderTabs(); renderInspector(); renderPreview();
    }
    loadAllLayouts().catch(err => setStatus('Failed to load layouts: ' + err.message, 'err'));
  </script>
</body>
</html>
""";
    }
}
