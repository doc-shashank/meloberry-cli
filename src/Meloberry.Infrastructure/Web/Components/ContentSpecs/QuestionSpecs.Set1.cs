namespace Meloberry.Infrastructure.Web.Components.ContentSpecs;

internal static class QuestionSpecsSet1
{
    private const string ChoiceGridCss = """
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
""";

    private const string McqJs = """
(function (MBY) {
  function buildMcqCard(slide, ctx, onAnswered) {
    const m = ctx.metricById.get(slide.id), mcq = slide.mcq;
    const card = mk('div', 'mby-card', null, [createUpperTab(mcq.isMultiCorrect ? 'MCQ (MULTI-CORRECT)' : 'MCQ')]);
    applySlideLayoutVars(card, resolveSlideLayout(slide, 'mcq'));
    const prompt = mk('div', 'mby-prompt'); prompt.innerHTML = mcq.promptHtml; ctx.renderMathIn(prompt);
    const promptMedia = mcq.imagePath ? mk('div', 'mby-prompt-media', null, [ctx.makeZoomImg(mcq.imagePath)]) : null;
    const grid = mk('div', 'mby-options-grid');
    if (!ctx.mcqStateMap.has(slide.id)) ctx.mcqStateMap.set(slide.id, { selected: new Set(), checked: false, isCorrect: null });
    const st = ctx.mcqStateMap.get(slide.id);

    const optionBtns = mcq.options.map(opt => {
      const btn = mk('button', 'mby-option-btn'); btn.type = 'button';
      if (st.selected.has(opt.index)) btn.classList.add('selected');
      if (st.checked) btn.classList.add(opt.isCorrect ? 'correct' : (st.selected.has(opt.index) ? 'wrong' : 'x'));
      btn.classList.remove('x');
      if (opt.imagePath) btn.appendChild(mk('div', 'mby-option-media', null, [ctx.makeZoomImg(opt.imagePath)]));
      if (opt.textHtml) { const t = mk('div', 'mby-option-text'); t.innerHTML = opt.textHtml; ctx.renderMathIn(t); btn.appendChild(t); }
      btn.addEventListener('click', () => {
        if (!mcq.isMultiCorrect) { st.selected.clear(); optionBtns.forEach(b => b.classList.remove('selected')); }
        if (st.selected.has(opt.index)) { st.selected.delete(opt.index); btn.classList.remove('selected'); }
        else { st.selected.add(opt.index); btn.classList.add('selected'); }
      });
      grid.appendChild(btn); return btn;
    });

    const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
    const feedback = mk('span'); if (st.checked) MBY.helpers.renderMcqFeedback(feedback, st.isCorrect);
    const explanationBox = MBY.helpers.createExplanationBox(mcq.explanationHtml, st.checked, ctx);
    const retryBtn = MBY.helpers.createRetryBtn(() => {
      st.selected.clear();
      st.checked = false;
      st.isCorrect = null;
      optionBtns.forEach(b => b.classList.remove('selected', 'correct', 'wrong'));
      feedback.textContent = '';
      if (explanationBox) explanationBox.classList.add('hidden');
      retryBtn.classList.add('hidden');
      if (m) m.isCorrect = null;
      if (onAnswered) onAnswered();
      requestAnimationFrame(ctx.updateScrollDownHint);
    }, st.checked);

    checkBtn.addEventListener('click', () => {
      let allCorrect = true;
      optionBtns.forEach((btn, i) => {
        const opt = mcq.options[i], picked = st.selected.has(opt.index);
        btn.classList.remove('correct', 'wrong');
        if (opt.isCorrect) btn.classList.add('correct'); else if (picked) btn.classList.add('wrong');
        if (picked !== opt.isCorrect) allCorrect = false;
      });
      st.checked = true; st.isCorrect = allCorrect;
      if (m) m.selectedOptionIndices = Array.from(st.selected).sort((a, b) => a - b);
      MBY.helpers.recordGradeAttempt(m, allCorrect);
      MBY.helpers.renderMcqFeedback(feedback, allCorrect);
      retryBtn.classList.remove('hidden');
      if (explanationBox) explanationBox.classList.remove('hidden');
      if (onAnswered) onAnswered();
      requestAnimationFrame(ctx.updateScrollDownHint);
    });

    card.appendChild(mk('div', 'mby-card-body', null, [prompt, promptMedia, grid, mk('div', 'mby-action-row', null, [checkBtn, retryBtn, feedback]), explanationBox]));
    return card;
  }

  MBY.gradedKinds['mcq'] = true;
  MBY.kindLabels['mcq'] = 'MCQ';
  MBY.questions['mcq'] = buildMcqCard;
  MBY.search['mcq'] = (q, parts, ctx) => {
    if (!q.mcq) return;
    parts.push(ctx.htmlToPlainText(q.mcq.promptHtml));
    q.mcq.options.forEach(o => { if (o.textHtml) parts.push(ctx.htmlToPlainText(o.textHtml)); });
    if (q.mcq.explanationHtml) parts.push(ctx.htmlToPlainText(q.mcq.explanationHtml));
  };
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    private const string AssertionReasonCss = """
.mby-ar-stack { display: flex; flex-direction: column; gap: 10px; margin-bottom: 18px; }
.mby-ar-box { padding: 12px 16px; border-radius: 12px; border: 1px solid var(--border); background: var(--bg); }
.mby-ar-label { font-size: 11px; font-weight: 800; letter-spacing: 0.04em; text-transform: uppercase; color: var(--accent); margin-bottom: 4px; }
.mby-ar-text { font-size: 1.02em; line-height: 1.5; }
.mby-ar-text p:first-child { margin-top: 0; }
.mby-ar-text p:last-child { margin-bottom: 0; }
""";

    private const string AssertionReasonJs = """
(function (MBY) {
  const AR_OPTIONS_4 = [
    { code: 'both-explain', label: 'Both Assertion (A) and Reason (R) are true, and R is the correct explanation of A.' },
    { code: 'both-no-explain', label: 'Both Assertion (A) and Reason (R) are true, but R is NOT the correct explanation of A.' },
    { code: 'a-true-r-false', label: 'Assertion (A) is true, but Reason (R) is false.' },
    { code: 'a-false-r-true', label: 'Assertion (A) is false, but Reason (R) is true.' }
  ];
  const AR_OPTIONS_5 = [...AR_OPTIONS_4, { code: 'both-false', label: 'Both Assertion (A) and Reason (R) are false.' }];

  function buildAssertionReasonCard(slide, ctx, onAnswered) {
    const m = ctx.metricById.get(slide.id), ar = slide.assertionReason;
    const options = ar.options || (String(ar.scheme || '').toLowerCase() === '5opt' ? AR_OPTIONS_5 : AR_OPTIONS_4);
    const card = mk('div', 'mby-card', null, [createUpperTab('ASSERTION — REASON')]);
    applySlideLayoutVars(card, resolveSlideLayout(slide, 'mcq'));
    if (!ctx.gradedStateMap.has(slide.id)) ctx.gradedStateMap.set(slide.id, { selectedCode: null, checked: false, isCorrect: null });
    const st = ctx.gradedStateMap.get(slide.id);

    const aText = mk('div', 'mby-ar-text'); aText.innerHTML = ar.assertionHtml; ctx.renderMathIn(aText);
    const rText = mk('div', 'mby-ar-text'); rText.innerHTML = ar.reasonHtml; ctx.renderMathIn(rText);
    const stack = mk('div', 'mby-ar-stack', null, [
      mk('div', 'mby-ar-box', null, [mk('div', 'mby-ar-label', 'Assertion (A)'), aText]),
      mk('div', 'mby-ar-box', null, [mk('div', 'mby-ar-label', 'Reason (R)'), rText])
    ]);

    const grid = mk('div', 'mby-options-grid');
    const optBtns = options.map(opt => {
      const btn = mk('button', 'mby-option-btn', opt.label); btn.type = 'button';
      if (st.selectedCode === opt.code) btn.classList.add('selected');
      btn.addEventListener('click', () => {
        st.selectedCode = opt.code;
        optBtns.forEach(b => b.classList.remove('selected'));
        btn.classList.add('selected');
      });
      grid.appendChild(btn);
      return btn;
    });

    const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
    const feedback = mk('span');
    const explanationBox = MBY.helpers.createExplanationBox(ar.explanationHtml, st.checked, ctx);
    const retryBtn = MBY.helpers.createRetryBtn(() => {
      st.selectedCode = null;
      st.checked = false;
      st.isCorrect = null;
      optBtns.forEach(b => b.classList.remove('selected', 'correct', 'wrong'));
      feedback.textContent = '';
      if (explanationBox) explanationBox.classList.add('hidden');
      retryBtn.classList.add('hidden');
      if (m) m.isCorrect = null;
      if (onAnswered) onAnswered();
      requestAnimationFrame(ctx.updateScrollDownHint);
    }, st.checked);

    checkBtn.addEventListener('click', () => {
      const isCorrect = st.selectedCode === ar.answer;
      optBtns.forEach((btn, idx) => {
        const code = options[idx].code;
        btn.classList.remove('correct', 'wrong');
        if (code === ar.answer) btn.classList.add('correct');
        else if (code === st.selectedCode) btn.classList.add('wrong');
      });
      st.checked = true; st.isCorrect = isCorrect;
      MBY.helpers.recordGradeAttempt(m, isCorrect);
      MBY.helpers.renderMcqFeedback(feedback, isCorrect);
      retryBtn.classList.remove('hidden');
      if (explanationBox) explanationBox.classList.remove('hidden');
      if (onAnswered) onAnswered();
      requestAnimationFrame(ctx.updateScrollDownHint);
    });

    card.appendChild(mk('div', 'mby-card-body', null, [stack, grid, mk('div', 'mby-action-row', null, [checkBtn, retryBtn, feedback]), explanationBox]));
    return card;
  }

  MBY.gradedKinds['assertion-reason'] = true;
  MBY.kindLabels['assertion-reason'] = 'Assertion–Reason';
  MBY.questions['assertion-reason'] = buildAssertionReasonCard;
  MBY.search['assertion-reason'] = (q, parts, ctx) => {
    if (!q.assertionReason) return;
    parts.push(ctx.htmlToPlainText(q.assertionReason.assertionHtml));
    parts.push(ctx.htmlToPlainText(q.assertionReason.reasonHtml));
    if (q.assertionReason.explanationHtml) parts.push(ctx.htmlToPlainText(q.assertionReason.explanationHtml));
  };
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    internal const string FlashcardFlipJs = """
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

    private const string FlashcardCss = """
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
.mby-flashcard-side-label { text-align: center; padding: 6px 16px 16px; font-size: 12px; font-weight: 600; color: var(--gray-label); }
""";

    private static string BuildFlashcardJs() => $$"""
(function (MBY) {
{{FlashcardFlipJs}}
  function renderFlashcardSide(side, faceLabel, slideMetric, fcState, ctx, onRated) {
    const body = mk('div', 'mby-flashcard-body');
    const txt = side.textHtml ? (() => { const d = mk('div'); d.innerHTML = side.textHtml; ctx.renderMathIn(d); return d; })() : null;
    const img = side.imagePath ? ctx.makeZoomImg(side.imagePath) : null;
    if (side.position === 'aboveImage') { if (txt) body.appendChild(txt); if (img) body.appendChild(img); }
    else { if (img) body.appendChild(img); if (txt) body.appendChild(txt); }

    if (faceLabel === 'Back') {
      const knownBtn = mk('button', 'mby-rate-btn' + (fcState.rating === 'known' ? ' active-known' : ''), 'Got It'); knownBtn.type = 'button';
      const reviewBtn = mk('button', 'mby-rate-btn' + (fcState.rating === 'needsReview' ? ' active-review' : ''), 'Needs Review'); reviewBtn.type = 'button';
      const createRetry = MBY.helpers.createRetryBtn || ((onRetry, visible, label = 'Reset', compact = true) => {
        const b = mk('button', 'mby-retry-btn' + (compact ? ' is-compact' : '') + (visible ? '' : ' hidden'), label);
        b.type = 'button';
        b.addEventListener('click', () => { if (onRetry) onRetry(); });
        return b;
      });
      const resetBtn = createRetry(() => {
        fcState.rating = null;
        if (slideMetric) slideMetric.flashcardRating = null;
        knownBtn.classList.remove('active-known');
        reviewBtn.classList.remove('active-review');
        resetBtn.classList.add('hidden');
        if (onRated) onRated();
      }, !!fcState.rating, 'Reset', true);
      const rate = r => {
        fcState.rating = r; if (slideMetric) slideMetric.flashcardRating = r;
        knownBtn.classList.toggle('active-known', r === 'known');
        reviewBtn.classList.toggle('active-review', r === 'needsReview');
        resetBtn.classList.remove('hidden');
        if (onRated) onRated();
      };
      knownBtn.addEventListener('click', e => { e.stopPropagation(); rate('known'); });
      reviewBtn.addEventListener('click', e => { e.stopPropagation(); rate('needsReview'); });
      resetBtn.addEventListener('click', e => e.stopPropagation());
      body.appendChild(mk('div', 'mby-rating-row', null, [knownBtn, reviewBtn, resetBtn]));
    }

    return mk('div', 'mby-flashcard-face ' + (faceLabel === 'Back' ? 'mby-flashcard-back' : ''), null,
      [createUpperTab('FLASHCARD'), body, mk('div', 'mby-flashcard-side-label', faceLabel)]);
  }

  function buildFlashcardPanel(slide, ctx, onRated, onFlipStart, onFlipEnd) {
    const slideMetric = ctx.metricById.get(slide.id);
    if (!ctx.flashcardStateMap.has(slide.id)) ctx.flashcardStateMap.set(slide.id, { flipped: false, rating: null });
    const fcState = ctx.flashcardStateMap.get(slide.id);
    const resolvedLayout = resolveSlideLayout(slide, 'flashcard');
    const scene = mk('div', 'mby-flashcard-scene'); applySlideLayoutVars(scene, resolvedLayout);
    const flipMs = Math.max(50, parseInt(resolvedLayout?.flipDurationMs || '450', 10) || 450);
    const frontFace = renderFlashcardSide(slide.flashcard.front, 'Front', slideMetric, fcState, ctx, onRated);
    const backFace = renderFlashcardSide(slide.flashcard.back, 'Back', slideMetric, fcState, ctx, onRated);
    frontFace.classList.toggle('is-active', !fcState.flipped); backFace.classList.toggle('is-active', fcState.flipped);
    const inner = mk('div', 'mby-flashcard-inner', null, [frontFace, backFace]);
    const triggerFlip = attachFlashcardFlip(scene, inner, frontFace, backFace, flipMs, () => fcState.flipped, v => {
      fcState.flipped = v; if (slideMetric) slideMetric.flipCount = (slideMetric.flipCount || 0) + 1;
    }, onFlipStart, () => { if (onFlipEnd) onFlipEnd(); requestAnimationFrame(ctx.updateScrollDownHint); });
    scene.addEventListener('click', triggerFlip);
    scene.appendChild(inner);
    return scene;
  }

  MBY.kindLabels['flashcard'] = 'Flashcard';
  MBY.questions['flashcard'] = buildFlashcardPanel;
  MBY.search['flashcard'] = (q, parts, ctx) => {
    if (!q.flashcard) return;
    parts.push(ctx.htmlToPlainText(q.flashcard.front.textHtml));
    parts.push(ctx.htmlToPlainText(q.flashcard.back.textHtml));
  };
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    private const string NumericCss = """
.mby-numeric-row { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.mby-numeric-input { width: 170px; max-width: 100%; padding: 9px 12px; border-radius: 10px; border: 1.5px solid var(--border); background: var(--surface); color: var(--text); font: inherit; font-size: 15px; font-weight: 650; }
.mby-numeric-input:focus { outline: none; border-color: var(--select); }
.mby-numeric-input.correct { border-color: var(--ok); background: rgba(25, 135, 84, 0.12); }
.mby-numeric-input.wrong { border-color: var(--bad); background: rgba(220, 53, 69, 0.12); }
.mby-numeric-unit { padding: 4px 10px; border-radius: 999px; background: var(--bg); border: 1px solid var(--border); font-size: 13px; font-weight: 700; color: var(--muted); }
.mby-numeric-range { font-size: 13px; color: var(--muted); font-weight: 600; }
""";

    private const string NumericJs = """
(function (MBY) {
  function buildNumericCard(slide, ctx, onAnswered) {
    const num = slide.numeric;
    const card = mk('div', 'mby-card', null, [createUpperTab('NUMERIC')]); applySlideLayoutVars(card, resolveSlideLayout(slide, 'mcq'));
    const st = ctx.gradedStateMap.get(slide.id) || { value: '', checked: false, isCorrect: null };
    ctx.gradedStateMap.set(slide.id, st);

    const body = mk('div', 'mby-card-body');
    if (num.imagePath) body.appendChild(ctx.makeZoomImg(num.imagePath, 'Question diagram'));
    const promptEl = mk('div', 'mby-prompt');
    promptEl.innerHTML = num.promptHtml || '';
    ctx.renderMathIn(promptEl);
    body.appendChild(promptEl);

    const input = mk('input', 'mby-numeric-input');
    input.type = 'text';
    input.inputMode = 'decimal';
    input.placeholder = num.placeholder || 'Enter numeric value...';
    input.value = st.value || '';

    const rowChildren = [input];
    if (num.unit) rowChildren.push(mk('span', 'mby-numeric-unit', num.unit));
    const rangeEl = mk('span', 'mby-numeric-range');
    rowChildren.push(rangeEl);
    body.appendChild(mk('div', 'mby-numeric-row', null, rowChildren));

    const checkBtn = mk('button', 'mby-check-btn', 'Check Answer'); checkBtn.type = 'button';
    const feedback = mk('span');
    const explanationBox = MBY.helpers.createExplanationBox(num.explanationHtml, st.checked, ctx);

    const tolRadius = num.toleranceMode === 'percent' ? Math.abs(num.answer * (num.tolerance / 100)) : Math.abs(num.tolerance);

    function syncVisual() {
      input.classList.remove('correct', 'wrong');
      rangeEl.textContent = '';
      if (st.checked) {
        input.classList.add(st.isCorrect ? 'correct' : 'wrong');
        const unitTxt = num.unit ? ` ${num.unit}` : '';
        if (tolRadius > 0) rangeEl.textContent = `Accepted: ${(num.answer - tolRadius).toPrecision(4)} .. ${(num.answer + tolRadius).toPrecision(4)}${unitTxt}`;
        else rangeEl.textContent = `Exact answer: ${num.answer}${unitTxt}`;
        MBY.helpers.setFeedbackMessage(feedback, st.isCorrect ? 'Correct!' : 'Out of accepted range.', st.isCorrect);
        if (explanationBox) explanationBox.classList.remove('hidden');
      } else {
        feedback.textContent = '';
        if (explanationBox) explanationBox.classList.add('hidden');
      }
      retryBtn.classList.toggle('hidden', !st.checked);
    }

    const retryBtn = MBY.helpers.createRetryBtn(() => {
      st.value = ''; st.checked = false; st.isCorrect = null;
      input.value = '';
      syncVisual();
      const m = ctx.metricById.get(slide.id); if (m) m.isCorrect = null;
      if (onAnswered) onAnswered();
    }, st.checked);

    input.addEventListener('input', () => {
      st.value = input.value;
      if (st.checked) { st.checked = false; st.isCorrect = null; syncVisual(); }
    });
    function gradeNumeric() {
      const cleaned = (st.value || '').trim().replace(/,/g, '');
      const parsed = cleaned === '' ? NaN : Number(cleaned);
      const ok = Number.isFinite(parsed) && Math.abs(parsed - num.answer) <= tolRadius + 1e-9;
      st.checked = true;
      st.isCorrect = ok;
      MBY.helpers.recordGradeAttempt(ctx.metricById.get(slide.id), ok);
      syncVisual();
      if (onAnswered) onAnswered();
    }
    input.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); gradeNumeric(); } });
    checkBtn.addEventListener('click', gradeNumeric);

    syncVisual();
    body.appendChild(mk('div', 'mby-action-row', null, [checkBtn, retryBtn, feedback]));
    if (explanationBox) body.appendChild(explanationBox);
    card.appendChild(body);
    return card;
  }

  MBY.gradedKinds['numeric'] = true;
  MBY.kindLabels['numeric'] = 'Numeric';
  MBY.questions['numeric'] = buildNumericCard;
  MBY.search['numeric'] = (q, parts, ctx) => {
    if (!q.numeric) return;
    parts.push(ctx.htmlToPlainText(q.numeric.promptHtml));
    if (q.numeric.unit) parts.push(q.numeric.unit);
    if (q.numeric.explanationHtml) parts.push(ctx.htmlToPlainText(q.numeric.explanationHtml));
  };
})(window.MBY = window.MBY || { blocks: {}, questions: {}, helpers: {}, search: {}, blockSearch: {}, gradedKinds: {}, kindLabels: {} });
""";

    public static IReadOnlyList<WebComponentModule> Modules { get; } =
    [
        new("choice-grid", "questions", f => f.HasChoiceGrid, ChoiceGridCss, null),
        new("mcq", "questions", f => f.HasMcq, null, McqJs),
        new("assertion-reason", "questions", f => f.HasAssertionReason, AssertionReasonCss, AssertionReasonJs),
        new("flashcard", "questions", f => f.HasFlashcard, FlashcardCss, BuildFlashcardJs()),
        new("numeric", "questions", f => f.HasNumeric, NumericCss, NumericJs)
    ];
}
