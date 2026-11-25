namespace Meloberry.Infrastructure.Web;

/// <summary>
/// Canonical CSS tokens, component styles, and shared DOM/flashcard runtime JS shared identically
/// between <see cref="SlidingWebAppGenerator"/> and <see cref="LayoutEditorPageBuilder"/>.
/// </summary>
internal static class WebAppSharedStyles
{
    public static string BuildCoreCss() => """
:root { --bg: #fdf6f9; --surface: #ffffff; --text: #1f1722; --muted: #6c5b70; --accent: #b8295b; --accent-soft: #fce8ef; --select: #2563eb; --select-soft: #e0edff; --table-hideable-bg: #eef2ff; --table-hideable-color: #3730a3; --border: #ecd7e0; --ok: #198754; --bad: #dc3545; --gray-label: #9e9e9e; --slide-ms: 380ms; --callout-pearl: #b45309; --callout-pearl-bg: #fffbeb; --callout-clinical: #0369a1; --callout-clinical-bg: #f0f9ff; --callout-warning: #dc2626; --callout-warning-bg: #fef2f2; }
html[data-theme="light"] { --bg: #f6f8fa; --accent: #2563eb; --accent-soft: #dbeafe; --select: #7c3aed; --select-soft: #ede9fe; --table-hideable-bg: #ecfdf5; --table-hideable-color: #065f46; --border: #e2e8f0; --text: #0f172a; --muted: #475569; }
html[data-theme="dark"] { --bg: #0f1117; --surface: #181b24; --text: #f1f5f9; --muted: #94a3b8; --accent: #f43f5e; --accent-soft: #311520; --select: #38bdf8; --select-soft: rgba(56, 189, 248, 0.16); --table-hideable-bg: rgba(99, 102, 241, 0.22); --table-hideable-color: #c7d2fe; --border: #2a2f3d; --callout-pearl: #fbbf24; --callout-pearl-bg: rgba(245, 158, 11, 0.12); --callout-clinical: #38bdf8; --callout-clinical-bg: rgba(14, 165, 233, 0.12); --callout-warning: #f87171; --callout-warning-bg: rgba(239, 68, 68, 0.13); }
html[data-theme="paper"] { --bg: #f7f3e9; --surface: #fffdf8; --text: #2c241b; --muted: #6f6254; --accent: #9a3412; --accent-soft: #ffedd5; --select: #1d4ed8; --select-soft: #e0e7ff; --table-hideable-bg: #eef2e6; --table-hideable-color: #3f6212; --border: #e7dcc8; --callout-pearl-bg: #fef7e0; --callout-clinical-bg: #eef7fb; --callout-warning-bg: #fdf0ed; }
* { box-sizing: border-box; }
html, body { margin: 0; padding: 0; font-family: 'Segoe UI', system-ui, -apple-system, sans-serif; background: var(--bg); color: var(--text); }
.hidden { display: none !important; }
.mby-greeting-card, .mby-card, .mby-flashcard-face { background: var(--surface); border: 1px solid var(--border); border-radius: 18px; box-shadow: none; overflow: hidden; }
.mby-card { width: 100%; max-width: var(--card-max-width, 840px); display: flex; flex-direction: column; text-align: var(--content-align, left); font-size: var(--body-font-size, 16px); }
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
.mby-media-wrap { position: relative; display: inline-flex; justify-content: center; align-self: var(--media-align, flex-start); width: fit-content; max-width: 100%; border-radius: 12px; overflow: hidden; cursor: zoom-in; background: rgba(0, 0, 0, 0.02); }
.mby-media-wrap.is-static { cursor: default; }
.mby-media-img { max-width: 100%; max-height: var(--image-max-height, 360px); border-radius: 12px; object-fit: contain; display: block; }
.mby-zoom-hint { position: absolute; right: 10px; bottom: 10px; width: 30px; height: 30px; border-radius: 50%; background: rgba(0, 0, 0, 0.62); color: #ffffff; display: flex; align-items: center; justify-content: center; pointer-events: none; }
.mby-zoom-icon { width: 16px; height: 16px; display: block; stroke: #ffffff; }
.mby-figure { margin: 4px 0; padding: 10px; border: 1px solid var(--border); border-radius: 14px; background: var(--bg); display: flex; flex-direction: column; align-items: var(--media-align, flex-start); gap: 10px; break-inside: avoid; page-break-inside: avoid; }
.mby-figcaption { width: 100%; font-size: 0.92em; line-height: 1.5; color: var(--muted); text-align: var(--content-align, left); padding: 0 6px 2px; }
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
.mby-options-grid { display: grid; grid-template-columns: repeat(var(--option-columns, 1), minmax(0, 1fr)); gap: var(--option-gap, 12px); align-items: stretch; }
.mby-option-btn { display: flex; flex-direction: column; align-items: stretch; justify-content: flex-start; gap: 10px; width: 100%; height: 100%; text-align: inherit; padding: 14px 16px; border-radius: 12px; border: 2px solid var(--border); background: transparent; color: var(--text); cursor: pointer; font: inherit; transition: border-color 140ms ease, background 140ms ease; }
.mby-option-btn:hover { border-color: var(--select); }
.mby-option-btn.selected { border-color: var(--select); background: var(--select-soft); }
.mby-option-btn.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.12); }
.mby-option-btn.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.12); }
.mby-option-media { width: 100%; display: flex; justify-content: var(--media-align, flex-start); align-items: center; }
.mby-option-media .mby-media-wrap { width: fit-content; max-width: 100%; }
.mby-option-text { width: 100%; }
.mby-option-text p { margin: 0; }
.mby-option-text p + p { margin-top: 0.4em; }
.mby-action-row { margin-top: 20px; display: flex; flex-wrap: wrap; gap: 10px; align-items: center; }
.mby-check-btn, .mby-nav-btn { display: inline-flex; align-items: center; justify-content: center; gap: 6px; padding: 10px 18px; border-radius: 10px; border: 1px solid var(--border); background: var(--surface); color: var(--text); font-weight: 600; cursor: pointer; user-select: none; }
.mby-nav-btn.primary, .mby-check-btn { background: var(--accent); color: #ffffff; border-color: var(--accent); }
.mby-nav-btn:disabled { opacity: 0.45; cursor: not-allowed; }
.mby-retry-btn { display: inline-flex; align-items: center; justify-content: center; gap: 5px; padding: 10px 14px; border-radius: 10px; border: 1px solid var(--border); background: var(--bg); color: var(--text); font: inherit; font-size: 14px; font-weight: 600; cursor: pointer; user-select: none; transition: border-color 140ms ease, color 140ms ease, background 140ms ease; }
.mby-retry-btn:hover { border-color: var(--select); color: var(--select); background: var(--select-soft); }
.mby-retry-btn.is-compact { padding: 7px 14px; font-size: 13px; border-radius: 8px; }
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
.mby-flashcard-body h1 { margin: 0.35em 0 0.25em; font-size: calc(var(--prompt-font-size, 22px) * 1.25); line-height: 1.25; font-weight: 800; }
.mby-flashcard-body h2 { margin: 0.35em 0 0.2em; font-size: var(--prompt-font-size, 22px); line-height: 1.3; font-weight: 750; }
.mby-flashcard-body h3 { margin: 0.3em 0 0.15em; font-size: calc(var(--prompt-font-size, 22px) * 0.88); line-height: 1.35; font-weight: 700; }
.mby-flashcard-body h4 { margin: 0.25em 0 0.15em; font-size: calc(var(--prompt-font-size, 22px) * 0.8); line-height: 1.4; font-weight: 700; }
.mby-rating-row { display: flex; gap: 12px; margin-top: 8px; justify-content: center; }
.mby-rate-btn { padding: 9px 18px; border-radius: 8px; border: 1.5px solid var(--border); background: transparent; color: var(--text); font-weight: 600; cursor: pointer; }
.mby-rate-btn.active-known { border-color: var(--ok); background: rgba(25, 135, 84, 0.14); color: var(--ok); }
.mby-rate-btn.active-review { border-color: var(--bad); background: rgba(220, 53, 69, 0.14); color: var(--bad); }
.mby-flashcard-side-label { text-align: center; padding: 6px 16px 16px; font-size: 12px; font-weight: 600; color: var(--gray-label); }
.mby-generator-footer { display: flex; align-items: center; justify-content: space-between; gap: 10px; flex-wrap: wrap; padding: 11px 20px; border-top: 1px solid var(--border); background: var(--bg); color: var(--muted); font-size: 12px; line-height: 1.4; }
.mby-generator-link { display: inline-flex; align-items: center; gap: 6px; font-weight: 700; color: var(--accent); text-decoration: none; }
.mby-generator-link:hover { text-decoration: underline; }
.mby-github-icon { width: 15px; height: 15px; fill: currentColor; flex-shrink: 0; }
.mby-generator-meta { font-family: ui-monospace, Consolas, monospace; font-size: 11px; }
.mby-native-table { margin: 6px 0; border: 1px solid var(--border); border-radius: 14px; background: var(--surface); overflow: hidden; }
.mby-table-toolbar { display: flex; align-items: center; justify-content: space-between; gap: 10px; flex-wrap: wrap; padding: 10px 14px; background: var(--bg); border-bottom: 1px solid var(--border); }
.mby-table-title { font-size: 14px; font-weight: 750; color: var(--text); }
.mby-table-scroll { width: 100%; max-width: 100%; overflow-x: auto; }
.mby-native-table table { width: 100%; border-collapse: collapse; margin: 0; border: none; border-radius: 0; display: table; font-size: 0.94em; }
.mby-native-table thead { background: var(--accent-soft); color: var(--accent); }
.mby-native-table th, .mby-native-table td { padding: 10px 14px; border-bottom: 1px solid var(--border); border-right: 1px solid var(--border); vertical-align: top; }
.mby-native-table th:last-child, .mby-native-table td:last-child { border-right: none; }
.mby-native-table tbody tr:nth-child(even) { background: var(--bg); }
.mby-native-table th p, .mby-native-table td p { margin: 0; }
.mby-native-table th.mby-th-hideable { background: var(--table-hideable-bg); color: var(--table-hideable-color); cursor: pointer; user-select: none; box-shadow: inset 0 -2px 0 var(--table-hideable-color); transition: background 140ms ease, color 140ms ease, opacity 140ms ease; }
.mby-native-table th.mby-th-hideable:hover { filter: brightness(0.96); }
.mby-native-table th.mby-th-hideable:focus-visible { outline: 2px solid var(--select); outline-offset: -2px; }
.mby-native-table th.mby-th-hideable.is-col-hidden { opacity: 0.82; }
.mby-th-hideable-inner { display: inline-flex; align-items: center; gap: 6px; }
.mby-th-eye-icon { width: 14px; height: 14px; flex-shrink: 0; opacity: 0.8; }
.mby-sticky-col th:first-child, .mby-sticky-col td:first-child { position: sticky; left: 0; z-index: 2; background: var(--surface); box-shadow: 2px 0 0 var(--border); }
.mby-sticky-col thead th:first-child { background: var(--accent-soft); z-index: 3; }
.mby-sticky-col thead th.mby-th-hideable:first-child { background: var(--table-hideable-bg); }
.mby-sticky-col tbody tr:nth-child(even) td:first-child { background: var(--bg); }
.mby-cell-masked { cursor: pointer; user-select: none; position: relative; }
.mby-cell-masked > .mby-cell-inner { filter: blur(6px); opacity: 0.35; transition: filter 150ms ease, opacity 150ms ease; }
.mby-cell-masked.peek > .mby-cell-inner { filter: none; opacity: 1; }
.mby-table-caption { padding: 8px 14px; font-size: 12px; color: var(--muted); border-top: 1px solid var(--border); background: var(--bg); }
.mby-table-footnote { padding: 8px 14px; font-size: 12px; color: var(--muted); border-top: 1px solid var(--border); font-style: italic; }
.mby-table-caption p, .mby-table-footnote p { margin: 0; }
.mby-blank-prompt { font-size: var(--prompt-font-size, 22px); font-weight: 550; line-height: 1.7; margin-bottom: 16px; }
.mby-blank-wrap { display: inline-flex; flex-direction: column; vertical-align: baseline; margin: 0 3px; }
.mby-blank-input { font: inherit; font-size: 0.92em; padding: 3px 10px; min-width: 110px; max-width: 240px; border-radius: 8px; border: 2px solid var(--border); background: var(--bg); color: var(--text); outline: none; transition: border-color 140ms ease, background 140ms ease; }
.mby-blank-input:focus { border-color: var(--select); background: var(--surface); }
.mby-blank-input.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.12); }
.mby-blank-input.fuzzy-ok { border-color: var(--callout-pearl); background: var(--callout-pearl-bg); }
.mby-blank-input.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.12); }
.mby-blank-expected { font-size: 12px; font-weight: 700; color: var(--ok); margin-top: 2px; line-height: 1.3; }
.mby-override-btn { padding: 7px 13px; border-radius: 9px; border: 1.5px solid var(--ok); background: rgba(25, 135, 84, 0.1); color: var(--ok); font-size: 13px; font-weight: 700; cursor: pointer; }
.mby-ar-stack { display: flex; flex-direction: column; gap: 10px; margin-bottom: 18px; }
.mby-ar-box { padding: 12px 16px; border-radius: 12px; border: 1px solid var(--border); background: var(--bg); }
.mby-ar-label { font-size: 11px; font-weight: 800; letter-spacing: 0.04em; text-transform: uppercase; color: var(--accent); margin-bottom: 4px; }
.mby-ar-text { font-size: 1.02em; line-height: 1.5; }
.mby-ar-text p:first-child { margin-top: 0; }
.mby-ar-text p:last-child { margin-bottom: 0; }
.mby-spot-statement { font-size: var(--prompt-font-size, 22px); font-weight: 500; line-height: 2.0; margin-bottom: 16px; }
.mby-error-chip-wrap { display: inline-flex; flex-direction: column; align-items: center; vertical-align: bottom; margin: 0 2px; }
.mby-error-chip { display: inline; padding: 1px 6px; margin: 0; border-radius: 6px; border: 1.5px solid var(--border); background: var(--bg); color: var(--text); font: inherit; font-size: 0.88em; line-height: 1.35; cursor: pointer; transition: border-color 140ms ease, background 140ms ease; }
.mby-error-chip:hover { border-color: var(--select); }
.mby-error-chip.selected { border-color: var(--select); background: var(--select-soft); color: var(--select); font-weight: 650; }
.mby-error-chip.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.14); color: var(--ok); }
.mby-error-chip.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.14); color: var(--bad); }
.mby-error-chip.missed-error { border-color: var(--bad); border-style: dashed; background: rgba(220, 53, 69, 0.08); }
.mby-error-fix-badge { display: inline-block; margin-bottom: 3px; padding: 1px 7px; border-radius: 6px; background: rgba(25, 135, 84, 0.16); color: var(--ok); font-size: 0.72em; font-weight: 700; line-height: 1.25; white-space: nowrap; }
.mby-no-error-btn { display: inline-flex; align-items: center; justify-content: center; padding: 10px 16px; border-radius: 10px; border: 1.5px solid var(--border); background: var(--bg); color: var(--text); font-weight: 600; font-size: 14px; cursor: pointer; transition: border-color 140ms ease, background 140ms ease, color 140ms ease; }
.mby-no-error-btn:hover { border-color: var(--select); }
.mby-no-error-btn.selected { border-color: var(--select); background: var(--select-soft); color: var(--select); }
.mby-no-error-btn.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.14); color: var(--ok); }
.mby-no-error-btn.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.14); color: var(--bad); }
.mby-fix-input-row { display: flex; flex-direction: column; gap: 8px; margin-bottom: 14px; padding: 12px 14px; border-radius: 12px; border: 1px solid var(--border); background: var(--bg); }
.mby-fix-input-label { font-size: 12px; font-weight: 700; color: var(--muted); }
.mby-fix-input { font: inherit; font-size: 15px; padding: 7px 11px; border-radius: 8px; border: 1.5px solid var(--border); background: var(--surface); color: var(--text); outline: none; }
.mby-fix-input:focus { border-color: var(--select); }
.mby-diff-wrap { display: flex; flex-direction: column; gap: 12px; margin-bottom: 12px; }
.mby-diff-title { font-size: var(--prompt-font-size, 22px); font-weight: 700; line-height: 1.4; }
.mby-diff-scroll { width: 100%; max-width: 100%; overflow-x: auto; border: 1px solid var(--border); border-radius: 12px; }
.mby-diff-table { width: 100%; border-collapse: collapse; font-size: 0.94em; background: var(--surface); }
.mby-diff-table thead { background: var(--accent-soft); color: var(--accent); }
.mby-diff-table th, .mby-diff-table td { padding: 10px 12px; border-bottom: 1px solid var(--border); border-right: 1px solid var(--border); vertical-align: middle; }
.mby-diff-table th:last-child, .mby-diff-table td:last-child { border-right: none; }
.mby-diff-table tbody tr:last-child td { border-bottom: none; }
.mby-diff-table tbody tr:nth-child(even) { background: var(--bg); }
.mby-diff-bank { display: flex; flex-wrap: wrap; gap: 8px; padding: 10px 12px; border-radius: 12px; border: 1px solid var(--border); background: var(--bg); }
.mby-diff-bank-chip { padding: 6px 12px; border-radius: 999px; border: 1.5px solid var(--border); background: var(--surface); color: var(--text); font: inherit; font-size: 13px; font-weight: 600; cursor: pointer; transition: border-color 140ms ease, background 140ms ease, opacity 140ms ease; }
.mby-diff-bank-chip:hover { border-color: var(--select); }
.mby-diff-bank-chip.selected { border-color: var(--select); background: var(--select-soft); color: var(--select); }
.mby-diff-bank-chip.used { opacity: 0.45; border-style: dashed; }
.mby-diff-cell-btn { width: 100%; text-align: inherit; padding: 7px 10px; border-radius: 9px; border: 1.5px solid var(--border); background: var(--surface); color: var(--text); font: inherit; cursor: pointer; transition: border-color 140ms ease, background 140ms ease; }
.mby-diff-cell-btn:hover { border-color: var(--select); }
.mby-diff-cell-btn.selected, .mby-diff-cell-btn.swap-selected { border-color: var(--select); background: var(--select-soft); color: var(--select); font-weight: 650; }
.mby-diff-cell-btn.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.14); color: var(--ok); }
.mby-diff-cell-btn.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.14); color: var(--bad); }
.mby-diff-cell-btn.missed-error { border-color: var(--bad); border-style: dashed; background: rgba(220, 53, 69, 0.08); }
.mby-diff-fix-badge { display: inline-block; margin-top: 5px; padding: 2px 8px; border-radius: 6px; background: rgba(25, 135, 84, 0.16); color: var(--ok); font-size: 12px; font-weight: 700; }
.mby-diff-corrections { padding: 12px 14px; border-radius: 12px; border: 1px solid var(--border); border-left: 4px solid var(--ok); background: var(--bg); font-size: 0.94em; }
.mby-diff-corrections-title { font-size: 11px; font-weight: 800; letter-spacing: 0.04em; text-transform: uppercase; color: var(--ok); margin-bottom: 6px; }
.mby-diff-correction-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
.mby-diff-correction-item { display: flex; flex-wrap: wrap; align-items: baseline; gap: 6px; line-height: 1.4; }
.mby-diff-correction-loc { font-weight: 700; font-size: 12px; color: var(--muted); }
.mby-diff-wrong-strike { text-decoration: line-through; color: var(--bad); }
.mby-diff-right-text { font-weight: 700; color: var(--ok); }
.mby-mech-layout { display: flex; flex-direction: column; gap: 12px; margin-bottom: 12px; }
.mby-mech-title { font-size: var(--prompt-font-size, 22px); font-weight: 700; line-height: 1.4; }
.mby-mech-section-label { font-size: 11px; font-weight: 800; letter-spacing: 0.04em; text-transform: uppercase; color: var(--muted); }
.mby-mech-bank { display: flex; flex-wrap: wrap; gap: 8px; padding: 12px; border-radius: 12px; border: 1px solid var(--border); background: var(--bg); min-height: 52px; }
.mby-mech-chip { padding: 8px 13px; border-radius: 10px; border: 1.5px solid var(--border); background: var(--surface); color: var(--text); font: inherit; font-size: 14px; font-weight: 600; text-align: left; cursor: pointer; transition: border-color 140ms ease, background 140ms ease; }
.mby-mech-chip:hover { border-color: var(--select); background: var(--select-soft); }
.mby-mech-seq { display: flex; flex-direction: column; gap: 8px; padding: 12px; border-radius: 12px; border: 1.5px dashed var(--border); background: var(--bg); min-height: 68px; }
.mby-mech-empty { font-size: 13px; color: var(--muted); padding: 8px 4px; text-align: center; }
.mby-mech-slot { display: flex; align-items: center; gap: 10px; padding: 10px 12px; border-radius: 10px; border: 1.5px solid var(--border); background: var(--surface); will-change: transform; }
.mby-mech-slot.is-moving { border-color: var(--select); box-shadow: 0 6px 18px rgba(15, 10, 20, 0.14); z-index: 2; }
.mby-mech-slot.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.12); }
.mby-mech-slot.wrong, .mby-mech-slot.distractor-wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.12); }
.mby-mech-badge { min-width: 26px; height: 26px; border-radius: 8px; background: var(--accent-soft); color: var(--accent); font-size: 12px; font-weight: 800; display: inline-flex; align-items: center; justify-content: center; flex-shrink: 0; }
.mby-mech-slot-text { flex: 1; min-width: 0; line-height: 1.45; }
.mby-mech-slot-actions { display: flex; gap: 4px; flex-shrink: 0; }
.mby-mech-ctrl-btn { width: 28px; height: 28px; border-radius: 7px; border: 1px solid var(--border); background: var(--bg); color: var(--text); font-weight: 700; cursor: pointer; display: inline-flex; align-items: center; justify-content: center; }
.mby-mech-ctrl-btn:hover:not(:disabled) { border-color: var(--select); color: var(--select); }
.mby-mech-ctrl-btn:disabled { opacity: 0.35; cursor: not-allowed; }
.mby-mech-canonical { margin-top: 10px; padding: 12px 14px; border-radius: 12px; border: 1px solid var(--border); border-left: 4px solid var(--ok); background: var(--bg); font-size: 0.94em; }
.mby-mech-canonical ol { margin: 6px 0 0; padding-left: 20px; }
.mby-draggable { cursor: grab; }
.mby-draggable:active { cursor: grabbing; }
.is-dragging { opacity: 0.45; cursor: grabbing; }
.mby-drop-over { border-color: var(--select) !important; background: var(--select-soft) !important; box-shadow: 0 0 0 2px var(--select-soft); }
.mby-relabel-layout { display: flex; flex-direction: column; gap: 12px; margin-bottom: 12px; }
.mby-relabel-prompt { font-size: var(--prompt-font-size, 22px); font-weight: 700; line-height: 1.4; }
.mby-relabel-toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.mby-relabel-tool-btn { padding: 6px 12px; border-radius: 9px; border: 1px solid var(--border); background: var(--bg); color: var(--text); font: inherit; font-size: 13px; font-weight: 650; cursor: pointer; }
.mby-relabel-tool-btn:hover { border-color: var(--select); color: var(--select); }
.mby-relabel-stage-wrap { display: flex; justify-content: center; width: 100%; }
.mby-relabel-stage { position: relative; display: inline-block; max-width: 100%; line-height: 0; border-radius: 12px; overflow: hidden; border: 1px solid var(--border); }
.mby-relabel-img { display: block; max-width: 100%; max-height: var(--image-max-height, 360px); width: auto; height: auto; user-select: none; }
.mby-mask-box { position: absolute; box-sizing: border-box; padding: 0; margin: 0; border-radius: 6px; cursor: pointer; display: flex; align-items: center; justify-content: center; overflow: hidden; transition: border-color 140ms ease, background 140ms ease, box-shadow 140ms ease; }
.mby-mask-solid { background: var(--accent); border: 1.5px solid rgba(255, 255, 255, 0.7); }
.mby-mask-blur { background: rgba(15, 23, 42, 0.22); backdrop-filter: blur(8px); -webkit-backdrop-filter: blur(8px); border: 1.5px solid var(--accent); }
.mby-mask-pixelate { background-image: repeating-conic-gradient(#334155 0% 25%, #94a3b8 0% 50%); background-size: 8px 8px; border: 1.5px solid #1e293b; }
.mby-mask-whiteout { background: #ffffff; border: 1.5px solid var(--border); }
.mby-mask-box.active { box-shadow: inset 0 0 0 2px #ffffff, 0 0 0 2px var(--select); z-index: 2; }
.mby-mask-box.revealed, .mby-mask-box.correct { background: rgba(25, 135, 84, 0.14); background-image: none; backdrop-filter: none; -webkit-backdrop-filter: none; border: 2px solid var(--ok); }
.mby-mask-box.wrong { background: rgba(220, 53, 69, 0.16); background-image: none; backdrop-filter: none; -webkit-backdrop-filter: none; border: 2px solid var(--bad); }
.mby-mask-badge { display: inline-flex; align-items: center; justify-content: center; min-width: 18px; height: 18px; padding: 0 5px; border-radius: 999px; background: rgba(15, 23, 42, 0.86); color: #ffffff; font-size: 11px; font-weight: 800; line-height: 1; pointer-events: none; max-width: 100%; overflow: hidden; }
.mby-mask-box.revealed .mby-mask-badge, .mby-mask-box.correct .mby-mask-badge { background: var(--ok); }
.mby-mask-box.wrong .mby-mask-badge { background: var(--bad); }
.mby-relabel-assignments { display: flex; flex-direction: column; gap: 6px; padding: 10px 12px; border-radius: 12px; border: 1px solid var(--border); background: var(--bg); }
.mby-relabel-assign-row { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; padding: 6px 10px; border-radius: 9px; border: 1.5px solid var(--border); background: var(--surface); cursor: pointer; font-size: 14px; }
.mby-relabel-assign-row.active { border-color: var(--select); background: var(--select-soft); }
.mby-relabel-assign-row.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.12); }
.mby-relabel-assign-row.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.12); }
.mby-relabel-bank { display: flex; flex-wrap: wrap; gap: 8px; padding: 10px 12px; border-radius: 12px; border: 1px solid var(--border); background: var(--bg); }
.mby-relabel-bank-chip { padding: 6px 12px; border-radius: 999px; border: 1.5px solid var(--border); background: var(--surface); color: var(--text); font: inherit; font-size: 13px; font-weight: 600; cursor: pointer; }
.mby-relabel-bank-chip:hover { border-color: var(--select); }
.mby-relabel-bank-chip.used { opacity: 0.45; border-style: dashed; }
.mby-relabel-recall-panel { display: flex; flex-direction: column; gap: 8px; padding: 10px 12px; border-radius: 12px; border: 1px solid var(--border); background: var(--bg); }
.mby-relabel-recall-row { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.mby-relabel-recall-input { flex: 1; min-width: 160px; padding: 7px 10px; border-radius: 9px; border: 1.5px solid var(--border); background: var(--surface); color: var(--text); font: inherit; font-size: 14px; }
.mby-relabel-recall-input:focus { outline: none; border-color: var(--select); }
.mby-relabel-recall-input.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.12); }
.mby-relabel-recall-input.fuzzy-ok { border-color: var(--callout-pearl); background: var(--callout-pearl-bg); }
.mby-relabel-recall-input.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.12); }
.mby-diagram-wrap { display: flex; flex-direction: column; gap: 10px; padding: 14px; border-radius: 14px; border: 1px solid var(--border); background: var(--bg); }
.mby-diagram-head { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 8px; }
.mby-diagram-title { font-size: 15px; font-weight: 800; color: var(--text); }
.mby-diagram-stage { position: relative; display: block; width: fit-content; max-width: 100%; margin: 0 auto; border-radius: 12px; overflow: hidden; border: 1px solid var(--border); background: var(--surface); }
.mby-diagram-img { display: block; max-width: 100%; max-height: var(--image-max-height, 360px); width: auto; height: auto; }
.mby-diagram-hotspot { position: absolute; box-sizing: border-box; border-radius: 8px; border: 2px solid var(--select); background: rgba(157, 23, 77, 0.14); color: var(--text); cursor: pointer; padding: 0; transition: transform 140ms ease, background 140ms ease, box-shadow 140ms ease; }
.mby-diagram-hotspot:hover, .mby-diagram-hotspot.active { background: rgba(157, 23, 77, 0.28); box-shadow: 0 0 0 3px var(--select-soft); transform: scale(1.02); }
.mby-diagram-pin { position: absolute; top: 3px; left: 3px; min-width: 20px; height: 20px; padding: 0 5px; border-radius: 999px; background: var(--select); color: #fff; font-size: 11px; font-weight: 800; display: inline-flex; align-items: center; justify-content: center; }
.mby-diagram-nav { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
.mby-diagram-card { padding: 12px 14px; border-radius: 12px; border: 1.5px solid var(--select); background: var(--surface); display: flex; flex-direction: column; gap: 6px; }
.mby-diagram-card-title { font-size: 14px; font-weight: 800; color: var(--select); }
.mby-diagram-all-list { display: flex; flex-direction: column; gap: 8px; }
.mby-reveal-block { display: flex; flex-direction: column; gap: 10px; padding: 14px; border-radius: 14px; border: 1px solid var(--border); background: var(--bg); }
.mby-reveal-header { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 8px; }
.mby-reveal-title { font-size: 15px; font-weight: 800; color: var(--text); }
.mby-reveal-actions { display: flex; flex-wrap: wrap; gap: 6px; }
.mby-reveal-steps { display: flex; flex-direction: column; gap: 8px; }
.mby-reveal-step { border-radius: 12px; border: 1px solid var(--border); background: var(--surface); padding: 11px 14px; transition: border-color 140ms ease, opacity 140ms ease; }
.mby-reveal-step.is-locked { opacity: 0.55; border-style: dashed; }
.mby-reveal-step.is-next { opacity: 1; border-color: var(--select); background: var(--select-soft); }
.mby-reveal-step-head { display: flex; align-items: center; justify-content: space-between; gap: 10px; font-weight: 700; font-size: 14px; color: var(--text); }
.mby-reveal-step-body { margin-top: 8px; padding-top: 8px; border-top: 1px solid var(--border); font-size: 14px; line-height: 1.55; }
.mby-reveal-unlock-btn { margin-top: 6px; padding: 6px 12px; border-radius: 999px; border: 1.5px solid var(--select); background: var(--surface); color: var(--select); font: inherit; font-size: 13px; font-weight: 700; cursor: pointer; }
.mby-classify-wrap { display: flex; flex-direction: column; gap: 10px; }
.mby-classify-pool { display: flex; flex-wrap: wrap; gap: 8px; min-height: 44px; padding: 10px 12px; border-radius: 12px; border: 1.5px dashed var(--border); background: var(--bg); align-items: center; }
.mby-classify-buckets { display: grid; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr)); gap: 10px; }
.mby-classify-bucket { display: flex; flex-direction: column; gap: 8px; min-height: 96px; padding: 10px 12px; border-radius: 12px; border: 1.5px solid var(--border); background: var(--bg); cursor: pointer; }
.mby-classify-bucket.target-ready { border-color: var(--select); background: var(--select-soft); }
.mby-classify-bucket-title { font-size: 13px; font-weight: 800; color: var(--select); text-transform: uppercase; letter-spacing: 0.03em; }
.mby-classify-bucket-items { display: flex; flex-wrap: wrap; gap: 6px; }
.mby-classify-chip { padding: 6px 11px; border-radius: 999px; border: 1.5px solid var(--border); background: var(--surface); color: var(--text); font: inherit; font-size: 13.5px; font-weight: 600; cursor: pointer; display: inline-flex; align-items: center; gap: 6px; max-width: 100%; }
.mby-classify-chip.selected { border-color: var(--select); background: var(--select-soft); }
.mby-classify-chip.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.14); }
.mby-classify-chip.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.14); }
.mby-classify-fix { font-size: 11px; font-weight: 700; color: var(--ok); }
.mby-match-wrap { display: flex; flex-direction: column; gap: 10px; }
.mby-match-rows { display: flex; flex-direction: column; gap: 8px; }
.mby-match-row { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1.2fr); gap: 8px; align-items: stretch; }
@media (max-width: 560px) { .mby-match-row { grid-template-columns: 1fr; } }
.mby-match-left { padding: 9px 12px; border-radius: 10px; border: 1px solid var(--border); background: var(--bg); font-weight: 650; font-size: 14px; display: flex; align-items: center; }
.mby-match-slot { padding: 8px 12px; border-radius: 10px; border: 1.5px dashed var(--border); background: var(--surface); color: var(--text); font: inherit; font-size: 14px; display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 6px; cursor: pointer; text-align: left; }
.mby-match-slot.filled { border-style: solid; }
.mby-match-slot.target-ready { border-color: var(--select); background: var(--select-soft); }
.mby-match-slot.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.14); }
.mby-match-slot.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.14); }
.mby-match-bank { display: flex; flex-wrap: wrap; gap: 8px; padding: 10px 12px; border-radius: 12px; border: 1px solid var(--border); background: var(--bg); }
.mby-match-chip { padding: 6px 12px; border-radius: 999px; border: 1.5px solid var(--border); background: var(--surface); color: var(--text); font: inherit; font-size: 13.5px; font-weight: 600; cursor: pointer; }
.mby-match-chip.selected { border-color: var(--select); background: var(--select-soft); }
.mby-match-chip.used { opacity: 0.45; border-style: dashed; }
.mby-numeric-row { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.mby-numeric-input { width: 170px; max-width: 100%; padding: 9px 12px; border-radius: 10px; border: 1.5px solid var(--border); background: var(--surface); color: var(--text); font: inherit; font-size: 15px; font-weight: 650; }
.mby-numeric-input:focus { outline: none; border-color: var(--select); }
.mby-numeric-input.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.12); }
.mby-numeric-input.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.12); }
.mby-numeric-unit { padding: 4px 10px; border-radius: 999px; background: var(--bg); border: 1px solid var(--border); font-size: 13px; font-weight: 700; color: var(--muted); }
.mby-numeric-range { font-size: 13px; color: var(--muted); font-weight: 600; }
.mby-case-layout { display: grid; grid-template-columns: minmax(0, 1fr); gap: 12px; }
@media (min-width: 900px) { .mby-case-layout.has-split { grid-template-columns: minmax(260px, 0.92fr) minmax(0, 1.08fr); align-items: start; } }
.mby-case-stem { padding: 12px 14px; border-radius: 12px; border: 1px solid var(--border); background: var(--bg); display: flex; flex-direction: column; gap: 8px; }
.mby-case-stem-badge { align-self: flex-start; padding: 2px 9px; border-radius: 999px; background: var(--select-soft); color: var(--select); font-size: 11px; font-weight: 800; text-transform: uppercase; letter-spacing: 0.04em; }
.mby-case-main { display: flex; flex-direction: column; gap: 10px; min-width: 0; }
.mby-case-tabs { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
.mby-case-tab { padding: 5px 11px; border-radius: 999px; border: 1.5px solid var(--border); background: var(--surface); color: var(--text); font: inherit; font-size: 12.5px; font-weight: 700; cursor: pointer; }
.mby-case-tab.active { border-color: var(--select); background: var(--select-soft); color: var(--select); }
.mby-case-tab.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.14); color: var(--ok); }
.mby-case-tab.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.14); color: var(--bad); }
.mby-case-subhost { min-width: 0; }
.mby-case-subhost > .mby-card { padding: 12px; border-radius: 12px; box-shadow: none; }
.mby-native-link { color: var(--select); font-weight: 650; text-decoration: underline; text-decoration-style: dotted; text-underline-offset: 3px; text-decoration-thickness: 1.5px; border-radius: 4px; padding: 0 2px; transition: background 140ms ease, color 140ms ease; }
.mby-native-link:hover, .mby-native-link:focus-visible { background: var(--select-soft); text-decoration-style: solid; outline: none; }
.mby-link-popover { position: fixed; z-index: 70; width: min(370px, calc(100vw - 24px)); background: var(--surface); border: 1px solid var(--border); border-radius: 14px; box-shadow: 0 18px 44px rgba(15, 10, 20, 0.24); padding: 14px 16px; display: flex; flex-direction: column; gap: 8px; pointer-events: auto; }
.mby-link-popover-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
.mby-link-popover-badge { display: inline-flex; align-items: center; padding: 2px 8px; border-radius: 999px; background: var(--select-soft); color: var(--select); font-size: 10px; font-weight: 800; letter-spacing: 0.04em; text-transform: uppercase; }
.mby-link-popover-title { font-size: 15px; font-weight: 800; color: var(--text); line-height: 1.3; }
.mby-link-popover-img { max-width: 100%; max-height: 180px; border-radius: 10px; object-fit: contain; display: block; margin: 0 auto; cursor: zoom-in; border: 1px solid var(--border); }
.mby-link-popover-body { font-size: 13.5px; line-height: 1.5; color: var(--text); }
.mby-link-popover-body p:first-child { margin-top: 0; }
.mby-link-popover-body p:last-child { margin-bottom: 0; }
.mby-link-popover-excerpt { font-size: 13px; line-height: 1.55; color: var(--muted); max-height: 145px; overflow: hidden; -webkit-mask-image: linear-gradient(to bottom, #000 58%, transparent 100%); mask-image: linear-gradient(to bottom, #000 58%, transparent 100%); }
.mby-link-popover-footer { font-size: 11px; color: var(--muted); font-weight: 600; display: flex; align-items: center; justify-content: space-between; gap: 8px; padding-top: 4px; border-top: 1px solid var(--border); }
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
  if (l.contentAlign) {
    const alignMap = { left: 'flex-start', center: 'center', right: 'flex-end' };
    el.style.setProperty('--media-align', alignMap[l.contentAlign] || 'flex-start');
  }
}
function createUpperTab(label) { return mk('div', 'mby-type-tab', label); }
function createZoomableImageNode(src, onZoom, onLoad, alt) {
  const wrap = mk('div', 'mby-media-wrap' + (onZoom ? '' : ' is-static'));
  const img = mk('img', 'mby-media-img');
  img.src = src;
  if (alt) img.alt = alt;
  if (onLoad) img.addEventListener('load', onLoad);
  wrap.appendChild(img);
  if (onZoom) {
    const hint = mk('span', 'mby-zoom-hint');
    hint.innerHTML = MAGNIFIER_ICON_SVG;
    hint.setAttribute('aria-hidden', 'true');
    wrap.appendChild(hint);
    wrap.addEventListener('click', e => { e.stopPropagation(); onZoom(src, alt); });
  }
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
  triggerFlip.cancel = () => {
    flipToken += 1;
    if (flipTimer !== null) { clearTimeout(flipTimer); flipTimer = null; }
  };
  return triggerFlip;
}
""";
}
