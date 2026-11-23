namespace Meloberry.Infrastructure.Web;

/// <summary>
/// Canonical CSS tokens, component styles, and shared DOM/flashcard runtime JS shared identically
/// between <see cref="SlidingWebAppGenerator"/> and <see cref="LayoutEditorPageBuilder"/>.
/// </summary>
internal static class WebAppSharedStyles
{
    public static string BuildCoreCss() => """
:root { --bg: #fdf6f9; --surface: #ffffff; --text: #1f1722; --muted: #6c5b70; --accent: #b8295b; --accent-soft: #fce8ef; --border: #ecd7e0; --ok: #198754; --bad: #dc3545; --gray-label: #9e9e9e; --slide-ms: 380ms; --callout-pearl: #b45309; --callout-pearl-bg: #fffbeb; --callout-clinical: #0369a1; --callout-clinical-bg: #f0f9ff; --callout-warning: #dc2626; --callout-warning-bg: #fef2f2; }
html[data-theme="light"] { --bg: #f6f8fa; --accent: #2563eb; --accent-soft: #dbeafe; --border: #e2e8f0; --text: #0f172a; --muted: #475569; }
html[data-theme="dark"] { --bg: #0f1117; --surface: #181b24; --text: #f1f5f9; --muted: #94a3b8; --accent: #f43f5e; --accent-soft: #311520; --border: #2a2f3d; --callout-pearl: #fbbf24; --callout-pearl-bg: rgba(245, 158, 11, 0.12); --callout-clinical: #38bdf8; --callout-clinical-bg: rgba(14, 165, 233, 0.12); --callout-warning: #f87171; --callout-warning-bg: rgba(239, 68, 68, 0.13); }
html[data-theme="paper"] { --bg: #f7f3e9; --surface: #fffdf8; --text: #2c241b; --muted: #6f6254; --accent: #9a3412; --accent-soft: #ffedd5; --border: #e7dcc8; --callout-pearl-bg: #fef7e0; --callout-clinical-bg: #eef7fb; --callout-warning-bg: #fdf0ed; }
* { box-sizing: border-box; }
html, body { margin: 0; padding: 0; font-family: 'Segoe UI', system-ui, -apple-system, sans-serif; background: var(--bg); color: var(--text); }
.hidden { display: none !important; }
.mby-greeting-card, .mby-card, .mby-flashcard-face { background: var(--surface); border: 1px solid var(--border); border-radius: 18px; box-shadow: none; overflow: hidden; }
.mby-card { width: 100%; max-width: var(--card-max-width, 840px); text-align: var(--content-align, left); font-size: var(--body-font-size, 16px); }
.mby-type-tab { display: inline-block; align-self: flex-start; width: fit-content; margin-left: var(--card-padding, 32px); padding: 5px 14px; background: var(--accent-soft); color: var(--accent); font-size: 11px; font-weight: 800; letter-spacing: 0.04em; text-transform: uppercase; border: 1px solid var(--border); border-top: none; border-radius: 0 0 10px 10px; }
.mby-card-body { padding: 16px var(--card-padding, 32px) var(--card-padding, 32px); }
.mby-prompt { font-size: var(--prompt-font-size, 22px); font-weight: 650; margin-bottom: 18px; line-height: 1.45; }
.mby-prompt p:first-child { margin-top: 0; }
.mby-article-blocks { display: flex; flex-direction: column; gap: var(--block-spacing, 20px); }
.mby-article-blocks h1 { margin: 0.35em 0 0.25em; font-size: calc(var(--prompt-font-size, 22px) * 1.25); line-height: 1.25; font-weight: 800; }
.mby-article-blocks h2 { margin: 0.35em 0 0.2em; font-size: var(--prompt-font-size, 22px); line-height: 1.3; font-weight: 750; }
.mby-article-blocks h3 { margin: 0.3em 0 0.15em; font-size: calc(var(--prompt-font-size, 22px) * 0.88); line-height: 1.35; font-weight: 700; }
.mby-article-blocks h4 { margin: 0.25em 0 0.15em; font-size: calc(var(--prompt-font-size, 22px) * 0.8); line-height: 1.4; font-weight: 700; }
.mby-article-blocks table { width: 100%; border-collapse: collapse; margin: 12px 0; font-size: 0.94em; border: 1px solid var(--border); border-radius: 10px; overflow: hidden; display: block; overflow-x: auto; }
.mby-article-blocks thead { background: var(--accent-soft); color: var(--accent); }
.mby-article-blocks th, .mby-article-blocks td { padding: 10px 14px; border: 1px solid var(--border); text-align: left; }
.mby-article-blocks tbody tr:nth-child(even) { background: var(--bg); }
.mby-article-blocks blockquote { margin: 12px 0; padding: 10px 16px; border-left: 4px solid var(--accent); background: var(--accent-soft); border-radius: 0 10px 10px 0; color: var(--text); }
.mby-article-blocks blockquote p { margin: 0.25em 0; }
.mby-article-blocks hr { border: none; border-top: 1px solid var(--border); margin: 16px 0; }
.mby-article-blocks ul.contains-task-list { list-style: none; padding-left: 4px; }
.mby-article-blocks .task-list-item { display: flex; align-items: baseline; gap: 8px; margin: 4px 0; }
.mby-prompt-media { margin-bottom: 16px; }
.mby-explanation-box { margin-top: 16px; padding: 14px 16px; border-radius: 12px; border: 1px solid var(--border); border-left: 4px solid var(--accent); background: var(--bg); font-size: 0.95em; line-height: 1.5; }
.mby-explanation-title { font-size: 11px; font-weight: 800; letter-spacing: 0.04em; text-transform: uppercase; color: var(--accent); margin-bottom: 6px; }
.mby-quiz-section-title { font-size: 15px; font-weight: 750; color: var(--text); text-align: center; }
.math { overflow-x: auto; max-width: 100%; }
code { font-family: 'Cascadia Code', 'Fira Code', ui-monospace, SFMono-Regular, Consolas, monospace; background: var(--accent-soft); color: var(--accent); border: 1px solid var(--border); border-radius: 6px; padding: 2px 7px; font-size: 0.9em; font-weight: 600; }
pre { background: var(--accent-soft); border: 1px solid var(--border); border-left: 4px solid var(--accent); border-radius: 12px; padding: 14px 18px; overflow-x: auto; margin: 12px 0; font-family: 'Cascadia Code', 'Fira Code', ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 0.92em; line-height: 1.55; text-align: left; }
pre code { background: transparent; color: var(--text); border: none; padding: 0; font-size: inherit; font-weight: 500; }
.mby-media-wrap { position: relative; display: inline-flex; justify-content: center; width: 100%; border-radius: 12px; overflow: hidden; cursor: zoom-in; background: rgba(0, 0, 0, 0.02); }
.mby-media-img { max-width: 100%; max-height: var(--image-max-height, 360px); border-radius: 12px; object-fit: contain; display: block; }
.mby-zoom-hint { position: absolute; right: 10px; bottom: 10px; width: 30px; height: 30px; border-radius: 50%; background: rgba(0, 0, 0, 0.62); color: #ffffff; display: flex; align-items: center; justify-content: center; pointer-events: none; }
.mby-zoom-icon { width: 16px; height: 16px; display: block; stroke: #ffffff; }
.mby-figure { margin: 4px 0; padding: 10px; border: 1px solid var(--border); border-radius: 14px; background: var(--bg); display: flex; flex-direction: column; align-items: center; gap: 10px; break-inside: avoid; page-break-inside: avoid; }
.mby-figcaption { width: 100%; font-size: 0.92em; line-height: 1.5; color: var(--muted); text-align: center; padding: 0 6px 2px; }
.mby-figcaption p { margin: 0.15em 0; }
.mby-callout { margin: 4px 0; padding: 14px 18px; border-radius: 12px; border: 1px solid var(--border); border-left: 4px solid var(--callout-pearl); background: var(--callout-pearl-bg); break-inside: avoid; page-break-inside: avoid; }
.mby-callout-pearl { border-left-color: var(--callout-pearl); background: var(--callout-pearl-bg); }
.mby-callout-clinical { border-left-color: var(--callout-clinical); background: var(--callout-clinical-bg); }
.mby-callout-warning { border-left-color: var(--callout-warning); background: var(--callout-warning-bg); }
.mby-callout-header { display: flex; align-items: center; gap: 8px; margin-bottom: 6px; font-size: 12px; font-weight: 800; letter-spacing: 0.04em; text-transform: uppercase; color: var(--callout-pearl); }
.mby-callout-clinical .mby-callout-header { color: var(--callout-clinical); }
.mby-callout-warning .mby-callout-header { color: var(--callout-warning); }
.mby-callout-body { font-size: 0.96em; line-height: 1.55; color: var(--text); }
.mby-callout-body p:first-child { margin-top: 0; }
.mby-callout-body p:last-child { margin-bottom: 0; }
.mby-options-grid { display: grid; grid-template-columns: repeat(var(--option-columns, 1), minmax(0, 1fr)); gap: var(--option-gap, 12px); }
.mby-option-btn { display: flex; flex-direction: column; gap: 10px; width: 100%; text-align: inherit; padding: 14px 16px; border-radius: 12px; border: 2px solid var(--border); background: transparent; color: var(--text); cursor: pointer; font: inherit; transition: border-color 140ms ease, background 140ms ease; }
.mby-option-btn.selected { border-color: var(--accent); background: var(--accent-soft); }
.mby-option-btn.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.12); }
.mby-option-btn.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.12); }
.mby-action-row { margin-top: 20px; display: flex; gap: 12px; align-items: center; }
.mby-check-btn, .mby-nav-btn { display: inline-flex; align-items: center; justify-content: center; gap: 6px; padding: 10px 18px; border-radius: 10px; border: 1px solid var(--border); background: var(--surface); color: var(--text); font-weight: 600; cursor: pointer; user-select: none; }
.mby-nav-btn.primary, .mby-check-btn { background: var(--accent); color: #ffffff; border-color: var(--accent); }
.mby-nav-btn:disabled { opacity: 0.45; cursor: not-allowed; }
.mby-save-icon { width: 16px; height: 16px; fill: currentColor; }
.mby-flashcard-scene { width: 100%; max-width: var(--card-max-width, 840px); text-align: var(--content-align, left); font-size: var(--body-font-size, 16px); cursor: pointer; }
.mby-flashcard-scene.is-flipping { perspective: 1200px; }
.mby-flashcard-inner { display: grid; grid-template-columns: minmax(0, 1fr); width: 100%; }
.mby-flashcard-scene.is-flipping .mby-flashcard-inner { height: 100%; transform-style: preserve-3d; }
.mby-flashcard-face { grid-area: 1 / 1; display: none; flex-direction: column; min-height: 270px; }
.mby-flashcard-face.is-active { display: flex; }
.mby-flashcard-scene.is-flipping .mby-flashcard-face { display: flex; height: 100%; backface-visibility: hidden; }
.mby-flashcard-scene.is-flipping .mby-flashcard-body { min-height: 0; overflow: hidden; }
.mby-flashcard-scene.is-flipping .mby-flashcard-back { transform: rotateY(180deg); }
.mby-flashcard-body { flex: 1; padding: 16px var(--card-padding, 32px) 16px; display: flex; flex-direction: column; gap: 14px; }
.mby-rating-row { display: flex; gap: 12px; margin-top: 8px; justify-content: center; }
.mby-rate-btn { padding: 9px 18px; border-radius: 8px; border: 1.5px solid var(--border); background: transparent; color: var(--text); font-weight: 600; cursor: pointer; }
.mby-rate-btn.active-known { border-color: var(--ok); background: rgba(25, 135, 84, 0.14); color: var(--ok); }
.mby-rate-btn.active-review { border-color: var(--bad); background: rgba(220, 53, 69, 0.14); color: var(--bad); }
.mby-flashcard-side-label { text-align: center; padding: 6px 16px 16px; font-size: 12px; font-weight: 600; color: var(--gray-label); }
.mby-generator-footer { display: flex; align-items: center; justify-content: space-between; gap: 10px; flex-wrap: wrap; padding: 11px 20px; border-top: 1px solid var(--border); background: var(--bg); color: var(--muted); font-size: 12px; line-height: 1.4; }
.mby-generator-badge { display: inline-flex; align-items: center; gap: 6px; font-weight: 700; color: var(--accent); }
.mby-generator-meta { font-family: ui-monospace, Consolas, monospace; font-size: 11px; }
""";

    public static string BuildCoreJs() => """
const MAGNIFIER_ICON_SVG = '<svg class="mby-zoom-icon" viewBox="0 0 24 24" fill="none" stroke="#ffffff" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="11" cy="11" r="7"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></svg>';
function mk(tag, cls, text, children) {
  const n = document.createElement(tag);
  if (cls) n.className = cls;
  if (text !== undefined && text !== null) n.textContent = text;
  if (children) children.forEach(c => { if (c) n.appendChild(c); });
  return n;
}
function escapeHtml(value) {
  return String(value ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}
function applySlideLayoutVars(el, l) {
  if (!l) return;
  const map = { cardMaxWidthPx: ['--card-max-width', 'px'], cardPaddingPx: ['--card-padding', 'px'], promptFontSizePx: ['--prompt-font-size', 'px'], bodyFontSizePx: ['--body-font-size', 'px'], imageMaxHeightPx: ['--image-max-height', 'px'], contentAlign: ['--content-align', ''], optionColumns: ['--option-columns', ''], optionGapPx: ['--option-gap', 'px'], blockSpacingPx: ['--block-spacing', 'px'] };
  Object.entries(map).forEach(([k, [cssVar, unit]]) => { if (l[k]) el.style.setProperty(cssVar, l[k] + unit); });
}
function createUpperTab(label) { return mk('div', 'mby-type-tab', label); }
function createZoomableImageNode(src, onZoom, onLoad, alt) {
  const wrap = mk('div', 'mby-media-wrap');
  const img = mk('img', 'mby-media-img');
  img.src = src;
  if (alt) img.alt = alt;
  if (onLoad) img.addEventListener('load', onLoad);
  const hint = mk('span', 'mby-zoom-hint');
  hint.innerHTML = MAGNIFIER_ICON_SVG;
  hint.setAttribute('aria-hidden', 'true');
  wrap.appendChild(img); wrap.appendChild(hint);
  if (onZoom) wrap.addEventListener('click', e => { e.stopPropagation(); onZoom(src, alt); });
  return wrap;
}
function renderMcqFeedback(feedbackEl, isCorrect) {
  feedbackEl.textContent = isCorrect ? 'Correct!' : 'Review highlighted options.';
  feedbackEl.style.color = isCorrect ? 'var(--ok)' : 'var(--bad)';
  feedbackEl.style.fontWeight = '600';
}
function attachFlashcardFlip(scene, inner, frontFace, backFace, flipMs, getFlipped, setFlipped, onFlipStart, onFlipEnd) {
  let flipTimer = null, flipToken = 0;
  function measureFaceNaturalHeight(faceEl) {
    if (getComputedStyle(faceEl).display !== 'none' && !scene.classList.contains('is-flipping')) return Math.ceil(faceEl.offsetHeight);
    const prev = [faceEl.style.display, faceEl.style.position, faceEl.style.left, faceEl.style.top, faceEl.style.height, faceEl.style.width, faceEl.style.visibility];
    Object.assign(faceEl.style, { display: 'flex', position: 'absolute', left: '-9999px', top: '0', height: 'auto', width: (scene.clientWidth || scene.offsetWidth || 840) + 'px', visibility: 'hidden' });
    const h = Math.ceil(faceEl.offsetHeight);
    [faceEl.style.display, faceEl.style.position, faceEl.style.left, faceEl.style.top, faceEl.style.height, faceEl.style.width, faceEl.style.visibility] = prev;
    return h;
  }
  function settleFlip(token) {
    if (token !== flipToken) return;
    if (flipTimer !== null) { clearTimeout(flipTimer); flipTimer = null; }
    scene.classList.remove('is-flipping');
    scene.style.transition = ''; scene.style.height = '';
    inner.style.transition = ''; inner.style.transform = '';
    frontFace.classList.toggle('is-active', !getFlipped());
    backFace.classList.toggle('is-active', getFlipped());
    if (onFlipEnd) onFlipEnd();
  }
  inner.addEventListener('transitionend', e => { if (e.target === inner && e.propertyName === 'transform') settleFlip(flipToken); });
  function triggerFlip() {
    flipToken += 1;
    const myToken = flipToken;
    if (flipTimer !== null) clearTimeout(flipTimer);
    const was = getFlipped(), fromFace = was ? backFace : frontFace, toFace = was ? frontFace : backFace;
    const fromH = scene.classList.contains('is-flipping') ? (Math.ceil(scene.getBoundingClientRect().height) || measureFaceNaturalHeight(fromFace)) : measureFaceNaturalHeight(fromFace);
    const toH = measureFaceNaturalHeight(toFace);
    scene.style.transition = 'none'; scene.style.height = fromH + 'px';
    inner.style.transition = 'none';
    if (!scene.classList.contains('is-flipping')) { inner.style.transform = was ? 'rotateY(180deg)' : 'rotateY(0deg)'; scene.classList.add('is-flipping'); }
    else inner.style.transform = getComputedStyle(inner).transform;
    void inner.offsetWidth;
    setFlipped(!was);
    const ease = 'cubic-bezier(0.4, 0, 0.2, 1)';
    scene.style.transition = 'height ' + flipMs + 'ms ' + ease;
    inner.style.transition = 'transform ' + flipMs + 'ms ' + ease;
    scene.style.height = toH + 'px';
    inner.style.transform = getFlipped() ? 'rotateY(180deg)' : 'rotateY(0deg)';
    if (onFlipStart) onFlipStart(toH, flipMs);
    flipTimer = setTimeout(() => settleFlip(myToken), flipMs + 60);
  }
  return triggerFlip;
}
""";
}
