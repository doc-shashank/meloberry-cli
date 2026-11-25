using Meloberry.Infrastructure.Web.Components;
using Meloberry.Infrastructure.Web.Components.AuxiliarySpecs;
using Meloberry.Infrastructure.Web.Components.ContentSpecs;

namespace Meloberry.Infrastructure.Web;

/// <summary>
/// Canonical CSS tokens, base component styles, and shared DOM/flashcard runtime JS shared identically
/// between <see cref="SlidingWebAppGenerator"/> and <see cref="LayoutEditorPageBuilder"/>.
/// </summary>
internal static class WebAppSharedStyles
{
    private const string BerryVars =
        ":root { --bg: #fdf6f9; --surface: #ffffff; --text: #1f1722; --muted: #6c5b70; --accent: #b8295b; --accent-soft: #fce8ef; --select: #2563eb; --select-soft: #e0edff; --table-hideable-bg: #eef2ff; --table-hideable-color: #3730a3; --border: #ecd7e0; --ok: #198754; --bad: #dc3545; --gray-label: #9e9e9e; --slide-ms: 380ms; --callout-pearl: #b45309; --callout-pearl-bg: #fffbeb; --callout-clinical: #0369a1; --callout-clinical-bg: #f0f9ff; --callout-warning: #dc2626; --callout-warning-bg: #fef2f2; }";

    private const string LightOverrides =
        "html[data-theme=\"light\"] { --bg: #f6f8fa; --accent: #2563eb; --accent-soft: #dbeafe; --select: #7c3aed; --select-soft: #ede9fe; --table-hideable-bg: #ecfdf5; --table-hideable-color: #065f46; --border: #e2e8f0; --text: #0f172a; --muted: #475569; }";

    private const string DarkOverrides =
        "html[data-theme=\"dark\"] { --bg: #0f1117; --surface: #181b24; --text: #f1f5f9; --muted: #94a3b8; --accent: #f43f5e; --accent-soft: #311520; --select: #38bdf8; --select-soft: rgba(56, 189, 248, 0.16); --table-hideable-bg: rgba(99, 102, 241, 0.22); --table-hideable-color: #c7d2fe; --border: #2a2f3d; --callout-pearl: #fbbf24; --callout-pearl-bg: rgba(245, 158, 11, 0.12); --callout-clinical: #38bdf8; --callout-clinical-bg: rgba(14, 165, 233, 0.12); --callout-warning: #f87171; --callout-warning-bg: rgba(239, 68, 68, 0.13); }";

    private const string PaperOverrides =
        "html[data-theme=\"paper\"] { --bg: #f7f3e9; --surface: #fffdf8; --text: #2c241b; --muted: #6f6254; --accent: #9a3412; --accent-soft: #ffedd5; --select: #1d4ed8; --select-soft: #e0e7ff; --table-hideable-bg: #eef2e6; --table-hideable-color: #3f6212; --border: #e7dcc8; --callout-pearl-bg: #fef7e0; --callout-clinical-bg: #eef7fb; --callout-warning-bg: #fdf0ed; }";

    public static string BuildThemeCss(string? activeTheme) => activeTheme?.Trim().ToLowerInvariant() switch
    {
        "berry" => BerryVars,
        "light" => $"{BerryVars}\n{LightOverrides}",
        "dark" => $"{BerryVars}\n{DarkOverrides}",
        "paper" => $"{BerryVars}\n{PaperOverrides}",
        _ => $"{BerryVars}\n{LightOverrides}\n{DarkOverrides}\n{PaperOverrides}"
    };

    public static string BuildBaseCss() => """
* { box-sizing: border-box; }
html, body { margin: 0; padding: 0; font-family: 'Segoe UI', system-ui, -apple-system, sans-serif; background: var(--bg); color: var(--text); }
.hidden { display: none !important; }
.mby-greeting-card, .mby-card, .mby-flashcard-face { background: var(--surface); border: 1px solid var(--border); border-radius: 18px; box-shadow: none; overflow: hidden; }
.mby-card { width: 100%; max-width: var(--card-max-width, 840px); display: flex; flex-direction: column; text-align: var(--content-align, left); font-size: var(--body-font-size, 16px); }
.mby-type-tab { display: inline-block; align-self: flex-start; width: fit-content; margin-left: var(--card-padding, 32px); padding: 5px 14px; background: var(--accent-soft); color: var(--accent); font-size: 11px; font-weight: 800; letter-spacing: 0.04em; text-transform: uppercase; border: 1px solid var(--border); border-top: none; border-radius: 0 0 10px 10px; }
.mby-card-body { padding: 16px var(--card-padding, 32px) var(--card-padding, 32px); }
.mby-prompt { font-size: var(--prompt-font-size, 22px); font-weight: 650; margin-bottom: 18px; line-height: 1.45; }
.mby-prompt p:first-child { margin-top: 0; }
.math { overflow-x: auto; max-width: 100%; }
code { font-family: 'Cascadia Code', 'Fira Code', ui-monospace, SFMono-Regular, Consolas, monospace; background: var(--accent-soft); color: var(--accent); border: 1px solid var(--border); border-radius: 6px; padding: 2px 7px; font-size: 0.9em; font-weight: 600; }
pre { background: var(--accent-soft); border: 1px solid var(--border); border-left: 4px solid var(--accent); border-radius: 12px; padding: 14px 18px; overflow-x: auto; margin: 12px 0; font-family: 'Cascadia Code', 'Fira Code', ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 0.92em; line-height: 1.55; text-align: left; }
pre code { background: transparent; color: var(--text); border: none; padding: 0; font-size: inherit; font-weight: 500; }
.mby-nav-btn { display: inline-flex; align-items: center; justify-content: center; gap: 6px; padding: 10px 18px; border-radius: 10px; border: 1px solid var(--border); background: var(--surface); color: var(--text); font-weight: 600; cursor: pointer; user-select: none; }
.mby-nav-btn.primary { background: var(--accent); color: #ffffff; border-color: var(--accent); }
.mby-nav-btn:disabled { opacity: 0.45; cursor: not-allowed; }
.mby-retry-btn { display: inline-flex; align-items: center; justify-content: center; gap: 5px; padding: 10px 14px; border-radius: 10px; border: 1px solid var(--border); background: var(--bg); color: var(--text); font: inherit; font-size: 14px; font-weight: 600; cursor: pointer; user-select: none; transition: border-color 140ms ease, color 140ms ease, background 140ms ease; }
.mby-retry-btn:hover { border-color: var(--select); color: var(--select); background: var(--select-soft); }
.mby-retry-btn.is-compact { padding: 7px 14px; font-size: 13px; border-radius: 8px; }
.mby-step-counter { font-size: 11px; font-weight: 800; color: var(--muted); font-family: ui-monospace, Consolas, monospace; }
.mby-save-icon { width: 16px; height: 16px; fill: currentColor; }
.mby-generator-footer { display: flex; align-items: center; justify-content: space-between; gap: 10px; flex-wrap: wrap; padding: 11px 20px; border-top: 1px solid var(--border); background: var(--bg); color: var(--muted); font-size: 12px; line-height: 1.4; }
.mby-generator-link { display: inline-flex; align-items: center; gap: 6px; font-weight: 700; color: var(--accent); text-decoration: none; }
.mby-generator-link:hover { text-decoration: underline; }
.mby-github-icon { width: 15px; height: 15px; fill: currentColor; flex-shrink: 0; }
.mby-generator-meta { font-family: ui-monospace, Consolas, monospace; font-size: 11px; }
""";

    public static string BuildEditorCss() =>
        $"{BuildThemeCss(null)}\n{BuildBaseCss()}\n{WebComponentCatalog.BuildCombinedCss(WebAppFeatureSet.All)}";

    public static string BuildBaseCoreJs() => """
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
  if (l.contentAlign) {
    const alignMap = { left: 'flex-start', center: 'center', right: 'flex-end' };
    el.style.setProperty('--media-align', alignMap[l.contentAlign] || 'flex-start');
  }
}
function resolveSlideLayout(slide, baseProfileKey) {
  const key = baseProfileKey || (slide.kind === 'article' ? 'article' : (slide.kind === 'flashcard' ? 'flashcard' : 'mcq'));
  return { ...(typeof DATA !== 'undefined' && DATA.profiles?.[key] || {}), ...(slide.layoutOverrides || {}) };
}
function createUpperTab(label) { return mk('div', 'mby-type-tab', label); }
""";

    public static string BuildEditorCoreJs() =>
        $"{BuildBaseCoreJs()}\n{AuxiliarySpecsCatalog.EditorMediaZoomJs}\n{AuxiliarySpecsCatalog.EditorGradedFeedbackJs}\n{QuestionSpecsSet1.FlashcardFlipJs}";
}
