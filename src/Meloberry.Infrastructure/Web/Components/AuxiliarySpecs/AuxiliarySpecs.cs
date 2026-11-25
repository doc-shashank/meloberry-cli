namespace Meloberry.Infrastructure.Web.Components.AuxiliarySpecs;

internal static class AuxiliarySpecsCatalog
{
    internal const string EditorMediaZoomJs = """
const MAGNIFIER_ICON_SVG = '<svg class="mby-zoom-icon" viewBox="0 0 24 24" fill="none" stroke="#ffffff" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="11" cy="11" r="7"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></svg>';
function createZoomableImageNode(src, onZoom, onLoad, alt) {
  const wrap = mk('div', 'mby-media-wrap' + (onZoom ? '' : ' is-static'));
  const img = mk('img', 'mby-media-img');
  img.decoding = 'async';
  img.loading = 'lazy';
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
""";

    internal const string EditorGradedFeedbackJs = """
function renderMcqFeedback(feedbackEl, isCorrect) {
  feedbackEl.textContent = isCorrect ? 'Correct!' : 'Review highlighted options.';
  feedbackEl.style.color = isCorrect ? 'var(--ok)' : 'var(--bad)';
  feedbackEl.style.fontWeight = '600';
}
""";

    private const string MediaZoomCss = """
.mby-media-wrap { position: relative; display: inline-flex; justify-content: center; align-self: var(--media-align, flex-start); width: fit-content; max-width: 100%; border-radius: 12px; overflow: hidden; cursor: zoom-in; background: rgba(0, 0, 0, 0.02); }
.mby-media-wrap.is-static { cursor: default; }
.mby-media-img { max-width: 100%; max-height: var(--image-max-height, 360px); border-radius: 12px; object-fit: contain; display: block; }
.mby-zoom-hint { position: absolute; right: 10px; bottom: 10px; width: 30px; height: 30px; border-radius: 50%; background: rgba(0, 0, 0, 0.62); color: #ffffff; display: flex; align-items: center; justify-content: center; pointer-events: none; }
.mby-zoom-icon { width: 16px; height: 16px; display: block; stroke: #ffffff; }
.mby-figure { margin: 4px 0; padding: 10px; border: 1px solid var(--border); border-radius: 14px; background: var(--bg); display: flex; flex-direction: column; align-items: var(--media-align, flex-start); gap: 10px; break-inside: avoid; page-break-inside: avoid; }
.mby-figcaption { width: 100%; font-size: 0.92em; line-height: 1.5; color: var(--muted); text-align: var(--content-align, left); padding: 0 6px 2px; }
.mby-figcaption p { margin: 0.15em 0; }
.mby-zoom-modal { position: fixed; inset: 0; background: rgba(0, 0, 0, 0.94); display: flex; flex-direction: column; z-index: 60; }
.mby-zoom-topbar { display: flex; align-items: center; justify-content: space-between; padding: 14px 20px; z-index: 2; }
.mby-zoom-pill { padding: 6px 12px; border-radius: 999px; background: rgba(255, 255, 255, 0.14); color: #ffffff; font-size: 13px; font-weight: 600; }
.mby-zoom-actions { display: flex; gap: 8px; }
.mby-zoom-btn { padding: 6px 12px; border-radius: 8px; border: 1px solid rgba(255, 255, 255, 0.22); background: rgba(255, 255, 255, 0.12); color: #ffffff; font-weight: 700; cursor: pointer; }
.mby-zoom-stage { flex: 1; min-height: 0; display: flex; align-items: center; justify-content: center; overflow: hidden; cursor: grab; }
.mby-zoom-stage:active { cursor: grabbing; }
.mby-zoom-img { max-width: 90vw; max-height: 82vh; object-fit: contain; user-select: none; transform-origin: center center; }
""";

    private static string BuildMediaZoomJs() => $$"""
(function (MBY) {
const MAGNIFIER_ICON_SVG = '<svg class="mby-zoom-icon" viewBox="0 0 24 24" fill="none" stroke="#ffffff" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><use href="#mby-ico-zoom"/></svg>';
function createZoomableImageNode(src, onZoom, onLoad, alt) {
  const wrap = mk('div', 'mby-media-wrap' + (onZoom ? '' : ' is-static'));
  const img = mk('img', 'mby-media-img');
  img.decoding = 'async';
  img.loading = 'lazy';
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
  let zoomScale = 1, zoomTx = 0, zoomTy = 0, isDraggingZoom = false, dragStartX = 0, dragStartY = 0;

  function updateZoomTransform() {
    const zoomTargetImg = document.getElementById('zoomTargetImg');
    const zoomPercentBadge = document.getElementById('zoomPercentBadge');
    const zoomResetBtn = document.getElementById('zoomResetBtn');
    if (zoomScale <= 1) { zoomScale = 1; zoomTx = 0; zoomTy = 0; }
    if (zoomTargetImg) zoomTargetImg.style.transform = `translate(${zoomTx}px, ${zoomTy}px) scale(${zoomScale})`;
    if (zoomPercentBadge) zoomPercentBadge.textContent = `${Math.round(zoomScale * 100)}%`;
    if (zoomResetBtn) zoomResetBtn.classList.toggle('hidden', zoomScale <= 1.01);
  }

  function openZoomLightbox(src, alt) {
    const imageZoomModal = document.getElementById('imageZoomModal');
    const zoomTargetImg = document.getElementById('zoomTargetImg');
    if (!imageZoomModal || !zoomTargetImg) return;
    zoomTargetImg.src = src;
    zoomTargetImg.alt = alt || 'Zoomed diagram';
    zoomScale = 1; zoomTx = 0; zoomTy = 0;
    updateZoomTransform();
    imageZoomModal.classList.remove('hidden');
  }

  function closeZoomLightbox() {
    const imageZoomModal = document.getElementById('imageZoomModal');
    if (imageZoomModal) imageZoomModal.classList.add('hidden');
  }

  function isZoomOpen() {
    const imageZoomModal = document.getElementById('imageZoomModal');
    return !!(imageZoomModal && !imageZoomModal.classList.contains('hidden'));
  }

  function initZoom() {
    const zoomInBtn = document.getElementById('zoomInBtn');
    const zoomOutBtn = document.getElementById('zoomOutBtn');
    const zoomResetBtn = document.getElementById('zoomResetBtn');
    const zoomCloseBtn = document.getElementById('zoomCloseBtn');
    const zoomStage = document.getElementById('zoomStage');
    zoomInBtn?.addEventListener('click', () => { zoomScale = Math.min(5, zoomScale + 0.5); updateZoomTransform(); });
    zoomOutBtn?.addEventListener('click', () => { zoomScale = Math.max(1, zoomScale - 0.5); updateZoomTransform(); });
    zoomResetBtn?.addEventListener('click', () => { zoomScale = 1; updateZoomTransform(); });
    zoomCloseBtn?.addEventListener('click', closeZoomLightbox);
    if (zoomStage) {
      zoomStage.addEventListener('wheel', e => { e.preventDefault(); zoomScale = Math.max(1, Math.min(5, zoomScale + (e.deltaY < 0 ? 0.25 : -0.25))); updateZoomTransform(); }, { passive: false });
      zoomStage.addEventListener('dblclick', () => { zoomScale = zoomScale > 1.05 ? 1 : 2.5; updateZoomTransform(); });
      zoomStage.addEventListener('pointerdown', e => { if (zoomScale <= 1) return; isDraggingZoom = true; dragStartX = e.clientX - zoomTx; dragStartY = e.clientY - zoomTy; zoomStage.setPointerCapture(e.pointerId); });
      zoomStage.addEventListener('pointermove', e => { if (!isDraggingZoom) return; zoomTx = e.clientX - dragStartX; zoomTy = e.clientY - dragStartY; updateZoomTransform(); });
      zoomStage.addEventListener('pointerup', () => { isDraggingZoom = false; });
    }
  }

  MBY.helpers.initZoom = initZoom;
  MBY.helpers.makeZoomImg = (src, alt, ctx) => createZoomableImageNode(src, openZoomLightbox, () => requestAnimationFrame(() => ctx?.updateScrollDownHint?.()), alt);
  MBY.helpers.openZoomLightbox = openZoomLightbox;
  MBY.helpers.closeZoomLightbox = closeZoomLightbox;
  MBY.helpers.isZoomOpen = isZoomOpen;
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    private const string LinksCss = """
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

    private const string LinksJs = """
(function (MBY) {
  const linkCardCache = new Map();
  const inSessionArticleIndex = new Map();
  const inSessionArticleExcerptCache = new Map();
  const externalSessionPreviewCache = new Map();
  const loadedPreviewDirs = new Set();
  let activePopoverAnchor = null, popoverHideTimer = null;

  function getArticlePreview(target, ctx) {
    const key = String(target || '').toLowerCase();
    if (inSessionArticleExcerptCache.has(key)) return inSessionArticleExcerptCache.get(key);
    const meta = inSessionArticleIndex.get(key);
    if (!meta) return null;
    const plain = ctx?.htmlToPlainText || (h => String(h || ''));
    const s = meta.slide;
    const parts = [];
    (s.articleBlocks || []).forEach(b => {
      if (b.title) parts.push(b.title);
      if (b.textHtml) parts.push(plain(b.textHtml));
      if (b.table) {
        if (b.table.title) parts.push(b.table.title);
        (b.table.headersHtml || []).forEach(h => parts.push(plain(h)));
        (b.table.rowsHtml || []).forEach(r => r.forEach(c => parts.push(plain(c))));
      }
      if (b.diagramExplore) {
        if (b.diagramExplore.title) parts.push(b.diagramExplore.title);
        (b.diagramExplore.hotspots || []).forEach(hs => parts.push(hs.label + ': ' + plain(hs.noteHtml)));
      }
      if (b.reveal) {
        if (b.reveal.title) parts.push(b.reveal.title);
        (b.reveal.steps || []).forEach(st => parts.push(plain(st.headingHtml) + ' ' + plain(st.bodyHtml)));
      }
    });
    const rawExcerpt = parts.join(' ').replace(/\s+/g, ' ').trim();
    const excerpt = rawExcerpt.length > 520 ? (rawExcerpt.slice(0, 519).trimEnd() + '…') : rawExcerpt;
    const entry = { slideIdx: meta.slideIdx, title: meta.title, excerpt };
    inSessionArticleExcerptCache.set(key, entry);
    return entry;
  }

  window.__mbyRegisterSessionPreview = function(payload) {
    if (!payload || !payload.sessionId) return;
    externalSessionPreviewCache.set(payload.sessionId, payload);
  };

  function findArticleSlideIndex(slides, target) {
    if (!target || !Array.isArray(slides)) return -1;
    const t = String(target).trim().toLowerCase();
    for (let i = 0; i < slides.length; i++) {
      const s = slides[i];
      if (s.kind !== 'article') continue;
      if (s.id && s.id.toLowerCase() === t) return i;
      if (s.refKey && s.refKey.toLowerCase() === t) return i;
      if (`c${s.componentIndex || (i + 1)}` === t) return i;
    }
    return -1;
  }

  function hideLinkPopoverImmediate() {
    if (popoverHideTimer) { clearTimeout(popoverHideTimer); popoverHideTimer = null; }
    activePopoverAnchor = null;
    const pop = document.getElementById('linkPreviewPopover');
    if (pop) pop.classList.add('hidden');
  }

  function scheduleHideLinkPopover() {
    if (popoverHideTimer) clearTimeout(popoverHideTimer);
    popoverHideTimer = setTimeout(hideLinkPopoverImmediate, 160);
  }

  function positionLinkPopover(pop, anchor) {
    if (!pop || !anchor) return;
    const r = anchor.getBoundingClientRect();
    const pw = Math.min(370, window.innerWidth - 24);
    const ph = Math.max(120, pop.offsetHeight || 200);
    const left = Math.max(12, Math.min(r.left, window.innerWidth - pw - 12));
    let top = r.bottom + 8;
    if (top + ph > window.innerHeight - 12 && r.top - ph - 8 > 12) {
      top = r.top - ph - 8;
    }
    pop.style.left = left + 'px';
    pop.style.top = Math.max(12, top) + 'px';
  }

  function showLinkPopover(anchor, ctx) {
    const pop = document.getElementById('linkPreviewPopover');
    if (!pop || !anchor) return;
    if (popoverHideTimer) { clearTimeout(popoverHideTimer); popoverHideTimer = null; }
    activePopoverAnchor = anchor;
    const kind = anchor.dataset.mbyKind;
    const target = anchor.dataset.mbyTarget || '';
    const sub = anchor.dataset.mbySub || '';
    pop.innerHTML = '';

    const head = mk('div', 'mby-link-popover-head');
    if (kind === 'card') {
      const card = linkCardCache.get(target.toLowerCase());
      head.appendChild(mk('span', 'mby-link-popover-badge', 'Reference Card'));
      pop.appendChild(head);
      pop.appendChild(mk('div', 'mby-link-popover-title', card?.title || anchor.textContent || target));
      if (card?.imagePath) {
        const img = mk('img', 'mby-link-popover-img');
        img.src = card.imagePath;
        img.alt = card.title || anchor.textContent || 'Reference image';
        img.addEventListener('click', () => MBY.helpers.openZoomLightbox?.(card.imagePath, card.title || img.alt));
        pop.appendChild(img);
      }
      if (card?.textHtml) {
        const bodyEl = mk('div', 'mby-link-popover-body');
        bodyEl.innerHTML = card.textHtml;
        ctx?.renderMathIn?.(bodyEl);
        pop.appendChild(bodyEl);
      }
    } else if (kind === 'article') {
      const art = getArticlePreview(target, ctx);
      head.appendChild(mk('span', 'mby-link-popover-badge', 'Article Preview'));
      pop.appendChild(head);
      pop.appendChild(mk('div', 'mby-link-popover-title', art?.title || anchor.textContent || target));
      const ex = mk('div', 'mby-link-popover-excerpt', art?.excerpt || 'In-session article reference.');
      pop.appendChild(ex);
      pop.appendChild(mk('div', 'mby-link-popover-footer', null, [mk('span', null, 'Click link to open article in deck')]));
    } else if (kind === 'session') {
      const ext = externalSessionPreviewCache.get(target);
      head.appendChild(mk('span', 'mby-link-popover-badge', 'External Session'));
      pop.appendChild(head);
      let previewTitle = ext?.title || (anchor.textContent || target);
      let previewText = ext?.description || 'Cross-session study reference. Preview loads automatically once the target session is built.';
      if (ext && sub) {
        const subLower = sub.toLowerCase();
        const matchedArt = (ext.articles || []).find(a =>
          (a.refKey && a.refKey.toLowerCase() === subLower) ||
          (a.id && a.id.toLowerCase() === subLower) ||
          `c${a.index}` === subLower);
        if (matchedArt) {
          previewTitle = `${ext.title} — ${matchedArt.title}`;
          previewText = matchedArt.excerpt || previewText;
        }
      } else if (ext && !previewText && ext.articles?.length) {
        previewText = ext.articles.map(a => a.title + (a.excerpt ? ': ' + a.excerpt : '')).join(' • ');
      }
      pop.appendChild(mk('div', 'mby-link-popover-title', previewTitle));
      pop.appendChild(mk('div', 'mby-link-popover-excerpt', previewText));
      pop.appendChild(mk('div', 'mby-link-popover-footer', null, [mk('span', null, ext ? 'Click link to open session' : 'Target session link ready (not yet built)')]));
    }

    pop.classList.remove('hidden');
    requestAnimationFrame(() => positionLinkPopover(pop, anchor));
  }

  function initLinks(ctx) {
    const data = ctx?.DATA || {};
    Object.values(data.linkCards || {}).forEach(c => {
      if (c && c.id) linkCardCache.set(String(c.id).toLowerCase(), c);
    });

    const previewDirs = new Set();
    const scanHtmlForPreviewDirs = html => {
      if (!html || html.indexOf('data-mby-preview-dir=') < 0) return;
      const re = /data-mby-preview-dir="([^"]+)"/g;
      let m;
      while ((m = re.exec(html)) !== null) {
        if (m[1]) previewDirs.add(m[1]);
      }
    };

    (data.slides || []).forEach((s, slideIdx) => {
      if (s.kind !== 'article') return;
      const cIdx = s.componentIndex || (slideIdx + 1);
      const entry = { slideIdx, slide: s, title: s.title || `Article ${cIdx}` };
      if (s.id) inSessionArticleIndex.set(String(s.id).toLowerCase(), entry);
      if (s.refKey) inSessionArticleIndex.set(String(s.refKey).toLowerCase(), entry);
      inSessionArticleIndex.set(`c${cIdx}`, entry);
      (s.articleBlocks || []).forEach(b => {
        scanHtmlForPreviewDirs(b.textHtml);
        scanHtmlForPreviewDirs(b.captionHtml);
        scanHtmlForPreviewDirs(b.titleHtml);
        if (b.table) {
          (b.table.headersHtml || []).forEach(scanHtmlForPreviewDirs);
          (b.table.rowsHtml || []).forEach(r => r.forEach(scanHtmlForPreviewDirs));
          scanHtmlForPreviewDirs(b.table.captionHtml);
          scanHtmlForPreviewDirs(b.table.footnoteHtml);
        }
        if (b.diagramExplore) {
          (b.diagramExplore.hotspots || []).forEach(hs => scanHtmlForPreviewDirs(hs.noteHtml));
        }
        if (b.reveal) {
          (b.reveal.steps || []).forEach(st => { scanHtmlForPreviewDirs(st.headingHtml); scanHtmlForPreviewDirs(st.bodyHtml); });
        }
      });
    });

    Object.entries(data.externalPreviews || {}).forEach(([sid, payload]) => {
      if (payload) externalSessionPreviewCache.set(sid, payload);
    });

    previewDirs.forEach(dir => {
      if (!dir || loadedPreviewDirs.has(dir)) return;
      loadedPreviewDirs.add(dir);
      const sc = document.createElement('script');
      sc.async = true;
      sc.src = dir.replace(/\/+$/, '') + '/preview-index.js';
      sc.onerror = () => {};
      document.head.appendChild(sc);
    });

    document.addEventListener('mouseover', e => {
      const link = e.target?.closest?.('.mby-native-link');
      if (link) { showLinkPopover(link, ctx); return; }
      const pop = document.getElementById('linkPreviewPopover');
      if (pop && pop.contains(e.target) && popoverHideTimer) {
        clearTimeout(popoverHideTimer);
        popoverHideTimer = null;
      }
    });
    document.addEventListener('mouseout', e => {
      const link = e.target?.closest?.('.mby-native-link');
      const pop = document.getElementById('linkPreviewPopover');
      const inPop = pop && pop.contains(e.target);
      if ((link || inPop) && !e.relatedTarget?.closest?.('.mby-native-link') && !pop?.contains(e.relatedTarget)) {
        scheduleHideLinkPopover();
      }
    });
    document.addEventListener('focusin', e => {
      const link = e.target?.closest?.('.mby-native-link');
      if (link) showLinkPopover(link, ctx);
    });
    document.addEventListener('focusout', e => {
      const pop = document.getElementById('linkPreviewPopover');
      if (activePopoverAnchor && !pop?.contains(e.relatedTarget) && !e.relatedTarget?.closest?.('.mby-native-link')) {
        scheduleHideLinkPopover();
      }
    });
    document.addEventListener('click', e => {
      const link = e.target?.closest?.('.mby-native-link');
      if (!link) return;
      const kind = link.dataset.mbyKind;
      if (kind === 'card') {
        e.preventDefault();
        showLinkPopover(link, ctx);
      } else if (kind === 'article') {
        e.preventDefault();
        const idx = findArticleSlideIndex(data.slides, link.dataset.mbyTarget || '');
        hideLinkPopoverImmediate();
        if (idx >= 0) ctx?.goToSlide?.(idx);
      }
    });
  }

  function applyInitialArticleHashRoute(ctx) {
    const hash = window.location.hash || '';
    const m = hash.match(/article=([^&]+)/i);
    if (!m) return;
    let target = m[1];
    try { target = decodeURIComponent(target); } catch {}
    const idx = findArticleSlideIndex(ctx?.DATA?.slides, target);
    if (idx >= 0) {
      ctx?.greetingScreen?.classList.add('hidden');
      ctx?.playerTopbar?.classList.remove('hidden');
      ctx?.playerViewport?.classList.remove('hidden');
      ctx?.playerControls?.classList.remove('hidden');
      ctx?.setSessionStarted?.();
      ctx?.goToSlide?.(idx);
    }
  }

  MBY.helpers.initLinks = initLinks;
  MBY.helpers.hideLinkPopover = hideLinkPopoverImmediate;
  MBY.helpers.isLinkPopoverOpen = () => {
    const pop = document.getElementById('linkPreviewPopover');
    return !!(pop && !pop.classList.contains('hidden'));
  };
  MBY.helpers.applyInitialArticleHashRoute = applyInitialArticleHashRoute;
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    private const string QuizBundleCss = """
.mby-quiz-section-title { font-size: 15px; font-weight: 750; color: var(--text); text-align: center; }
.mby-quiz-bundle { width: 100%; max-width: 100%; display: flex; flex-direction: column; align-items: center; gap: 14px; }
.mby-quiz-pills { display: flex; flex-wrap: wrap; gap: 8px; justify-content: center; align-items: center; }
.mby-q-pill { min-width: 38px; height: 38px; padding: 0 10px; border-radius: 10px; border: 1px solid var(--border); background: var(--surface); color: var(--text); font-size: 12px; font-weight: 700; display: inline-flex; align-items: center; justify-content: center; cursor: pointer; transition: background 140ms ease, border-color 140ms ease, color 140ms ease; }
.mby-q-pill.answered { background: var(--accent-soft); border-color: var(--accent); color: var(--accent); }
.mby-q-pill.active { background: var(--accent); border-color: var(--accent); color: #ffffff; }
.mby-quiz-viewport { width: 100%; overflow: hidden; transition: height var(--slide-ms) cubic-bezier(0.22, 1, 0.36, 1); }
.mby-quiz-track { display: flex; width: 100%; transition: transform var(--slide-ms) cubic-bezier(0.22, 1, 0.36, 1); align-items: flex-start; }
.mby-quiz-q-slide { min-width: 100%; width: 100%; padding: 0; display: flex; justify-content: center; align-items: flex-start; }
""";

    private const string QuizBundleJs = """
(function (MBY) {
  MBY.helpers.buildBundledQuiz = function (quizSlide, ctx) {
    const baseProfileKey = quizSlide.questions?.[0]?.kind === 'flashcard' ? 'flashcard' : 'mcq';
    const wrap = mk('div', 'mby-quiz-bundle'); applySlideLayoutVars(wrap, resolveSlideLayout(quizSlide, baseProfileKey));
    const questions = quizSlide.questions || [];
    if (!ctx.activeQuizQuestionIdx.has(quizSlide.id)) ctx.activeQuizQuestionIdx.set(quizSlide.id, 0);
    const pillsRow = mk('div', 'mby-quiz-pills'), qTrack = mk('div', 'mby-quiz-track'), qViewport = mk('div', 'mby-quiz-viewport', null, [qTrack]);
    const qSlides = [];
    const hydratedQuestions = new Set();
    const pillButtons = questions.map((_, idx) => {
      const pill = mk('button', 'mby-q-pill', 'Q' + (idx + 1)); pill.type = 'button';
      pill.addEventListener('click', () => selectQuestion(idx));
      pillsRow.appendChild(pill); return pill;
    });

    function syncQuizViewportHeight(qIdx) {
      const cardEl = qSlides[qIdx]?.firstElementChild || qSlides[qIdx];
      const h = cardEl ? Math.ceil(cardEl.getBoundingClientRect().height) : 0;
      if (h > 0) qViewport.style.height = h + 'px';
    }
    function refreshPills(activeQIdx) {
      questions.forEach((q, idx) => {
        const answered = (q.kind === 'mcq' && ctx.mcqStateMap.get(q.id)?.checked) ||
          (q.kind === 'flashcard' && !!ctx.flashcardStateMap.get(q.id)?.rating) ||
          (ctx.isGradedKind(q.kind) && q.kind !== 'mcq' && ctx.gradedStateMap.get(q.id)?.checked);
        pillButtons[idx].classList.toggle('active', idx === activeQIdx);
        pillButtons[idx].classList.toggle('answered', idx !== activeQIdx && answered);
      });
    }

    questions.forEach(() => {
      const qSlide = mk('div', 'mby-quiz-q-slide');
      qSlides.push(qSlide);
      qTrack.appendChild(qSlide);
    });

    function ensureQuestionRendered(qIdx, eagerImages = false) {
      if (qIdx < 0 || qIdx >= questions.length) return;
      const qSlide = qSlides[qIdx];
      if (!hydratedQuestions.has(qIdx)) {
        hydratedQuestions.add(qIdx);
        const q = questions[qIdx];
        const onItemChanged = () => {
          const activeQ = ctx.activeQuizQuestionIdx.get(quizSlide.id) || 0;
          refreshPills(activeQ);
          requestAnimationFrame(() => { if (!qSlides[activeQ]?.firstElementChild?.classList.contains('is-flipping')) syncQuizViewportHeight(activeQ); ctx.updateScrollDownHint(); });
        };
        const onFlipStart = (h, ms) => { qViewport.style.transition = 'height ' + ms + 'ms cubic-bezier(0.4, 0, 0.2, 1)'; if (h > 0) qViewport.style.height = h + 'px'; };
        const onFlipEnd = () => { qViewport.style.transition = ''; syncQuizViewportHeight(ctx.activeQuizQuestionIdx.get(quizSlide.id) || 0); };
        if (q) qSlide.appendChild(ctx.renderSingleQuestionWidget(q, onItemChanged, onFlipStart, onFlipEnd));
      }
      if (eagerImages && qSlide) {
        qSlide.querySelectorAll('img').forEach(img => { img.loading = 'eager'; });
      }
    }

    const ro = typeof ResizeObserver !== 'undefined'
      ? new ResizeObserver(() => {
          const activeQ = ctx.activeQuizQuestionIdx.get(quizSlide.id) || 0;
          if (!qSlides[activeQ]?.firstElementChild?.classList.contains('is-flipping')) syncQuizViewportHeight(activeQ);
          if (ctx.DATA.slides[ctx.getCurrentIndex()]?.id === quizSlide.id) ctx.updateScrollDownHint();
        })
      : null;

    qTrack.addEventListener('transitionend', e => { if (e.target === qTrack && e.propertyName === 'transform') requestAnimationFrame(ctx.updateScrollDownHint); });
    qViewport.addEventListener('transitionend', e => { if (e.target === qViewport && e.propertyName === 'height') requestAnimationFrame(ctx.updateScrollDownHint); });

    function selectQuestion(qIdx, opts = { recordVisit: true, flushPrevious: true, scrollToTop: true }) {
      if (questions.length === 0) return;
      const { recordVisit = true, flushPrevious = true, scrollToTop = true } = opts || {};
      ctx.closePrevCompMenu();
      if (flushPrevious) ctx.flushActiveLeafTime();
      const clamped = Math.max(0, Math.min(qIdx, questions.length - 1));
      ensureQuestionRendered(clamped, true);
      if (ro) {
        ro.disconnect();
        const activeCard = qSlides[clamped]?.firstElementChild;
        if (activeCard) ro.observe(activeCard);
      }
      ctx.activeQuizQuestionIdx.set(quizSlide.id, clamped);
      if (recordVisit && ctx.greetingScreen.classList.contains('hidden') && ctx.DATA.slides[ctx.getCurrentIndex()]?.id === quizSlide.id) {
        ctx.enterActiveLeaf();
      }
      qTrack.style.transform = 'translateX(-' + (clamped * 100) + '%)';
      refreshPills(clamped); ctx.updateNavButtonsUi();
      if (scrollToTop) ctx.scrollSlideToTop(wrap.closest('.mby-slide'));
      requestAnimationFrame(() => { syncQuizViewportHeight(clamped); ctx.updateScrollDownHint(); });
      if (clamped + 1 < questions.length) {
        const idle = window.requestIdleCallback || (fn => setTimeout(fn, 120));
        idle(() => ensureQuestionRendered(clamped + 1, false));
      }
    }

    ctx.quizControllers.set(quizSlide.id, { selectQuestion, syncHeight: () => syncQuizViewportHeight(ctx.activeQuizQuestionIdx.get(quizSlide.id) || 0), getQuestionCount: () => questions.length });
    if (quizSlide.title) wrap.appendChild(mk('div', 'mby-quiz-section-title', quizSlide.title));
    wrap.appendChild(pillsRow); wrap.appendChild(qViewport);
    selectQuestion(ctx.activeQuizQuestionIdx.get(quizSlide.id) || 0, { recordVisit: false, flushPrevious: false, scrollToTop: false });
    return wrap;
  };
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    private const string GradedCoreCss = """
.mby-prompt-media { margin-bottom: 16px; }
.mby-action-row { margin-top: 20px; display: flex; flex-wrap: wrap; gap: 10px; align-items: center; }
.mby-check-btn { display: inline-flex; align-items: center; justify-content: center; gap: 6px; padding: 10px 18px; border-radius: 10px; border: 1px solid var(--accent); background: var(--accent); color: #ffffff; font-weight: 600; cursor: pointer; user-select: none; }
.mby-explanation-box { margin-top: 16px; padding: 14px 16px; border-radius: 12px; border: 1px solid var(--border); border-left: 4px solid var(--accent); background: var(--bg); font-size: 0.95em; line-height: 1.5; }
.mby-explanation-title { font-size: 11px; font-weight: 800; letter-spacing: 0.04em; text-transform: uppercase; color: var(--accent); margin-bottom: 6px; }
.mby-override-btn { padding: 7px 13px; border-radius: 9px; border: 1.5px solid var(--ok); background: rgba(25, 135, 84, 0.1); color: var(--ok); font-size: 13px; font-weight: 700; cursor: pointer; }
.mby-mech-badge { min-width: 26px; height: 26px; border-radius: 8px; background: var(--accent-soft); color: var(--accent); font-size: 12px; font-weight: 800; display: inline-flex; align-items: center; justify-content: center; flex-shrink: 0; }
.mby-diff-title { font-size: var(--prompt-font-size, 22px); font-weight: 700; line-height: 1.4; }
.mby-diff-fix-badge { display: inline-block; margin-top: 5px; padding: 2px 8px; border-radius: 6px; background: rgba(25, 135, 84, 0.16); color: var(--ok); font-size: 12px; font-weight: 700; }
""";

    private static string BuildGradedCoreJs() => $$"""
(function (MBY) {
{{EditorGradedFeedbackJs}}
  function createRetryBtn(onRetry, visible, label = 'Retry', compact = false) {
    const btn = mk('button', 'mby-retry-btn' + (compact ? ' is-compact' : '') + (visible ? '' : ' hidden'), label);
    btn.type = 'button';
    btn.addEventListener('click', () => { if (onRetry) onRetry(); });
    return btn;
  }
  function createExplanationBox(html, initiallyVisible = false, ctx = null) {
    if (!html) return null;
    const content = mk('div');
    content.innerHTML = html;
    (ctx?.renderMathIn || MBY.helpers.renderMathIn)?.(content);
    return mk('div', 'mby-explanation-box' + (initiallyVisible ? '' : ' hidden'), null, [mk('div', 'mby-explanation-title', 'Explanation'), content]);
  }
  function recordGradeAttempt(m, isCorrect) {
    if (!m) return;
    m.checkAttempts = (m.checkAttempts || 0) + 1;
    if (m.firstTryCorrect === null) m.firstTryCorrect = isCorrect;
    m.isCorrect = isCorrect;
  }
  function setFeedbackMessage(el, text, isCorrect) {
    el.textContent = text;
    el.style.color = isCorrect ? 'var(--ok)' : 'var(--bad)';
    el.style.fontWeight = '600';
  }

  MBY.helpers.createRetryBtn = createRetryBtn;
  MBY.helpers.createExplanationBox = createExplanationBox;
  MBY.helpers.recordGradeAttempt = recordGradeAttempt;
  MBY.helpers.setFeedbackMessage = setFeedbackMessage;
  MBY.helpers.renderMcqFeedback = renderMcqFeedback;
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    private const string RatingRowCss = """
.mby-rating-row { display: flex; gap: 12px; margin-top: 8px; justify-content: center; }
.mby-rate-btn { padding: 9px 18px; border-radius: 8px; border: 1.5px solid var(--border); background: transparent; color: var(--text); font-weight: 600; cursor: pointer; }
.mby-rate-btn.active-known { border-color: var(--ok); background: rgba(25, 135, 84, 0.14); color: var(--ok); }
.mby-rate-btn.active-review { border-color: var(--bad); background: rgba(220, 53, 69, 0.14); color: var(--bad); }
""";

    private const string DndShuffleCss = """
.mby-draggable { cursor: grab; }
.mby-draggable:active { cursor: grabbing; }
.is-dragging { opacity: 0.45; cursor: grabbing; }
.mby-drop-over { border-color: var(--select) !important; background: var(--select-soft) !important; box-shadow: 0 0 0 2px var(--select-soft); }
""";

    private const string DndShuffleJs = """
(function (MBY) {
  function bindDropTarget(el, mimeTypes, onDrop, checkContains = false) {
    const accepted = Array.isArray(mimeTypes) ? mimeTypes : [mimeTypes];
    el.addEventListener('dragover', e => {
      if (e.dataTransfer && accepted.some(t => e.dataTransfer.types.includes(t))) {
        e.preventDefault();
        el.classList.add('mby-drop-over');
      }
    });
    el.addEventListener('dragleave', e => {
      if (!checkContains || !el.contains(e.relatedTarget)) el.classList.remove('mby-drop-over');
    });
    el.addEventListener('drop', e => {
      e.preventDefault();
      el.classList.remove('mby-drop-over');
      onDrop(e);
    });
  }

  function deterministicShuffle(arr, seedStr, keyFn) {
    const out = arr.slice();
    if (out.length < 2) return out;
    let h = 2166136261 >>> 0;
    for (let i = 0; i < seedStr.length; i++) { h ^= seedStr.charCodeAt(i); h = Math.imul(h, 16777619) >>> 0; }
    const nextRand = () => { h = (Math.imul(1664525, h) + 1013904223) >>> 0; return h / 4294967296; };
    for (let i = out.length - 1; i > 0; i--) {
      const j = Math.floor(nextRand() * (i + 1));
      const tmp = out[i]; out[i] = out[j]; out[j] = tmp;
    }
    if (keyFn && new Set(out.map(keyFn)).size >= 2) {
      let same = true;
      for (let i = 0; i < out.length; i++) { if (keyFn(out[i]) !== keyFn(arr[i])) { same = false; break; } }
      if (same) { const tmp = out[0]; out[0] = out[1]; out[1] = tmp; }
    }
    return out;
  }

  MBY.helpers.bindDropTarget = bindDropTarget;
  MBY.helpers.deterministicShuffle = deterministicShuffle;
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    public static IReadOnlyList<WebComponentModule> Modules { get; } =
    [
        new("media-zoom", "aux", f => f.HasImages, MediaZoomCss, BuildMediaZoomJs()),
        new("links", "aux", f => f.HasNativeLinks, LinksCss, LinksJs),
        new("quiz-bundle", "aux", f => f.HasQuizBundle, QuizBundleCss, QuizBundleJs),
        new("graded-core", "aux", f => f.HasAnyGradedQuestion, GradedCoreCss, BuildGradedCoreJs()),
        new("rating-row", "aux", f => f.HasRatingRow, RatingRowCss, null),
        new("dnd-shuffle", "aux", f => f.HasDragAndDrop, DndShuffleCss, DndShuffleJs)
    ];
}
