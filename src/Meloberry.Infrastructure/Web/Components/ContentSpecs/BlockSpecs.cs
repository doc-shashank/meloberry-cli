namespace Meloberry.Infrastructure.Web.Components.ContentSpecs;

internal static class BlockSpecs
{
    private const string ArticleCss = """
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
""";

    private const string CalloutCss = """
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
""";

    private const string CalloutJs = """
(function (MBY) {
  function calloutDefaultTitle(tone) {
    if (tone === 'clinical') return 'Clinical Correlation';
    if (tone === 'warning') return 'Warning';
    return 'High-Yield Pearl';
  }

  MBY.blocks['callout'] = (b, ctx) => {
    if (!b.textHtml) return null;
    const tone = b.tone || 'pearl';
    const hdr = mk('div', 'mby-callout-header');
    if (b.titleHtml) { hdr.innerHTML = b.titleHtml; ctx.renderMathIn(hdr); }
    else { hdr.textContent = b.title || calloutDefaultTitle(tone); }
    const body = mk('div', 'mby-callout-body');
    body.innerHTML = b.textHtml;
    ctx.renderMathIn(body);
    const aside = mk('aside', `mby-callout mby-callout-${tone}`, null, [hdr, body]);
    aside.setAttribute('role', 'note');
    aside.dataset.blockId = b.id;
    return aside;
  };

  MBY.blockSearch['callout'] = (b, parts, ctx) => {
    if (b.title) parts.push(b.title);
    if (b.textHtml) parts.push(ctx.htmlToPlainText(b.textHtml));
  };
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    private const string TableCss = """
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
""";

    private const string TableJs = """
(function (MBY) {
  const TABLE_EYE_SVG = '<svg class="mby-th-eye-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><use href="#mby-ico-eye"/></svg>';
  const TABLE_EYE_OFF_SVG = '<svg class="mby-th-eye-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><use href="#mby-ico-eye-off"/></svg>';

  function buildNativeTableBlock(b, ctx) {
    const t = b.table;
    if (!t) return null;
    const wrap = mk('div', 'mby-native-table' + (t.stickyFirstCol ? ' mby-sticky-col' : ''));
    wrap.dataset.blockId = b.id;

    const hiddenCols = new Set();
    const hideableSet = new Set(t.hideableCols || []);
    const tdByCol = new Map();

    if (t.title || t.titleHtml) {
      const toolbar = mk('div', 'mby-table-toolbar');
      const titleEl = mk('div', 'mby-table-title');
      if (t.titleHtml) { titleEl.innerHTML = t.titleHtml; ctx.renderMathIn(titleEl); }
      else { titleEl.textContent = t.title || 'Table'; }
      toolbar.appendChild(titleEl);
      wrap.appendChild(toolbar);
    }

    const scroll = mk('div', 'mby-table-scroll');
    const tableEl = mk('table');
    const thead = mk('thead');
    const headTr = mk('tr');
    (t.headersHtml || []).forEach((hHtml, idx) => {
      const colNum = idx + 1;
      const th = mk('th');
      if (t.alignments?.[idx]) th.style.textAlign = t.alignments[idx];
      if (hideableSet.has(colNum)) {
        th.classList.add('mby-th-hideable');
        th.tabIndex = 0;
        th.setAttribute('role', 'button');
        th.setAttribute('aria-pressed', 'false');
        th.title = 'Click to hide or show column';
        const inner = mk('span', 'mby-th-hideable-inner');
        const lbl = mk('span');
        lbl.innerHTML = hHtml;
        ctx.renderMathIn(lbl);
        const eyeWrap = mk('span');
        eyeWrap.innerHTML = TABLE_EYE_SVG;
        inner.appendChild(lbl);
        inner.appendChild(eyeWrap);
        th.appendChild(inner);
        const toggleColumn = () => {
          const isHidden = hiddenCols.has(colNum);
          if (isHidden) hiddenCols.delete(colNum); else hiddenCols.add(colNum);
          const nowHidden = hiddenCols.has(colNum);
          th.classList.toggle('is-col-hidden', nowHidden);
          th.setAttribute('aria-pressed', nowHidden ? 'true' : 'false');
          eyeWrap.innerHTML = nowHidden ? TABLE_EYE_OFF_SVG : TABLE_EYE_SVG;
          (tdByCol.get(colNum) || []).forEach(td => {
            td.classList.toggle('mby-cell-masked', nowHidden);
            td.classList.remove('peek');
          });
        };
        th.addEventListener('click', toggleColumn);
        th.addEventListener('keydown', e => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            toggleColumn();
          }
        });
      } else {
        th.innerHTML = hHtml;
        ctx.renderMathIn(th);
      }
      headTr.appendChild(th);
    });
    thead.appendChild(headTr);
    tableEl.appendChild(thead);

    const tbody = mk('tbody');
    (t.rowsHtml || []).forEach(row => {
      const tr = mk('tr');
      row.forEach((cellHtml, idx) => {
        const colNum = idx + 1;
        const td = mk('td');
        if (t.alignments?.[idx]) td.style.textAlign = t.alignments[idx];
        const inner = mk('div', 'mby-cell-inner');
        inner.innerHTML = cellHtml;
        ctx.renderMathIn(inner);
        td.appendChild(inner);
        if (hideableSet.has(colNum)) {
          if (!tdByCol.has(colNum)) tdByCol.set(colNum, []);
          tdByCol.get(colNum).push(td);
          td.addEventListener('click', () => { if (td.classList.contains('mby-cell-masked')) td.classList.toggle('peek'); });
        }
        tr.appendChild(td);
      });
      tbody.appendChild(tr);
    });
    tableEl.appendChild(tbody);
    scroll.appendChild(tableEl);
    wrap.appendChild(scroll);

    if (t.captionHtml) {
      const cap = mk('div', 'mby-table-caption');
      cap.innerHTML = t.captionHtml;
      ctx.renderMathIn(cap);
      wrap.appendChild(cap);
    }
    if (t.footnoteHtml) {
      const fn = mk('div', 'mby-table-footnote');
      fn.innerHTML = t.footnoteHtml;
      ctx.renderMathIn(fn);
      wrap.appendChild(fn);
    }
    return wrap;
  }

  MBY.blocks['table'] = buildNativeTableBlock;
  MBY.blockSearch['table'] = (b, parts, ctx) => {
    if (!b.table) return;
    if (b.table.title) parts.push(b.table.title);
    (b.table.headersHtml || []).forEach(h => parts.push(ctx.htmlToPlainText(h)));
    (b.table.rowsHtml || []).forEach(r => r.forEach(c => parts.push(ctx.htmlToPlainText(c))));
    if (b.table.captionHtml) parts.push(ctx.htmlToPlainText(b.table.captionHtml));
    if (b.table.footnoteHtml) parts.push(ctx.htmlToPlainText(b.table.footnoteHtml));
  };
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    private const string DiagramExploreCss = """
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
.mby-diagram-card-body { font-size: 0.96em; line-height: 1.55; color: var(--text); }
.mby-diagram-card-body p:first-child { margin-top: 0; }
.mby-diagram-card-body p:last-child { margin-bottom: 0; }
.mby-diagram-all-list { display: flex; flex-direction: column; gap: 8px; }
""";

    private const string DiagramExploreJs = """
(function (MBY) {
  function buildDiagramExploreBlock(b, ctx) {
    const diag = b.diagramExplore;
    const wrap = mk('div', 'mby-diagram-wrap');
    wrap.dataset.blockId = b.id;
    if (!diag) return wrap;

    const hotspots = diag.hotspots || [];
    let activeIdx = 0;
    let showAll = false;

    const head = mk('div', 'mby-diagram-head');
    if (diag.titleHtml || diag.title) {
      const tEl = mk('div', 'mby-diagram-title');
      if (diag.titleHtml) { tEl.innerHTML = diag.titleHtml; ctx.renderMathIn(tEl); }
      else tEl.textContent = diag.title;
      head.appendChild(tEl);
    }
    const nav = mk('div', 'mby-diagram-nav');
    const prevSpotBtn = mk('button', 'mby-retry-btn is-compact', 'Prev'); prevSpotBtn.type = 'button';
    const nextSpotBtn = mk('button', 'mby-retry-btn is-compact', 'Next'); nextSpotBtn.type = 'button';
    const showAllBtn = mk('button', 'mby-retry-btn is-compact', 'Show All'); showAllBtn.type = 'button';
    const zoomBtn = mk('button', 'mby-retry-btn is-compact', 'Zoom'); zoomBtn.type = 'button';
    zoomBtn.addEventListener('click', () => ctx.openZoomLightbox(diag.imagePath, diag.alt || diag.title || 'Diagram'));
    nav.appendChild(prevSpotBtn); nav.appendChild(nextSpotBtn); nav.appendChild(showAllBtn); nav.appendChild(zoomBtn);
    head.appendChild(nav);
    wrap.appendChild(head);

    const stage = mk('div', 'mby-diagram-stage');
    const img = mk('img', 'mby-diagram-img');
    img.decoding = 'async';
    img.loading = 'lazy';
    img.src = diag.imagePath;
    if (diag.alt) img.alt = diag.alt;
    stage.appendChild(img);

    const spotBtns = hotspots.map((hs, idx) => {
      const pin = mk('button', 'mby-diagram-hotspot');
      pin.type = 'button';
      pin.title = hs.label;
      pin.style.left = (hs.x * 100).toFixed(4) + '%';
      pin.style.top = (hs.y * 100).toFixed(4) + '%';
      pin.style.width = (hs.w * 100).toFixed(4) + '%';
      pin.style.height = (hs.h * 100).toFixed(4) + '%';
      pin.appendChild(mk('span', 'mby-diagram-pin', String(idx + 1)));
      pin.addEventListener('click', () => { activeIdx = idx; showAll = false; syncView(); });
      stage.appendChild(pin);
      return pin;
    });
    wrap.appendChild(stage);

    if (diag.captionHtml) {
      const cap = mk('div', 'mby-figcaption');
      cap.innerHTML = diag.captionHtml;
      ctx.renderMathIn(cap);
      wrap.appendChild(cap);
    }

    const cardHost = mk('div');
    wrap.appendChild(cardHost);

    function syncView() {
      spotBtns.forEach((btn, idx) => btn.classList.toggle('active', !showAll && idx === activeIdx));
      showAllBtn.textContent = showAll ? 'Active Only' : 'Show All';
      cardHost.innerHTML = '';
      if (hotspots.length === 0) return;

      if (showAll) {
        const list = mk('div', 'mby-diagram-all-list');
        hotspots.forEach((hs, idx) => {
          const c = mk('div', 'mby-diagram-card');
          c.appendChild(mk('div', 'mby-diagram-card-title', `${idx + 1}. ${hs.label}`));
          const body = mk('div', 'mby-diagram-card-body');
          body.innerHTML = hs.noteHtml || '';
          ctx.renderMathIn(body);
          c.appendChild(body);
          list.appendChild(c);
        });
        cardHost.appendChild(list);
      } else {
        const hs = hotspots[Math.max(0, Math.min(activeIdx, hotspots.length - 1))];
        const c = mk('div', 'mby-diagram-card');
        c.appendChild(mk('div', 'mby-diagram-card-title', `${activeIdx + 1} / ${hotspots.length} • ${hs.label}`));
        const body = mk('div', 'mby-diagram-card-body');
        body.innerHTML = hs.noteHtml || '';
        ctx.renderMathIn(body);
        c.appendChild(body);
        cardHost.appendChild(c);
      }
      requestAnimationFrame(ctx.updateScrollDownHint);
    }

    prevSpotBtn.addEventListener('click', () => { if (hotspots.length) { showAll = false; activeIdx = (activeIdx - 1 + hotspots.length) % hotspots.length; syncView(); } });
    nextSpotBtn.addEventListener('click', () => { if (hotspots.length) { showAll = false; activeIdx = (activeIdx + 1) % hotspots.length; syncView(); } });
    showAllBtn.addEventListener('click', () => { showAll = !showAll; syncView(); });
    syncView();
    return wrap;
  }

  MBY.blocks['diagram-explore'] = buildDiagramExploreBlock;
  MBY.blockSearch['diagram-explore'] = (b, parts, ctx) => {
    if (!b.diagramExplore) return;
    if (b.diagramExplore.title) parts.push(b.diagramExplore.title);
    (b.diagramExplore.hotspots || []).forEach(hs => parts.push(hs.label + ' ' + ctx.htmlToPlainText(hs.noteHtml)));
  };
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    private const string RevealCss = """
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
""";

    private const string RevealJs = """
(function (MBY) {
  function buildRevealBlock(b, ctx) {
    const rev = b.reveal;
    const wrap = mk('div', 'mby-reveal-block');
    wrap.dataset.blockId = b.id;
    if (!rev) return wrap;

    const steps = rev.steps || [];
    let unlocked = rev.showFirst ? Math.min(1, steps.length) : 0;

    const header = mk('div', 'mby-reveal-header');
    const titleEl = mk('div', 'mby-reveal-title');
    if (rev.titleHtml) { titleEl.innerHTML = rev.titleHtml; ctx.renderMathIn(titleEl); }
    else titleEl.textContent = rev.title || 'Step-by-Step Reveal';
    header.appendChild(titleEl);

    const actions = mk('div', 'mby-reveal-actions');
    const revAllBtn = mk('button', 'mby-retry-btn is-compact', 'Reveal All'); revAllBtn.type = 'button';
    const resetBtn = mk('button', 'mby-retry-btn is-compact', 'Reset'); resetBtn.type = 'button';
    actions.appendChild(revAllBtn); actions.appendChild(resetBtn);
    header.appendChild(actions);
    wrap.appendChild(header);

    const listEl = mk('div', 'mby-reveal-steps');
    wrap.appendChild(listEl);

    function renderSteps() {
      listEl.innerHTML = '';
      steps.forEach((st, idx) => {
        const isUnlocked = idx < unlocked;
        const isNext = idx === unlocked;
        const row = mk('div', 'mby-reveal-step ' + (isUnlocked ? 'is-unlocked' : (isNext ? 'is-next' : 'is-locked')));
        const sHead = mk('div', 'mby-reveal-step-head');
        const titleSpan = mk('span');
        titleSpan.innerHTML = st.headingHtml || `Step ${idx + 1}`;
        ctx.renderMathIn(titleSpan);
        sHead.appendChild(titleSpan);
        sHead.appendChild(mk('span', 'mby-step-counter', `${idx + 1}/${steps.length}`));
        row.appendChild(sHead);

        if (isUnlocked) {
          const sBody = mk('div', 'mby-reveal-step-body');
          sBody.innerHTML = st.bodyHtml || '';
          ctx.renderMathIn(sBody);
          row.appendChild(sBody);
        } else if (isNext) {
          const unlockBtn = mk('button', 'mby-reveal-unlock-btn', `Reveal Step ${idx + 1}`);
          unlockBtn.type = 'button';
          unlockBtn.addEventListener('click', () => { unlocked = Math.min(steps.length, unlocked + 1); renderSteps(); });
          row.appendChild(unlockBtn);
        }
        listEl.appendChild(row);
      });
      requestAnimationFrame(ctx.updateScrollDownHint);
    }

    revAllBtn.addEventListener('click', () => { unlocked = steps.length; renderSteps(); });
    resetBtn.addEventListener('click', () => { unlocked = rev.showFirst ? Math.min(1, steps.length) : 0; renderSteps(); });
    renderSteps();
    return wrap;
  }

  MBY.blocks['reveal'] = buildRevealBlock;
  MBY.blockSearch['reveal'] = (b, parts, ctx) => {
    if (!b.reveal) return;
    if (b.reveal.title) parts.push(b.reveal.title);
    (b.reveal.steps || []).forEach(st => parts.push(ctx.htmlToPlainText(st.headingHtml) + ' ' + ctx.htmlToPlainText(st.bodyHtml)));
  };
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    public static IReadOnlyList<WebComponentModule> Modules { get; } =
    [
        new("article", "blocks", f => f.HasArticle, ArticleCss, null),
        new("callout", "blocks", f => f.HasCallout, CalloutCss, CalloutJs),
        new("table", "blocks", f => f.HasTable, TableCss, TableJs),
        new("diagram-explore", "blocks", f => f.HasDiagramExplore, DiagramExploreCss, DiagramExploreJs),
        new("reveal", "blocks", f => f.HasReveal, RevealCss, RevealJs)
    ];
}
