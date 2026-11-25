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
    public string BuildHtml(string initialComponent, CliToolMetadata toolMetadata, string? csrfToken = null)
    {
        var encodedInitial = JsonSerializer.Serialize(initialComponent);
        var encodedCsrf = JsonSerializer.Serialize(csrfToken ?? string.Empty);
        var encodedTitleVersion = WebUtility.HtmlEncode($"{toolMetadata.ExecutableName} v{toolMetadata.Version}");

        return $$"""
<!DOCTYPE html>
<html lang="en" data-theme="berry">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <meta name="generator" content="{{WebUtility.HtmlEncode($"{toolMetadata.ProductName} ({toolMetadata.ExecutableName}) {toolMetadata.Version}")}}" />
  <link rel="icon" href="data:," />
  <title>Meloberry Component Layout Editor — {{encodedTitleVersion}}</title>
  <style>
{{WebAppSharedStyles.BuildEditorCss()}}
html, body { height: 100%; overflow: hidden; }
.ed-shell { display: flex; flex-direction: column; height: 100vh; overflow: hidden; }
.ed-topbar { flex-shrink: 0; display: flex; align-items: center; justify-content: space-between; gap: 16px; flex-wrap: wrap; padding: 12px 20px; background: var(--surface); border-bottom: 1px solid var(--border); }
.ed-brand-group, .ed-top-controls, .ed-actions { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
.ed-title { font-size: 16px; font-weight: 800; color: var(--accent); letter-spacing: -0.02em; }
.ed-ver-pill { padding: 3px 9px; border-radius: 999px; background: var(--accent-soft); color: var(--accent); border: 1px solid var(--border); font-size: 11px; font-weight: 700; font-family: ui-monospace, Consolas, monospace; }
.ed-dirty-pill { padding: 3px 9px; border-radius: 999px; background: var(--callout-pearl-bg); color: var(--callout-pearl); border: 1px solid var(--border); font-size: 11px; font-weight: 700; }
.ed-inline-ctrl { display: flex; align-items: center; gap: 8px; font-size: 12px; font-weight: 700; color: var(--muted); }
.ed-seg-group { display: inline-flex; border: 1px solid var(--border); border-radius: 9px; overflow: hidden; background: var(--bg); }
.ed-seg-btn { padding: 6px 11px; border: none; background: transparent; color: var(--muted); font-size: 12px; font-weight: 600; cursor: pointer; }
.ed-seg-btn.active { background: var(--accent); color: #ffffff; }
.ed-speed-range { width: 96px; accent-color: var(--accent); }
.ed-speed-num { width: 64px; padding: 4px 6px; border-radius: 8px; border: 1px solid var(--border); background: var(--bg); color: var(--text); font-size: 12px; font-weight: 700; text-align: right; }
.ed-mini-btn { padding: 6px 11px; border-radius: 8px; border: 1px solid var(--border); background: var(--bg); color: var(--text); font-size: 12px; font-weight: 600; cursor: pointer; }
.ed-mini-btn:hover { border-color: var(--accent); color: var(--accent); }
.ed-workspace { flex: 1; min-height: 0; display: grid; grid-template-columns: 390px 1fr; }
.ed-sidebar { background: var(--surface); border-right: 1px solid var(--border); display: flex; flex-direction: column; min-height: 0; }
@media (max-width: 960px) {
  .ed-workspace { grid-template-columns: 1fr; grid-template-rows: minmax(220px, 44vh) minmax(240px, 1fr); }
  .ed-sidebar { border-right: none; border-bottom: 1px solid var(--border); overflow: hidden; }
}
@media (max-width: 960px) and (max-height: 600px) {
  html, body, .ed-shell { height: auto; min-height: 100vh; overflow: visible; }
  .ed-workspace { grid-template-rows: auto auto; }
}
.ed-comp-tabs { display: flex; flex-wrap: wrap; gap: 6px; padding: 12px 14px; max-height: 150px; overflow-y: auto; border-bottom: 1px solid var(--border); background: var(--bg); }
.ed-comp-tab { padding: 6px 10px; border-radius: 10px; border: 1px solid var(--border); background: var(--surface); color: var(--text); font-size: 12px; font-weight: 700; cursor: pointer; transition: border-color 140ms ease, background 140ms ease, color 140ms ease; }
.ed-comp-tab.active { background: var(--accent); border-color: var(--accent); color: #ffffff; }
.ed-inspector-scroll { flex: 1; min-height: 0; overflow-y: auto; padding: 16px; display: flex; flex-direction: column; gap: 14px; }
.ed-comp-header { padding-bottom: 10px; border-bottom: 1px solid var(--border); }
.ed-comp-title { margin: 0 0 4px; font-size: 16px; font-weight: 800; }
.ed-comp-sub { margin: 0; font-size: 12px; color: var(--muted); line-height: 1.45; }
.ed-scope-bar { display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 10px 12px; border-radius: 10px; border: 1px solid var(--border); background: var(--bg); font-size: 12px; font-weight: 700; flex-wrap: wrap; }
.ed-scope-note { padding: 9px 11px; border-radius: 10px; border: 1px dashed var(--border); background: var(--bg); font-size: 11.5px; color: var(--muted); line-height: 1.45; }
.ed-field { border: 1px solid var(--border); border-radius: 12px; padding: 11px 12px; background: var(--bg); display: flex; flex-direction: column; gap: 8px; }
.ed-field-top { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
.ed-field-title-row { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
.ed-field-label { font-size: 13px; font-weight: 700; color: var(--text); }
.ed-mixed-badge { padding: 2px 7px; border-radius: 999px; background: var(--callout-pearl-bg); color: var(--callout-pearl); border: 1px solid var(--border); font-size: 10.5px; font-weight: 700; }
.ed-field-key { font-family: ui-monospace, Consolas, monospace; font-size: 11px; color: var(--muted); }
.ed-field-desc { font-size: 11px; color: var(--muted); line-height: 1.35; }
.ed-slider-row { display: flex; align-items: center; gap: 8px; }
.ed-slider { flex: 1; accent-color: var(--accent); }
.ed-num-input { width: 68px; padding: 5px 7px; border-radius: 8px; border: 1px solid var(--border); background: var(--surface); color: var(--text); font-size: 13px; font-weight: 700; text-align: right; }
.ed-unit { font-size: 11px; font-weight: 700; color: var(--muted); min-width: 24px; }
.ed-field-reset { padding: 3px 7px; border-radius: 6px; border: 1px solid var(--border); background: var(--surface); color: var(--muted); font-size: 11px; font-weight: 600; cursor: pointer; }
.ed-field-reset:hover { color: var(--accent); border-color: var(--accent); }
.ed-sidebar-footer { padding: 11px 16px; border-top: 1px solid var(--border); font-size: 12px; color: var(--muted); display: flex; justify-content: space-between; align-items: center; gap: 8px; }
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
.mby-zoom-modal { background: rgba(0, 0, 0, 0.92); }
.mby-zoom-stage, .mby-zoom-stage:active { cursor: default; padding: 16px; }
.mby-zoom-img { transition: transform 140ms ease; }
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
          <input id="globalSlideRange" class="ed-speed-range" type="range" min="100" max="1200" step="1" value="380" aria-label="Slide transition duration slider" />
          <input id="globalSlideNum" class="ed-speed-num" type="number" min="100" max="1200" value="380" aria-label="Slide transition milliseconds" />
          <span id="globalSlideVal" class="ed-unit">ms</span>
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
  <div id="editorZoomModal" class="mby-zoom-modal hidden" role="dialog" aria-modal="true" aria-label="Image zoom preview">
    <div class="mby-zoom-topbar">
      <span id="editorZoomTitle" class="mby-zoom-pill">Preview Zoom</span>
      <div class="mby-zoom-actions">
        <button id="edZoomInBtn" type="button" class="mby-zoom-btn">+</button>
        <button id="edZoomOutBtn" type="button" class="mby-zoom-btn">−</button>
        <button id="edZoomResetBtn" type="button" class="mby-zoom-btn">Reset</button>
        <button id="edZoomCloseBtn" type="button" class="mby-zoom-btn">Close</button>
      </div>
    </div>
    <div id="editorZoomStage" class="mby-zoom-stage"><img id="editorZoomImg" class="mby-zoom-img" alt="Zoomed preview diagram" /></div>
  </div>
  <script>
{{WebAppSharedStyles.BuildEditorCoreJs()}}
    const INITIAL_COMPONENT = {{encodedInitial}};
    const EDITOR_CSRF_TOKEN = {{encodedCsrf}};
    const COMPONENTS = [
      { id: 'article-text', label: 'Article Text Block', profile: 'article', description: 'Controls vertical Markdown headings, body copy, inline code, fenced code blocks, figure framing, and inter-block spacing inside Article slides.',
        fields: [{ key: 'cardMaxWidthPx', label: 'Card Max Width', unit: 'px' }, { key: 'cardPaddingPx', label: 'Inner Card Padding', unit: 'px' }, { key: 'promptFontSizePx', label: 'Heading Font Size', unit: 'px' }, { key: 'bodyFontSizePx', label: 'Body Text Size', unit: 'px' }, { key: 'blockSpacingPx', label: 'Block Vertical Spacing', unit: 'px' }, { key: 'imageMaxHeightPx', label: 'Figure Image Max Height', unit: 'px' }, { key: 'contentAlign', label: 'Content Alignment', unit: '' }] },
      { id: 'callout', label: 'Callout', profile: 'article', description: 'Previews Pearl, Clinical, and Warning callout blocks inheriting the Article profile.',
        fields: [{ key: 'cardMaxWidthPx', label: 'Card Max Width', unit: 'px' }, { key: 'cardPaddingPx', label: 'Inner Card Padding', unit: 'px' }, { key: 'bodyFontSizePx', label: 'Callout Text Size', unit: 'px' }, { key: 'blockSpacingPx', label: 'Block Vertical Spacing', unit: 'px' }, { key: 'contentAlign', label: 'Content Alignment', unit: '' }] },
      { id: 'table', label: 'Table', profile: 'article', description: 'Previews interactive study tables (sticky first column, hideable recall column, caption & footnote) inheriting the Article profile.',
        fields: [{ key: 'cardMaxWidthPx', label: 'Card Max Width', unit: 'px' }, { key: 'cardPaddingPx', label: 'Inner Card Padding', unit: 'px' }, { key: 'bodyFontSizePx', label: 'Table Text Size', unit: 'px' }, { key: 'blockSpacingPx', label: 'Block Vertical Spacing', unit: 'px' }, { key: 'contentAlign', label: 'Content Alignment', unit: '' }] },
      { id: 'image', label: 'Image Block', profile: 'scoped', description: 'Controls image height uniformly across all card types, or select a specific profile below to tune its framing and media alignment.',
        fields: [{ key: 'imageMaxHeightPx', label: 'Image Max Height', unit: 'px' }, { key: 'cardMaxWidthPx', label: 'Card Max Width', unit: 'px' }, { key: 'cardPaddingPx', label: 'Inner Card Padding', unit: 'px' }, { key: 'contentAlign', label: 'Media Alignment', unit: '' }] },
      { id: 'mcq-question', label: 'MCQ Question', profile: 'mcq', description: 'Controls the MCQ card frame, padding, alignment, and prompt heading size above the answer choices.',
        fields: [{ key: 'cardMaxWidthPx', label: 'MCQ Card Max Width', unit: 'px' }, { key: 'cardPaddingPx', label: 'Inner Card Padding', unit: 'px' }, { key: 'promptFontSizePx', label: 'Question Prompt Size', unit: 'px' }, { key: 'contentAlign', label: 'Prompt Alignment', unit: '' }] },
      { id: 'mcq-options', label: 'MCQ Options', profile: 'mcq', description: 'Controls answer option grid columns (1 vertical stack vs. 2 columns), option spacing, option text size, and embedded option image cap.',
        fields: [{ key: 'optionColumns', label: 'Option Grid Columns', unit: 'cols' }, { key: 'optionGapPx', label: 'Option Gap', unit: 'px' }, { key: 'bodyFontSizePx', label: 'Option Text Size', unit: 'px' }, { key: 'imageMaxHeightPx', label: 'Option Image Max Height', unit: 'px' }, { key: 'contentAlign', label: 'Option Text Alignment', unit: '' }] },
      { id: 'fill-blank', label: 'Fill-in-the-Blank', profile: 'mcq', description: 'Previews inline fill-blank prompts and inputs inheriting the MCQ profile.',
        fields: [{ key: 'cardMaxWidthPx', label: 'Card Max Width', unit: 'px' }, { key: 'cardPaddingPx', label: 'Inner Card Padding', unit: 'px' }, { key: 'promptFontSizePx', label: 'Prompt Font Size', unit: 'px' }, { key: 'bodyFontSizePx', label: 'Body Text Size', unit: 'px' }, { key: 'contentAlign', label: 'Content Alignment', unit: '' }] },
      { id: 'assertion-reason', label: 'Assertion-Reason', profile: 'mcq', description: 'Previews Assertion (A) / Reason (R) stacks and option grids inheriting the MCQ profile.',
        fields: [{ key: 'cardMaxWidthPx', label: 'Card Max Width', unit: 'px' }, { key: 'cardPaddingPx', label: 'Inner Card Padding', unit: 'px' }, { key: 'bodyFontSizePx', label: 'Body & Option Size', unit: 'px' }, { key: 'optionColumns', label: 'Option Grid Columns', unit: 'cols' }, { key: 'optionGapPx', label: 'Option Gap', unit: 'px' }, { key: 'contentAlign', label: 'Content Alignment', unit: '' }] },
      { id: 'spot-error', label: 'Spot-the-Error', profile: 'mcq', description: 'Previews clickable error-span chips and No-Error controls inheriting the MCQ profile.',
        fields: [{ key: 'cardMaxWidthPx', label: 'Card Max Width', unit: 'px' }, { key: 'cardPaddingPx', label: 'Inner Card Padding', unit: 'px' }, { key: 'promptFontSizePx', label: 'Statement Size', unit: 'px' }, { key: 'bodyFontSizePx', label: 'Control Text Size', unit: 'px' }, { key: 'contentAlign', label: 'Content Alignment', unit: '' }] },
      { id: 'diff', label: 'Differentiation', profile: 'mcq', description: 'Previews comparison matrix tables and word banks inheriting the MCQ profile.',
        fields: [{ key: 'cardMaxWidthPx', label: 'Card Max Width', unit: 'px' }, { key: 'cardPaddingPx', label: 'Inner Card Padding', unit: 'px' }, { key: 'promptFontSizePx', label: 'Title Size', unit: 'px' }, { key: 'bodyFontSizePx', label: 'Table Cell Size', unit: 'px' }, { key: 'contentAlign', label: 'Content Alignment', unit: '' }] },
      { id: 'mechanism', label: 'Mechanism', profile: 'mcq', description: 'Previews ordered pathway step banks and sequence slots inheriting the MCQ profile.',
        fields: [{ key: 'cardMaxWidthPx', label: 'Card Max Width', unit: 'px' }, { key: 'cardPaddingPx', label: 'Inner Card Padding', unit: 'px' }, { key: 'promptFontSizePx', label: 'Title Size', unit: 'px' }, { key: 'bodyFontSizePx', label: 'Step Text Size', unit: 'px' }, { key: 'contentAlign', label: 'Content Alignment', unit: '' }] },
      { id: 'relabel', label: 'Relabel', profile: 'mcq', description: 'Previews interactive image-occlusion masks and diagram scaling inheriting the MCQ profile.',
        fields: [{ key: 'cardMaxWidthPx', label: 'Card Max Width', unit: 'px' }, { key: 'cardPaddingPx', label: 'Inner Card Padding', unit: 'px' }, { key: 'promptFontSizePx', label: 'Prompt Font Size', unit: 'px' }, { key: 'bodyFontSizePx', label: 'Label Text Size', unit: 'px' }, { key: 'imageMaxHeightPx', label: 'Diagram Max Height', unit: 'px' }, { key: 'contentAlign', label: 'Content Alignment', unit: '' }] },
      { id: 'flashcard', label: 'Flashcard', profile: 'flashcard', description: 'Controls front/back panel dimensions, heading & body typography, media height, text vs. image order, and 3D flip duration.',
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
    let imageScope = 'all', previewMcqSelected = new Set([1]), previewMcqChecked = false, previewFcFlipped = false, previewFcRating = 'known';
    let previewArSelected = 1, previewSpotIdx = 0, previewSpotNoError = false, previewTableHidden = true;
    let previewMechOrder = ['Step 1: Stimulus binds receptor', 'Step 2: Second messenger cascade'], previewRelabelRevealed = new Set();
    let triggerPreviewFlashcardFlip = () => {}, cancelPreviewFlashcardFlip = () => {}, previewRenderToken = 0;
    let testSlideTimer = null, isDirty = false, isBusy = false, zoomScale = 1;
    const specsByProfile = { article: {}, mcq: {}, flashcard: {} };
    const valuesByProfile = { article: {}, mcq: {}, flashcard: {} };
    const savedValuesByProfile = { article: {}, mcq: {}, flashcard: {} };
    const $ = id => document.getElementById(id);
    const componentTabsEl = $('componentTabs'), inspectorBodyEl = $('inspectorBody'), previewStageEl = $('previewStage');
    const specChipsEl = $('specChips'), stageQuickActionsEl = $('stageQuickActions'), activeProfileHintEl = $('activeProfileHint');
    const saveStatusEl = $('saveStatus'), dirtyBadgeEl = $('dirtyBadge'), globalSlideRange = $('globalSlideRange'), globalSlideNum = $('globalSlideNum'), globalSlideVal = $('globalSlideVal');
    const saveAllBtn = $('saveAllBtn'), resetCompBtn = $('resetCompBtn');
    const zoomModal = $('editorZoomModal'), zoomImg = $('editorZoomImg'), zoomTitle = $('editorZoomTitle');

    function openEditorZoomLightbox(src, alt) {
      zoomImg.src = src; zoomImg.alt = alt || 'Preview diagram';
      zoomTitle.textContent = alt || 'Preview Zoom';
      zoomScale = 1; zoomImg.style.transform = 'scale(1)';
      zoomModal.classList.remove('hidden');
    }
    function closeEditorZoomLightbox() { zoomModal.classList.add('hidden'); }
    $('edZoomInBtn').addEventListener('click', () => { zoomScale = Math.min(4, zoomScale + 0.5); zoomImg.style.transform = 'scale(' + zoomScale + ')'; });
    $('edZoomOutBtn').addEventListener('click', () => { zoomScale = Math.max(1, zoomScale - 0.5); zoomImg.style.transform = 'scale(' + zoomScale + ')'; });
    $('edZoomResetBtn').addEventListener('click', () => { zoomScale = 1; zoomImg.style.transform = 'scale(1)'; });
    $('edZoomCloseBtn').addEventListener('click', closeEditorZoomLightbox);
    zoomModal.addEventListener('click', e => { if (e.target === zoomModal || e.target === $('editorZoomStage')) closeEditorZoomLightbox(); });
    document.addEventListener('keydown', e => {
      if (e.key === 'Escape' && !zoomModal.classList.contains('hidden')) closeEditorZoomLightbox();
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') { e.preventDefault(); if (!isBusy) saveAllBtn.click(); }
    });
    window.addEventListener('beforeunload', e => { if (isDirty) { e.preventDefault(); e.returnValue = ''; } });

    function hasUnsavedChanges() {
      return ['article', 'mcq', 'flashcard'].some(p => {
        const keys = new Set([...Object.keys(valuesByProfile[p] || {}), ...Object.keys(savedValuesByProfile[p] || {})]);
        return Array.from(keys).some(k => String(valuesByProfile[p]?.[k] ?? '') !== String(savedValuesByProfile[p]?.[k] ?? ''));
      });
    }
    function syncDirtyState() { isDirty = hasUnsavedChanges(); dirtyBadgeEl.classList.toggle('hidden', !isDirty); }
    const setStatus = (text, cls) => { saveStatusEl.textContent = text; saveStatusEl.className = 'ed-status' + (cls ? ' ' + cls : ''); };
    const setBusy = flag => { isBusy = flag; saveAllBtn.disabled = flag; resetCompBtn.disabled = flag; };
    const getActiveComponent = () => COMPONENTS.find(c => c.id === activeComponentId) || COMPONENTS[0];
    const resolveTargetProfiles = comp => comp.id === 'image' ? (imageScope === 'all' ? ['article', 'mcq', 'flashcard'] : [imageScope]) : [comp.profile];
    const getVisibleFields = comp => (comp.id === 'image' && imageScope === 'all') ? comp.fields.filter(f => f.key === 'imageMaxHeightPx') : comp.fields;
    const getFieldValue = (comp, key) => valuesByProfile[resolveTargetProfiles(comp)[0]][key];
    function setFieldValue(comp, key, val) {
      resolveTargetProfiles(comp).forEach(p => { if (specsByProfile[p][key]) valuesByProfile[p][key] = val; });
      syncDirtyState();
    }
    function resolveDeckGlobalValue(prop, fallback) {
      let firstPresent = null;
      for (const target of ['mcq', 'article', 'flashcard']) {
        const v = valuesByProfile[target]?.[prop];
        if (v === undefined || v === null || String(v).trim() === '') continue;
        const s = String(v);
        if (firstPresent === null) firstPresent = s;
        if (s.toLowerCase() !== fallback.toLowerCase()) return s;
      }
      return firstPresent ?? fallback;
    }
    function syncGlobalControlsUi() {
      const theme = resolveDeckGlobalValue('themeMode', 'berry'), slideMs = resolveDeckGlobalValue('slideTransitionMs', '380');
      document.documentElement.setAttribute('data-theme', theme);
      document.querySelectorAll('#themeSeg button').forEach(b => b.classList.toggle('active', b.dataset.themeVal === theme));
      globalSlideRange.value = slideMs; globalSlideNum.value = slideMs; globalSlideVal.textContent = 'ms';
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
        const isActive = comp.id === activeComponentId;
        const btn = mk('button', 'ed-comp-tab' + (isActive ? ' active' : ''), comp.label);
        btn.type = 'button';
        btn.setAttribute('role', 'tab');
        btn.setAttribute('aria-selected', String(isActive));
        btn.dataset.component = comp.id;
        btn.addEventListener('click', () => { activeComponentId = comp.id; renderTabs(); renderInspector(); renderPreview(); });
        componentTabsEl.appendChild(btn);
      });
    }
    function renderInspector() {
      const comp = getActiveComponent(), targets = resolveTargetProfiles(comp), primaryProfile = targets[0];
      activeProfileHintEl.textContent = comp.id === 'image' ? 'scope: ' + imageScope : 'profile: ' + comp.profile;
      inspectorBodyEl.innerHTML = '';
      inspectorBodyEl.appendChild(mk('div', 'ed-comp-header', null, [mk('h2', 'ed-comp-title', comp.label), mk('p', 'ed-comp-sub', comp.description)]));

      if (comp.id === 'image') {
        inspectorBodyEl.appendChild(mk('div', 'ed-scope-bar', null, [
          mk('span', null, 'Target Profile'),
          makeSegButtons([['all', 'All'], ['article', 'Article'], ['mcq', 'MCQ'], ['flashcard', 'Flashcard']], v => imageScope === v, v => { imageScope = v; renderInspector(); renderPreview(); })
        ]));
        if (imageScope === 'all') {
          inspectorBodyEl.appendChild(mk('div', 'ed-scope-note', 'Scope "All" edits shared Image Max Height only so per-profile card framing and media alignment are never overwritten unintentionally. Select Article, MCQ, or Flashcard above to tune card width, padding, or alignment.'));
        }
      }

      getVisibleFields(comp).forEach(field => {
        const spec = specsByProfile[primaryProfile][field.key];
        if (!spec) return;
        const currentVal = String(getFieldValue(comp, field.key) ?? spec.defaultValue);
        const isMixed = targets.length > 1 && new Set(targets.map(p => String(valuesByProfile[p]?.[field.key] ?? specsByProfile[p]?.[field.key]?.defaultValue ?? ''))).size > 1;
        const resetBtn = mk('button', 'ed-field-reset', 'Default (' + spec.defaultValue + ')');
        resetBtn.type = 'button';
        resetBtn.addEventListener('click', () => { setFieldValue(comp, field.key, spec.defaultValue); renderInspector(); renderPreview(); });

        const titleRow = mk('div', 'ed-field-title-row', null, [mk('div', 'ed-field-label', field.label)]);
        if (isMixed) titleRow.appendChild(mk('span', 'ed-mixed-badge', 'Mixed: ' + targets.map(p => p + '=' + valuesByProfile[p][field.key]).join(', ')));

        const box = mk('div', 'ed-field', null, [
          mk('div', 'ed-field-top', null, [mk('div', null, null, [titleRow, mk('div', 'ed-field-key', spec.property + ' (' + spec.allowedBounds + ')')]), resetBtn]),
          mk('div', 'ed-field-desc', spec.description)
        ]);

        if (spec.valueType === 'enum') {
          const opts = (spec.allowedBounds || '').split('|').map(s => s.trim()).filter(Boolean).map(v => [v, v]);
          box.appendChild(makeSegButtons(opts, v => currentVal === v, (v, b, seg) => {
            setFieldValue(comp, field.key, v);
            seg.querySelectorAll('button').forEach(x => x.classList.remove('active')); b.classList.add('active'); renderInspector(); renderPreview();
          }));
        } else {
          const [min, max] = spec.allowedBounds.split('..').map(Number);
          if (max - min <= 4) {
            const opts = Array.from({ length: max - min + 1 }, (_, i) => [String(min + i), String(min + i) + (field.unit ? ' ' + field.unit : '')]);
            box.appendChild(makeSegButtons(opts, v => currentVal === v, (v, b, seg) => {
              setFieldValue(comp, field.key, v);
              seg.querySelectorAll('button').forEach(x => x.classList.remove('active')); b.classList.add('active'); renderInspector(); renderPreview();
            }));
          } else {
            const range = mk('input', 'ed-slider'), num = mk('input', 'ed-num-input');
            Object.assign(range, { type: 'range', min: String(min), max: String(max), value: currentVal });
            Object.assign(num, { type: 'number', min: String(min), max: String(max), value: currentVal });
            const onValChange = raw => {
              const p = parseInt(raw, 10);
              if (Number.isNaN(p)) { num.value = range.value; return; }
              const norm = String(Math.max(min, Math.min(max, p)));
              range.value = norm; num.value = norm; setFieldValue(comp, field.key, norm); renderPreview();
            };
            range.addEventListener('input', () => onValChange(range.value));
            num.addEventListener('change', () => onValChange(num.value));
            num.addEventListener('blur', () => { if (Number.isNaN(parseInt(num.value, 10))) num.value = range.value; });
            box.appendChild(mk('div', 'ed-slider-row', null, [range, num, mk('span', 'ed-unit', field.unit || '')]));
          }
        }
        inspectorBodyEl.appendChild(box);
      });
    }
    const createDiagramNode = () => createZoomableImageNode(SAMPLE_DIAGRAM_SVG, openEditorZoomLightbox, null, 'Sample diagram');

    function renderStageHeader(comp, vals) {
      specChipsEl.innerHTML = ''; stageQuickActionsEl.innerHTML = '';
      const chips = [['width', (vals.cardMaxWidthPx || '840') + 'px'], ['padding', (vals.cardPaddingPx || '32') + 'px'], ['align', vals.contentAlign || 'left']];
      if (['image', 'article-text', 'mcq-options', 'relabel'].includes(comp.id)) chips.push(['imgMaxH', (vals.imageMaxHeightPx || '360') + 'px']);
      if (['article-text', 'callout', 'table'].includes(comp.id)) chips.push(['spacing', (vals.blockSpacingPx || '20') + 'px']);
      if (['mcq-options', 'assertion-reason'].includes(comp.id)) chips.push(['cols', vals.optionColumns || '1'], ['gap', (vals.optionGapPx || '12') + 'px']);
      if (comp.id === 'flashcard') chips.push(['pos', vals.defaultTextPosition || 'belowImage'], ['flip', (vals.flipDurationMs || '450') + 'ms']);
      chips.forEach(([k, v]) => specChipsEl.appendChild(mk('span', 'ed-spec-chip', k + ': ' + v)));
      if (comp.id === 'flashcard') {
        const flipBtn = mk('button', 'ed-mini-btn', previewFcFlipped ? 'Show Front ↻' : 'Flip to Back ↻');
        flipBtn.type = 'button'; flipBtn.addEventListener('click', () => triggerPreviewFlashcardFlip());
        stageQuickActionsEl.appendChild(flipBtn);
      }
    }

    function renderPreview() {
      cancelPreviewFlashcardFlip();
      cancelPreviewFlashcardFlip = () => {};
      triggerPreviewFlashcardFlip = () => {};
      const myRenderToken = ++previewRenderToken;

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
            ? '<h3>Portable .mbypkg v2.0 Export</h3><p>Exports are content-only by default; pass <code>--include-layout</code> to bundle global profiles and entity <code>layoutOverrides</code>.</p>'
            : '<h3>Front Prompt — Click Card to Flip</h3><p>Observe text vs. image placement (<code>' + textPos + '</code>), heading scaling, and flip duration (<code>' + flipMs + 'ms</code>).</p>';
          if (!isBack) {
            const img = createDiagramNode();
            if (textPos === 'aboveImage') { body.appendChild(txt); body.appendChild(img); } else { body.appendChild(img); body.appendChild(txt); }
          } else {
            const rRow = mk('div', 'mby-rating-row');
            const knownBtn = mk('button', 'mby-rate-btn' + (previewFcRating === 'known' ? ' active-known' : ''), 'Got It');
            knownBtn.type = 'button';
            knownBtn.addEventListener('click', e => { e.stopPropagation(); previewFcRating = 'known'; renderPreview(); });
            const reviewBtn = mk('button', 'mby-rate-btn' + (previewFcRating === 'review' ? ' active-review' : ''), 'Needs Review');
            reviewBtn.type = 'button';
            reviewBtn.addEventListener('click', e => { e.stopPropagation(); previewFcRating = 'review'; renderPreview(); });
            rRow.appendChild(knownBtn); rRow.appendChild(reviewBtn);
            body.appendChild(txt); body.appendChild(rRow);
          }
          return mk('div', 'mby-flashcard-face' + (isBack ? ' mby-flashcard-back' : ''), null, [createUpperTab('FLASHCARD'), body, mk('div', 'mby-flashcard-side-label', isBack ? 'Back' : 'Front')]);
        };
        const frontFace = buildFace(false), backFace = buildFace(true);
        frontFace.classList.toggle('is-active', !previewFcFlipped); backFace.classList.toggle('is-active', previewFcFlipped);
        inner.appendChild(frontFace); inner.appendChild(backFace); scene.appendChild(inner);
        const flipFn = attachFlashcardFlip(scene, inner, frontFace, backFace, flipMs, () => previewFcFlipped, v => { previewFcFlipped = v; }, null, () => {
          if (myRenderToken === previewRenderToken && activeComponentId === comp.id) renderStageHeader(comp, vals);
        });
        triggerPreviewFlashcardFlip = flipFn;
        cancelPreviewFlashcardFlip = () => { if (flipFn && typeof flipFn.cancel === 'function') flipFn.cancel(); };
        scene.addEventListener('click', e => { if (!e.target.closest('button')) triggerPreviewFlashcardFlip(); });
        previewStageEl.appendChild(scene);
        return;
      }

      const card = mk('div', 'mby-card'); applySlideLayoutVars(card, vals);
      const body = mk('div', 'mby-card-body');
      if (comp.id === 'article-text') {
        const stack = mk('div', 'mby-article-blocks');
        const intro = mk('div');
        intro.innerHTML = '<h2>Clean Architecture Overview</h2><p>Inline code pills like <code>mby build</code> stay crisp while fenced blocks keep syntax alignment left-anchored across themes.</p>';
        const codeBlock = mk('div');
        codeBlock.innerHTML = '<pre><code>mby layout set article blockSpacingPx ' + (vals.blockSpacingPx || '20') + '</code></pre>';
        const fig = mk('figure', 'mby-figure', null, [createDiagramNode(), mk('figcaption', 'mby-figcaption', 'Figure 1: Semantic figure & figcaption alignment follows contentAlign.')]);
        const outro = mk('div');
        outro.innerHTML = '<p>Second article paragraph demonstrating vertical spacing between blocks and maximum readable card line width.</p>';
        stack.appendChild(intro); stack.appendChild(codeBlock); stack.appendChild(fig); stack.appendChild(outro);
        card.appendChild(createUpperTab('ARTICLE')); body.appendChild(stack);
      } else if (comp.id === 'callout') {
        const stack = mk('div', 'mby-article-blocks');
        const makeCallout = (tone, title, text) => mk('div', 'mby-callout mby-callout-' + tone, null, [
          mk('div', 'mby-callout-header', title),
          mk('div', 'mby-callout-body', null, [mk('p', null, text)])
        ]);
        stack.appendChild(makeCallout('pearl', 'High-Yield Pearl', 'Callouts inherit Article card width, padding, body text size, and block vertical spacing.'));
        stack.appendChild(makeCallout('clinical', 'Clinical Correlation', 'Use :::callout{tone="clinical"} for bedside pearls and diagnostic links.'));
        stack.appendChild(makeCallout('warning', 'Warning', 'Use :::callout{tone="warning"} to highlight contraindications or common pitfalls.'));
        card.appendChild(createUpperTab('ARTICLE · CALLOUTS')); body.appendChild(stack);
      } else if (comp.id === 'table') {
        const stack = mk('div', 'mby-article-blocks');
        const wrap = mk('div', 'mby-native-table');
        wrap.appendChild(mk('div', 'mby-table-toolbar', null, [mk('span', 'mby-table-title', 'Study Table (Click highlighted column to toggle recall mask)')]));
        const scroll = mk('div', 'mby-table-scroll mby-sticky-col');
        const tbl = mk('table');
        const thead = mk('thead'), thr = mk('tr');
        thr.appendChild(mk('th', null, 'Drug Class'));
        thr.appendChild(mk('th', null, 'Target'));
        const hideTh = mk('th', 'mby-th-hideable' + (previewTableHidden ? ' is-col-hidden' : ''), previewTableHidden ? 'Key Adverse Effect (Hidden — Click)' : 'Key Adverse Effect (Visible — Click)');
        hideTh.addEventListener('click', () => { previewTableHidden = !previewTableHidden; renderPreview(); });
        thr.appendChild(hideTh); thead.appendChild(thr); tbl.appendChild(thead);
        const tbody = mk('tbody');
        [['Loop Diuretic', 'NKCC2 (TAL)', 'Hypokalemia, ototoxicity'], ['Thiazide', 'NCC (DCT)', 'Hypercalcemia, hyponatremia']].forEach(([a, b, c]) => {
          const tr = mk('tr');
          tr.appendChild(mk('td', null, a));
          tr.appendChild(mk('td', null, b));
          const maskTd = mk('td', previewTableHidden ? 'mby-cell-masked' : 'mby-cell-masked peek');
          maskTd.appendChild(mk('span', 'mby-cell-inner', c));
          maskTd.addEventListener('click', () => maskTd.classList.toggle('peek'));
          tr.appendChild(maskTd); tbody.appendChild(tr);
        });
        tbl.appendChild(tbody); scroll.appendChild(tbl); wrap.appendChild(scroll);
        wrap.appendChild(mk('div', 'mby-table-caption', 'Caption: Native study tables support sticky first column and active-recall masking.'));
        wrap.appendChild(mk('div', 'mby-table-footnote', 'Footnote: Tables inherit the Article layout profile.'));
        stack.appendChild(wrap);
        card.appendChild(createUpperTab('ARTICLE · TABLE')); body.appendChild(stack);
      } else if (comp.id === 'image') {
        card.appendChild(createUpperTab(imageScope === 'all' ? 'IMAGE BLOCK (ALL PROFILES)' : ('IMAGE BLOCK (' + imageScope.toUpperCase() + ')')));
        body.appendChild(mk('div', 'mby-prompt', 'Diagram & Media Scaling Preview'));
        body.appendChild(mk('figure', 'mby-figure', null, [createDiagramNode(), mk('figcaption', 'mby-figcaption', 'Click diagram to test lightbox zoom; alignment follows contentAlign when tuned per profile.')]));
        body.appendChild(mk('div', 'ed-mcq-question-note', 'Constrained to max-height: ' + (vals.imageMaxHeightPx || '360') + 'px.'));
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
          if (previewMcqChecked) {
            if (opt.correct) btn.classList.add('correct');
            else if (previewMcqSelected.has(opt.idx)) btn.classList.add('wrong');
          }
          if (opt.hasImg) btn.appendChild(mk('div', 'mby-option-media', null, [createDiagramNode()]));
          const t = mk('div', 'mby-option-text'); t.innerHTML = opt.text; btn.appendChild(t);
          btn.addEventListener('click', () => { previewMcqSelected.clear(); previewMcqSelected.add(opt.idx); previewMcqChecked = false; renderPreview(); });
          grid.appendChild(btn);
        });
        const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
        checkBtn.addEventListener('click', () => { previewMcqChecked = !previewMcqChecked; renderPreview(); });
        const fb = mk('span'); if (previewMcqChecked) renderMcqFeedback(fb, previewMcqSelected.has(1));
        body.appendChild(grid); body.appendChild(mk('div', 'mby-action-row', null, [checkBtn, fb]));
      } else if (comp.id === 'fill-blank') {
        card.appendChild(createUpperTab('FILL IN THE BLANK'));
        const prompt = mk('div', 'mby-blank-prompt');
        prompt.append('The ');
        const wrap1 = mk('span', 'mby-blank-wrap', null, [Object.assign(mk('input', 'mby-blank-input correct'), { type: 'text', value: 'Application', readOnly: true })]);
        const wrap2 = mk('span', 'mby-blank-wrap', null, [Object.assign(mk('input', 'mby-blank-input'), { type: 'text', placeholder: 'layer...' })]);
        prompt.appendChild(wrap1);
        prompt.append(' layer defines repository ports implemented by ');
        prompt.appendChild(wrap2);
        prompt.append('.');
        body.appendChild(prompt);
        body.appendChild(mk('div', 'ed-mcq-question-note', 'Fill-blank questions inherit MCQ card width, padding, prompt font size, and alignment.'));
      } else if (comp.id === 'assertion-reason') {
        card.appendChild(createUpperTab('ASSERTION-REASON'));
        body.appendChild(mk('div', 'mby-ar-stack', null, [
          mk('div', 'mby-ar-box', null, [mk('div', 'mby-ar-label', 'Assertion (A)'), mk('div', 'mby-ar-text', null, [mk('p', null, 'Global layout keys themeMode and slideTransitionMs synchronize across all three profiles.')])]),
          mk('div', 'mby-ar-box', null, [mk('div', 'mby-ar-label', 'Reason (R)'), mk('div', 'mby-ar-text', null, [mk('p', null, 'Compiled study decks apply one unified theme and slide transition timing.')])])
        ]));
        const grid = mk('div', 'mby-options-grid');
        ['Both A and R are true, and R is the correct explanation of A', 'Both A and R are true, but R is not the correct explanation of A', 'A is true, but R is false', 'A is false, but R is true'].forEach((label, idx) => {
          const btn = mk('button', 'mby-option-btn' + (previewArSelected === idx + 1 ? ' selected' : ''), label);
          btn.type = 'button';
          btn.addEventListener('click', () => { previewArSelected = idx + 1; renderPreview(); });
          grid.appendChild(btn);
        });
        body.appendChild(grid);
      } else if (comp.id === 'spot-error') {
        card.appendChild(createUpperTab('SPOT THE ERROR'));
        const stmt = mk('div', 'mby-spot-statement');
        ['Domain layer ', 'may reference SQLite', ' directly while ', 'Infrastructure implements ports', '.'].forEach((part, idx) => {
          if (idx === 1 || idx === 3) {
            const chipIdx = idx === 1 ? 0 : 1;
            const chip = mk('button', 'mby-error-chip' + (previewSpotIdx === chipIdx && !previewSpotNoError ? ' selected' : ''), part);
            chip.type = 'button';
            chip.addEventListener('click', () => { previewSpotIdx = chipIdx; previewSpotNoError = false; renderPreview(); });
            stmt.appendChild(mk('span', 'mby-error-chip-wrap', null, [chip]));
          } else {
            stmt.append(part);
          }
        });
        body.appendChild(stmt);
        const noErrBtn = mk('button', 'mby-no-error-btn' + (previewSpotNoError ? ' selected' : ''), 'No Error in Statement');
        noErrBtn.type = 'button';
        noErrBtn.addEventListener('click', () => { previewSpotNoError = !previewSpotNoError; renderPreview(); });
        body.appendChild(mk('div', 'mby-action-row', null, [noErrBtn]));
      } else if (comp.id === 'diff') {
        card.appendChild(createUpperTab('DIFFERENTIATION'));
        const wrap = mk('div', 'mby-diff-wrap');
        wrap.appendChild(mk('div', 'mby-diff-title', 'Compare Clean CLI Layers (MCQ Profile)'));
        const scroll = mk('div', 'mby-diff-scroll');
        const tbl = mk('table', 'mby-diff-table');
        tbl.innerHTML = '<thead><tr><th>Layer</th><th>Allowed Dependencies</th><th>Examples</th></tr></thead>' +
          '<tbody><tr><td>Domain</td><td><button type="button" class="mby-diff-cell-btn selected">None (Pure C#)</button></td><td>LayoutBoundsCatalog</td></tr>' +
          '<tr><td>Application</td><td><button type="button" class="mby-diff-cell-btn">Domain only</button></td><td>SaveLayoutProfilesHandler</td></tr></tbody>';
        scroll.appendChild(tbl); wrap.appendChild(scroll);
        wrap.appendChild(mk('div', 'mby-diff-bank', null, [mk('button', 'mby-diff-bank-chip selected', 'None (Pure C#)'), mk('button', 'mby-diff-bank-chip', 'Infrastructure')]));
        body.appendChild(wrap);
      } else if (comp.id === 'mechanism') {
        card.appendChild(createUpperTab('MECHANISM'));
        const layout = mk('div', 'mby-mech-layout');
        layout.appendChild(mk('div', 'mby-mech-title', 'Order the Layout Save Pipeline'));
        const seq = mk('div', 'mby-mech-seq');
        previewMechOrder.forEach((stepText, idx) => {
          const upBtn = mk('button', 'mby-mech-ctrl-btn', '↑'); upBtn.type = 'button'; upBtn.disabled = idx === 0;
          upBtn.addEventListener('click', () => { [previewMechOrder[idx - 1], previewMechOrder[idx]] = [previewMechOrder[idx], previewMechOrder[idx - 1]]; renderPreview(); });
          const dnBtn = mk('button', 'mby-mech-ctrl-btn', '↓'); dnBtn.type = 'button'; dnBtn.disabled = idx === previewMechOrder.length - 1;
          dnBtn.addEventListener('click', () => { [previewMechOrder[idx + 1], previewMechOrder[idx]] = [previewMechOrder[idx], previewMechOrder[idx + 1]]; renderPreview(); });
          seq.appendChild(mk('div', 'mby-mech-slot', null, [
            mk('span', 'mby-mech-badge', String(idx + 1)),
            mk('span', 'mby-mech-slot-text', stepText),
            mk('div', 'mby-mech-slot-actions', null, [upBtn, dnBtn])
          ]));
        });
        layout.appendChild(seq);
        body.appendChild(layout);
      } else if (comp.id === 'relabel') {
        card.appendChild(createUpperTab('RELABEL'));
        const layout = mk('div', 'mby-relabel-layout');
        layout.appendChild(mk('div', 'mby-relabel-prompt', 'Click an occlusion box below to toggle reveal state:'));
        const stageWrap = mk('div', 'mby-relabel-stage-wrap');
        const stage = mk('div', 'mby-relabel-stage');
        const img = Object.assign(mk('img', 'mby-relabel-img'), { src: SAMPLE_DIAGRAM_SVG, alt: 'Relabel preview diagram' });
        stage.appendChild(img);
        [
          { idx: 1, label: 'Domain', left: '6%', top: '26%', width: '24%', height: '48%' },
          { idx: 2, label: 'WebApp', left: '72%', top: '26%', width: '22%', height: '48%' }
        ].forEach(b => {
          const rev = previewRelabelRevealed.has(b.idx);
          const box = mk('button', 'mby-mask-box mby-mask-solid' + (rev ? ' revealed' : ''), null, [
            mk('span', 'mby-mask-badge', rev ? b.label : String(b.idx))
          ]);
          box.type = 'button';
          Object.assign(box.style, { left: b.left, top: b.top, width: b.width, height: b.height });
          box.addEventListener('click', () => { if (previewRelabelRevealed.has(b.idx)) previewRelabelRevealed.delete(b.idx); else previewRelabelRevealed.add(b.idx); renderPreview(); });
          stage.appendChild(box);
        });
        stageWrap.appendChild(stage); layout.appendChild(stageWrap);
        body.appendChild(layout);
      }
      card.appendChild(body); previewStageEl.appendChild(card);
    }

    document.querySelectorAll('#themeSeg button').forEach(btn => btn.addEventListener('click', () => {
      ['article', 'mcq', 'flashcard'].forEach(p => { valuesByProfile[p].themeMode = btn.dataset.themeVal; });
      syncDirtyState(); syncGlobalControlsUi();
    }));
    function applyGlobalSlideInput(raw) {
      const p = parseInt(raw, 10);
      if (Number.isNaN(p)) { globalSlideNum.value = globalSlideRange.value; return; }
      const norm = String(Math.max(100, Math.min(1200, p)));
      ['article', 'mcq', 'flashcard'].forEach(prof => { valuesByProfile[prof].slideTransitionMs = norm; });
      syncDirtyState(); syncGlobalControlsUi();
    }
    globalSlideRange.addEventListener('input', () => applyGlobalSlideInput(globalSlideRange.value));
    globalSlideNum.addEventListener('change', () => applyGlobalSlideInput(globalSlideNum.value));
    globalSlideNum.addEventListener('blur', () => { if (Number.isNaN(parseInt(globalSlideNum.value, 10))) globalSlideNum.value = globalSlideRange.value; });
    $('testSlideBtn').addEventListener('click', () => {
      if (testSlideTimer !== null) { clearTimeout(testSlideTimer); testSlideTimer = null; }
      previewStageEl.classList.remove('is-sliding');
      void previewStageEl.offsetWidth;
      previewStageEl.classList.add('is-sliding');
      const slideMs = Math.max(100, parseInt(globalSlideRange.value, 10) || 380);
      testSlideTimer = setTimeout(() => { previewStageEl.classList.remove('is-sliding'); testSlideTimer = null; }, slideMs);
    });
    saveAllBtn.addEventListener('click', async () => {
      if (isBusy) return;
      setBusy(true); setStatus('Saving...', '');
      try {
        const res = await fetch('/api/layout', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'X-Mby-Editor-Token': EDITOR_CSRF_TOKEN },
          body: JSON.stringify({ profiles: valuesByProfile })
        });
        const data = await res.json().catch(() => ({}));
        if (res.ok && data.saved) {
          ['article', 'mcq', 'flashcard'].forEach(p => { savedValuesByProfile[p] = { ...valuesByProfile[p] }; });
          syncDirtyState(); setStatus('All component layouts saved.', 'ok');
        } else {
          setStatus('Save error: ' + ((data.errors && data.errors.join(', ')) || ('HTTP ' + res.status)), 'err');
        }
      } catch (err) {
        setStatus('Save failed: ' + (err?.message || String(err)), 'err');
      } finally {
        setBusy(false);
      }
    });
    resetCompBtn.addEventListener('click', async () => {
      if (isBusy) return;
      const comp = getActiveComponent();
      const targets = resolveTargetProfiles(comp);
      const keys = getVisibleFields(comp).map(k => k.key);
      if (keys.length === 0) return;
      setBusy(true); setStatus('Resetting component defaults...', '');
      try {
        const errs = [];
        for (const t of targets) {
          const res = await fetch('/api/layout/reset', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'X-Mby-Editor-Token': EDITOR_CSRF_TOKEN },
            body: JSON.stringify({ targetType: t, keys })
          });
          const data = await res.json().catch(() => ({}));
          if (!res.ok || !data.reset) {
            errs.push((data.errors && data.errors.join(', ')) || (t + ': HTTP ' + res.status));
            continue;
          }
          keys.forEach(k => {
            const def = specsByProfile[t]?.[k]?.defaultValue;
            if (def !== undefined) {
              valuesByProfile[t][k] = def;
              savedValuesByProfile[t][k] = def;
            }
          });
        }
        syncGlobalControlsUi(); renderInspector(); renderPreview(); syncDirtyState();
        if (errs.length === 0) setStatus('Reset ' + comp.label + ' to defaults.', 'ok');
        else setStatus('Reset error: ' + errs.join('; '), 'err');
      } catch (err) {
        setStatus('Reset failed: ' + (err?.message || String(err)), 'err');
      } finally {
        setBusy(false);
      }
    });
    async function loadAllLayouts() {
      const res = await fetch('/api/layout');
      if (!res.ok) throw new Error('HTTP ' + res.status);
      const data = await res.json();
      ['article', 'mcq', 'flashcard'].forEach(k => {
        specsByProfile[k] = {}; valuesByProfile[k] = {}; savedValuesByProfile[k] = {};
        (data.profiles?.[k] || []).forEach(p => {
          specsByProfile[k][p.property] = p;
          valuesByProfile[k][p.property] = p.currentValue;
          savedValuesByProfile[k][p.property] = p.currentValue;
        });
      });
      syncGlobalControlsUi(); syncDirtyState(); renderTabs(); renderInspector(); renderPreview();
    }
    loadAllLayouts().catch(err => setStatus('Failed to load layouts: ' + err.message, 'err'));
  </script>
</body>
</html>
""";
    }
}
